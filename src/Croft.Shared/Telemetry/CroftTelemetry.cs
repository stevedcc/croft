using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Croft.Shared.Telemetry;

public enum CroftApp
{
    Api,
    Web,
}

/// <summary>
/// The one telemetry setup both apps call. With OTEL_EXPORTER_OTLP_ENDPOINT set, the app exports
/// metrics, traces and logs over OTLP to the owner's collector, using the standard OTEL_* variables
/// for everything else (resource attributes, sampling, exporter settings). Without the variable
/// nothing is registered at all and the app behaves exactly as it did before telemetry existed.
/// Health probes are never traced, and every exported attribute passes the privacy scrub in
/// <see cref="TelemetryPrivacy"/> before any exporter sees it.
/// </summary>
public static class CroftTelemetry
{
    private const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    // WORKAROUND(W-035, OpenTelemetry): framework meters are subscribed by name; a new .NET meter needs adding by hand
    /// <summary>Meters every app subscribes: the framework's HTTP surface, HttpClient and Npgsql.</summary>
    private static readonly string[] CommonMeters =
    [
        "Microsoft.AspNetCore.Diagnostics",
        "Microsoft.AspNetCore.HeaderParsing",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Http",
        "Microsoft.AspNetCore.RateLimiting",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Npgsql",
        "System.Net.Http",
    ];
    // END WORKAROUND(W-035)

    // WORKAROUND(W-035, Blazor): the .NET 10 Blazor and SignalR meters are subscribed by name; a new .NET meter needs adding by hand
    /// <summary>The .NET 10 Blazor and SignalR meters, Web only.</summary>
    private static readonly string[] WebMeters =
    [
        "Microsoft.AspNetCore.Components",
        "Microsoft.AspNetCore.Components.Lifecycle",
        "Microsoft.AspNetCore.Components.Server.Circuits",
        "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.SignalR",
    ];
    // END WORKAROUND(W-035)

    public static void AddCroftTelemetry(this IHostApplicationBuilder builder, CroftApp app)
    {
        // Off unless configured: no exporter, no providers, no behaviour change.
        if (string.IsNullOrEmpty(builder.Configuration[OtlpEndpointVariable]))
        {
            return;
        }

        var (defaultServiceName, appMeter) = app switch
        {
            CroftApp.Web => ("croft-web", "Croft.Web"),
            _ => ("croft-api", "Croft.Api"),
        };

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
                ConfigureResource(resource, builder.Configuration, builder.Environment.ApplicationName, defaultServiceName))
            .WithTracing(tracing => tracing
                .AddSource(Warmup.CroftActivity.SourceName)
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !IsHealthPath(context.Request.Path.Value))
                .AddHttpClientInstrumentation(ConfigureHttpClientInstrumentation)
                .AddNpgsql()
                .AddProcessor(new ActivityPrivacyScrubber())
                .AddOtlpExporter())
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddNpgsqlInstrumentation(_ => { })
                    .AddMeter(appMeter)
                    .AddMeter(CommonMeters);

                if (app == CroftApp.Web)
                {
                    metrics.AddMeter(WebMeters);
                }

                metrics.AddOtlpExporter();
            });

        // The console logger stays as it is; this adds the OTLP pipeline beside it, carrying the
        // structured fields ([LoggerMessage] parameters) as attributes and the active trace ids.
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.AddProcessor(new LogRecordPrivacyScrubber());
            options.AddOtlpExporter();
        });
    }

    /// <summary>
    /// The HttpClient span setup: health probes are not traced (<see cref="IsHealthPath"/>), and a request flagged with
    /// <see cref="TelemetryRedaction.HostOnly"/> (an avatar download) exports its host alone.
    /// </summary>
    public static void ConfigureHttpClientInstrumentation(OpenTelemetry.Instrumentation.Http.HttpClientTraceInstrumentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.FilterHttpRequestMessage = request => !IsHealthPath(request.RequestUri?.AbsolutePath);
        options.EnrichWithHttpRequestMessage = TelemetryRedaction.Apply;
    }

    /// <summary>The meter names the setup subscribes, for tests that compare against what the
    /// framework actually emits.</summary>
    public static IReadOnlyList<string> SubscribedFrameworkMeters(CroftApp app)
        => app == CroftApp.Web ? [.. CommonMeters, .. WebMeters] : CommonMeters;

    // Probes every few seconds would drown everything else, and they carry no user traffic.
    private static bool IsHealthPath(string? path)
        => path is not null && path.StartsWith("/health", StringComparison.OrdinalIgnoreCase);

    private static void ConfigureResource(
        ResourceBuilder resource,
        IConfiguration configuration,
        string applicationName,
        string defaultServiceName)
    {
        var extra = new List<KeyValuePair<string, object?>>();
        if (configuration["OTEL_RESOURCE_ATTRIBUTES"] is { Length: > 0 } raw)
        {
            foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator > 0)
                {
                    extra.Add(new(pair[..separator], pair[(separator + 1)..]));
                }
            }
        }

        // Standard precedence: service.name from OTEL_RESOURCE_ATTRIBUTES, then OTEL_SERVICE_NAME.
        var serviceName = defaultServiceName;
        if (extra.FirstOrDefault(t => t.Key == "service.name").Value is string fromAttributes && fromAttributes.Length > 0)
        {
            serviceName = fromAttributes;
        }

        if (configuration["OTEL_SERVICE_NAME"] is { Length: > 0 } fromVariable)
        {
            serviceName = fromVariable;
        }

        resource
            .AddService(serviceName, serviceVersion: ResolveServiceVersion(applicationName))
            .AddAttributes(extra
                .Where(t => t.Key != "service.name")
                .Select(t => new KeyValuePair<string, object>(t.Key, t.Value!)));
    }

    private static string ResolveServiceVersion(string applicationName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => candidate.GetName().Name == applicationName)
            ?? Assembly.GetEntryAssembly();
        return assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
    }
}
