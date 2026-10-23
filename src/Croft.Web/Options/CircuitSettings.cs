using System.Globalization;

namespace Croft.Web.Options;

/// <summary>
/// The <c>Croft:Web:Circuits:*</c> limits. A disconnected circuit is pure cost — a reload never
/// reconnects to its old circuit — so the defaults keep few of them, briefly. An invalid value
/// (non-integer, negative) is a deployment error and fails startup naming the setting.
/// </summary>
public sealed record CircuitSettings(
    int DisconnectedMaxRetained,
    int DisconnectedRetentionSeconds,
    int MaxActive)
{
    // WORKAROUND(W-052, Blazor): Blazor's defaults (100 circuits, 3 minutes) OOM-killed the pods under reload churn; 20 circuits for 60 s was measured
    public const int DefaultDisconnectedMaxRetained = 20;
    public const int DefaultDisconnectedRetentionSeconds = 60;
    // END WORKAROUND(W-052)

    /// <summary>0 means no limit; any other value is the circuit ceiling for this process.</summary>
    public const int DefaultMaxActive = 400;

    /// <summary>Parses the three settings, applying the documented defaults to unset values.</summary>
    public static CircuitSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(
            ReadNonNegative(configuration, "Croft:Web:Circuits:DisconnectedMaxRetained", DefaultDisconnectedMaxRetained),
            ReadNonNegative(configuration, "Croft:Web:Circuits:DisconnectedRetentionSeconds", DefaultDisconnectedRetentionSeconds),
            ReadNonNegative(configuration, "Croft:Web:Circuits:MaxActive", DefaultMaxActive));
    }

    private static int ReadNonNegative(IConfiguration configuration, string setting, int defaultValue)
    {
        var raw = configuration[setting];
        if (raw is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new InvalidOperationException($"{setting} must be an integer >= 0, got '{raw}'.");
        }

        return value;
    }
}
