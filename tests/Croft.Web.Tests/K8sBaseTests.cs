using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using YamlDotNet.RepresentationModel;

namespace Croft.Web.Tests;

/// <summary>
/// Pins the contract of the kustomize base in <c>k8s/</c>: plain Kubernetes only, placeholders
/// instead of site values, and the configuration keys the apps read. Site facts (registries,
/// providers, storage classes, operators) belong to a site's overlay; these tests are what keeps
/// them from drifting back in.
/// </summary>
public partial class K8sBaseTests
{
    [Fact]
    public void Kustomization_ListsEveryManifest_AndEveryListedFileExists()
    {
        var kustomization = DocsIn(Path.Combine(K8sDir(), "kustomization.yaml")).Single().Root;

        StrAt(kustomization, "apiVersion").Should().Be("kustomize.config.k8s.io/v1beta1");
        var listed = SeqAt(kustomization, "resources").OfType<string>().ToList();
        var present = Directory.GetFiles(K8sDir(), "*.yaml")
            .Select(f => Path.GetFileName(f)!)
            .Where(f => f != "kustomization.yaml")
            .Order()
            .ToList();

        listed.Order().Should().BeEquivalentTo(present, "the kustomization lists every manifest in k8s/");
        foreach (var file in listed)
        {
            File.Exists(Path.Combine(K8sDir(), file)).Should().BeTrue($"'{file}' is listed in resources");
        }
    }

    [Fact]
    public void EveryManifest_UsesOnlyPlainKubernetesApiVersions()
    {
        // policy/v1: the PodDisruptionBudgets.
        string[] allowed = ["v1", "apps/v1", "networking.k8s.io/v1", "policy/v1"];

        foreach (var doc in Manifests())
        {
            StrAt(doc.Root, "apiVersion").Should().BeOneOf(allowed,
                $"{doc.File} must stay plain Kubernetes; CRDs are the site overlay's business");
        }
    }

    [Fact]
    public void NoManifest_SetsStorageClassName()
    {
        foreach (var doc in Manifests())
        {
            AllKeys(doc.Root).Should().NotContain("storageClassName",
                $"{doc.File} must leave storage to the cluster's default class");
        }
    }

    [Fact]
    public void Ingress_IsPlain_AndRoutesBlogExampleComToTheWebService()
    {
        var ingress = Manifests().Where(d => StrAt(d.Root, "kind") == "Ingress").ToList();
        var doc = ingress.Should().ContainSingle("the base ships exactly one plain Ingress").Subject;

        MapAt(doc.Root, "metadata").Should().NotContainKey("annotations");
        var spec = MapAt(doc.Root, "spec");
        spec.Should().NotContainKey("ingressClassName", "the cluster's default ingress class applies");
        spec.Should().NotContainKey("tls", "TLS is the site overlay's business");

        var rule = AsItems(SeqAt(spec, "rules")).Single();
        StrAt(rule, "host").Should().Be("blog.example.com", "the base carries only a placeholder host");
        var path = AsItems(SeqAt(MapAt(rule, "http"), "paths")).Single();
        StrAt(path, "path").Should().Be("/");
        StrAt(path, "pathType").Should().Be("Prefix");
        var service = MapAt(MapAt(path, "backend"), "service");
        StrAt(service, "name").Should().Be("blog-web");
        StrAt(MapAt(service, "port"), "number").Should().Be("80");
    }

    [Fact]
    public void NoFileUnderK8s_MentionsAuthentik()
    {
        // A concrete provider name is as out of place in the base as a concrete site: an
        // overlay picks both.
        foreach (var file in Directory.GetFiles(K8sDir(), "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(K8sDir(), file);
            var text = File.ReadAllText(file).ToLowerInvariant();

            text.Should().NotContain("authentik", $"{name} must not name a concrete provider");
        }
    }

    [Fact]
    public void ConfigMapUrls_UseOnlyPlaceholderOrClusterHosts()
    {
        // Stronger than naming one forbidden site: every URL a site overlay may replace must
        // already be a placeholder (example.com / *.example.com) or an in-cluster name.
        foreach (var doc in Manifests().Where(d => StrAt(d.Root, "kind") == "ConfigMap"))
        {
            foreach (var (key, value) in MapAt(doc.Root, "data"))
            {
                foreach (Match url in Regex.Matches(value as string ?? string.Empty, @"https?://[^,\s]+"))
                {
                    var host = new Uri(url.Value).Host;
                    (host is "example.com"
                        || host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase)
                        || host.EndsWith(".svc.cluster.local", StringComparison.OrdinalIgnoreCase))
                        .Should().BeTrue(
                            $"{doc.File} {key} must point at a placeholder or in-cluster host, not '{url.Value}'");
                }
            }
        }
    }

    [Fact]
    public void EveryIngressHost_IsAPlaceholderHost()
    {
        foreach (var doc in Manifests().Where(d => StrAt(d.Root, "kind") == "Ingress"))
        {
            foreach (var rule in AsItems(SeqAt(MapAt(doc.Root, "spec"), "rules")))
            {
                var host = StrAt(rule, "host") ?? string.Empty;
                (host is "example.com" || host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase))
                    .Should().BeTrue($"the Ingress host '{host}' must be an example.com placeholder");
            }
        }
    }

    [Fact]
    public void EveryContainerImage_HasNoRegistryAndNoTag()
    {
        // No registry part and no tag: an overlay retags with images:. A bare name is what
        // leaves that decision to the site.
        foreach (var doc in Manifests())
        {
            foreach (var image in ImageValues(doc.Root))
            {
                image.Should().NotContain("/", $"{doc.File} image '{image}' must not name a registry");
                image.Should().NotContain(":", $"{doc.File} image '{image}' must not pin a tag");
            }
        }
    }

    [Fact]
    public void ApiMigrateInitContainer_RunsTheApisMigrateEntry_NotTheRawEfbundle()
    {
        var pod = PodSpec(Resource("Deployment", "blog-api"));
        var migrate = Named(AsItems(SeqAt(pod, "initContainers")), "migrate");

        SeqAt(migrate, "command").Select(c => c?.ToString()).Should().Equal(
            ["dotnet", "Croft.Api.dll"],
            "the migrate entry wraps Migrate in an advisory lock; the raw efbundle cannot");
        SeqAt(migrate, "args").Select(a => a?.ToString()).Should().Equal(
            ["migrate"],
            "the entry is the API's 'migrate' mode");
    }

    [Fact]
    public void WebMigrateInitContainer_RunsTheWebsMigrateEntry_NotTheRawEfbundle()
    {
        var pod = PodSpec(Resource("Deployment", "blog-web"));
        var migrate = Named(AsItems(SeqAt(pod, "initContainers")), "migrate");

        StrAt(migrate, "image").Should().Be("croft-web", "migrations run from the Web image");
        SeqAt(migrate, "command").Select(c => c?.ToString()).Should().Equal(
            ["dotnet", "Croft.Web.dll"],
            "the migrate entry wraps Migrate in an advisory lock; the raw efbundle cannot");
        SeqAt(migrate, "args").Select(a => a?.ToString()).Should().Equal(
            ["migrate"],
            "the entry is the Web app's 'migrate' mode");
    }

    [Fact]
    public void WebMigrateInitContainer_ReadsWebDb_FromTheCroftWebDbSecret()
    {
        var pod = PodSpec(Resource("Deployment", "blog-web"));
        var migrate = Named(AsItems(SeqAt(pod, "initContainers")), "migrate");
        var env = AsItems(SeqAt(migrate, "env"));

        var secretKeyRef = MapAt(MapAt(Named(env, "ConnectionStrings__WebDb"), "valueFrom"), "secretKeyRef");
        StrAt(secretKeyRef, "name").Should().Be("croft-web-db",
            "the Web database has its own Secret (k8s/secrets.example.md), created per site");
        StrAt(secretKeyRef, "key").Should().Be("connection-string");
    }

    [Fact]
    public void WebContainer_ReadsWebDb_FromTheSameCroftWebDbSecret()
    {
        var pod = PodSpec(Resource("Deployment", "blog-web"));
        var web = Named(AsItems(SeqAt(pod, "containers")), "web");
        var env = AsItems(SeqAt(web, "env"));

        var secretKeyRef = MapAt(MapAt(Named(env, "ConnectionStrings__WebDb"), "valueFrom"), "secretKeyRef");
        StrAt(secretKeyRef, "name").Should().Be("croft-web-db",
            "the app must read the same Web database its migrate entry migrated");
        StrAt(secretKeyRef, "key").Should().Be("connection-string");
    }

    [Fact]
    public void WebProbes_LiveOnHealthLive_ReadyOnHealthReady()
    {
        var web = Named(AsItems(SeqAt(PodSpec(Resource("Deployment", "blog-web")), "containers")), "web");

        var liveness = MapAt(web, "livenessProbe");
        StrAt(MapAt(liveness, "httpGet"), "path").Should().Be("/health/live",
            "liveness is the process alone: an API or database outage must never restart Web pods");
        StrAt(MapAt(liveness, "httpGet"), "port").Should().Be("8080");

        var readiness = MapAt(web, "readinessProbe");
        StrAt(MapAt(readiness, "httpGet"), "path").Should().Be("/health/ready",
            "readiness holds traffic back while the Web database or the API is unreachable");
        StrAt(MapAt(readiness, "httpGet"), "port").Should().Be("8080");
        StrAt(readiness, "timeoutSeconds").Should().Be("5",
            "the endpoint's bounded checks (2s each) must answer inside the probe's timeout");
    }

    [Fact]
    public void ApiProbes_LiveOnHealthLive_ReadyOnHealthReady()
    {
        // Brief 13c item 5: liveness is the process alone (a database outage must never restart API pods),
        // readiness asks the primary database (an API that cannot reach it takes no traffic).
        var api = Named(AsItems(SeqAt(PodSpec(Resource("Deployment", "blog-api")), "containers")), "api");

        var liveness = MapAt(api, "livenessProbe");
        StrAt(MapAt(liveness, "httpGet"), "path").Should().Be("/health/live");
        StrAt(MapAt(liveness, "httpGet"), "port").Should().Be("8080");

        var readiness = MapAt(api, "readinessProbe");
        StrAt(MapAt(readiness, "httpGet"), "path").Should().Be("/health/ready");
        StrAt(MapAt(readiness, "httpGet"), "port").Should().Be("8080");
        StrAt(readiness, "timeoutSeconds").Should().Be("5",
            "the readiness check is bounded at 2 s and must answer inside the probe's timeout (the kubelet default is 1 s)");
    }

    [Fact]
    public void WebPod_CarriesNoDataProtectionKeysVolume()
    {
        // Keys live in the Web database (D1): the base mounts no key ring, so both pods run on
        // any node and a rollout never needs the single-instance overlay's ReadWriteOnce volume.
        var pod = PodSpec(Resource("Deployment", "blog-web"));
        var web = Named(AsItems(SeqAt(pod, "containers")), "web");

        AsItems(SeqAt(pod, "volumes")).Select(v => StrAt(v, "name"))
            .Should().NotContain("keys", "the base stores the Data Protection keys in the WebDb");
        AsItems(SeqAt(web, "volumeMounts")).Select(m => StrAt(m, "name"))
            .Should().NotContain("keys", "no mount without a key-ring volume");
        AsItems(SeqAt(web, "env")).Select(e => StrAt(e, "name"))
            .Should().NotContain("DataProtection__KeysPath",
                "file keys are the single-instance overlay's choice, not the base's");
    }

    [Fact]
    public void Base_CarriesNoBlogWebKeysClaim()
    {
        Manifests().Where(d => StrAt(d.Root, "kind") == "PersistentVolumeClaim")
            .Select(d => StrAt(MapAt(d.Root, "metadata"), "name"))
            .Should().NotContain("blog-web-keys",
                "the claim exists only for the single-instance overlay's file key ring");
    }

    [Fact]
    public void Readme_SingleInstanceOverlay_PutsBackFileKeys_AndDropsWebDb()
    {
        // The overlay is the one no-WebDb shape, so it must be complete and real: file keys (the
        // claim and DataProtection__KeysPath), one replica, ReadWriteOnce volumes, Recreate —
        // and no croft-web-db Secret requirement (the WebDb env and the migrate init container
        // are deleted from the patch).
        var readme = File.ReadAllText(Path.Combine(K8sDir(), "README.md"));

        readme.Should().Contain("blog-web-keys", "the overlay puts the file key ring's claim back");
        readme.Should().Contain("DataProtection__KeysPath", "without a WebDb the keys are files again");
        readme.Should().Contain("ConnectionStrings__WebDb", "the overlay must say what happens to the WebDb env");
        readme.Should().Contain("$patch: delete", "the WebDb env and the migrate init container go, so no Secret is required");
        readme.Should().Contain("/spec/replicas", "the overlay sets one replica");
        readme.Should().Contain("ReadWriteOnce", "one pod mounts the volumes");
        readme.Should().Contain("type: Recreate", "ReadWriteOnce volumes allow no rolling update");
    }

    [Fact]
    public void BothDeployments_SetOtelServiceName_AndPodAttributesFromTheDownwardApi()
    {
        var web = Named(AsItems(SeqAt(PodSpec(Resource("Deployment", "blog-web")), "containers")), "web");
        var api = Named(AsItems(SeqAt(PodSpec(Resource("Deployment", "blog-api")), "containers")), "api");

        StrAt(Named(AsItems(SeqAt(web, "env")), "OTEL_SERVICE_NAME"), "value").Should().Be("croft-web",
            "the telemetry resource's service.name defaults per app");
        StrAt(Named(AsItems(SeqAt(api, "env")), "OTEL_SERVICE_NAME"), "value").Should().Be("croft-api",
            "the telemetry resource's service.name defaults per app");

        foreach (var (container, app) in new[] { (web, "web"), (api, "api") })
        {
            var env = AsItems(SeqAt(container, "env"));
            StrAt(Named(env, "OTEL_RESOURCE_ATTRIBUTES"), "value").Should().Be(
                "k8s.pod.name=$(POD_NAME),k8s.namespace.name=$(POD_NAMESPACE)",
                $"{app} carries the pod and namespace identity from the downward API");
            StrAt(MapAt(MapAt(Named(env, "POD_NAME"), "valueFrom"), "fieldRef"), "fieldPath").Should().Be(
                "metadata.name", "the pod name is the downward API's, not a hardcoded value");
            StrAt(MapAt(MapAt(Named(env, "POD_NAMESPACE"), "valueFrom"), "fieldRef"), "fieldPath").Should().Be(
                "metadata.namespace", "the namespace is the downward API's, not a hardcoded value");
        }
    }

    [Fact]
    public void EveryContainer_RunsWithoutPrivileges_OnAReadOnlyRoot_AndTmpIsAnEmptyDir()
    {
        foreach (var deployment in new[] { "blog-web", "blog-api" })
        {
            var pod = PodSpec(Resource("Deployment", deployment));
            var containers = AsItems(SeqAt(pod, "containers")).Concat(AsItems(SeqAt(pod, "initContainers"))).ToList();
            containers.Should().HaveCount(2, "each deployment has its app container and the migrate init container");

            foreach (var container in containers)
            {
                var name = $"{deployment}/{StrAt(container, "name")}";
                var context = MapAt(container, "securityContext");
                StrAt(context, "allowPrivilegeEscalation").Should().Be("false", $"{name} must not escalate privileges");
                StrAt(context, "readOnlyRootFilesystem").Should().Be("true", $"{name} must run on a read-only root filesystem");
                SeqAt(MapAt(context, "capabilities"), "drop").OfType<string>().Should().Equal(["ALL"], $"{name} must drop every capability");
                StrAt(Named(AsItems(SeqAt(container, "volumeMounts")), "tmp"), "mountPath").Should().Be("/tmp",
                    $"{name} needs /tmp as its one writable scratch path");
            }

            StrAt(MapAt(MapAt(pod, "securityContext"), "seccompProfile"), "type").Should().Be("RuntimeDefault", deployment);
            StrAt(pod, "automountServiceAccountToken").Should().Be("false",
                $"{deployment} never calls the Kubernetes API, so it gets no token");
            var tmp = Named(AsItems(SeqAt(pod, "volumes")), "tmp");
            MapAt(tmp, "emptyDir").Should().NotBeNull($"{deployment}'s /tmp is an emptyDir");
        }
    }

    [Fact]
    public void BothDeployments_SetDnsConfigNdotsToTwo()
    {
        foreach (var deployment in new[] { "blog-web", "blog-api" })
        {
            var pod = PodSpec(Resource("Deployment", deployment));
            var options = AsItems(SeqAt(MapAt(pod, "dnsConfig"), "options"));
            StrAt(Named(options, "ndots"), "value").Should().Be("2",
                $"{deployment} must resolve the fully-qualified service names in one query " +
                "instead of walking the cluster's search list first (the 2026-10-14 DNS stalls)");
        }
    }

    [Fact]
    public void Base_SetsNoOtlpEndpoint_TheReadmeSaysHowSitesAddIt()
    {
        foreach (var deployment in new[] { "blog-web", "blog-api" })
        {
            var pod = PodSpec(Resource("Deployment", deployment));
            var containers = AsItems(SeqAt(pod, "containers")).Concat(AsItems(SeqAt(pod, "initContainers")));
            foreach (var container in containers)
            {
                AsItems(SeqAt(container, "env")).Select(e => StrAt(e, "name"))
                    .Should().NotContain("OTEL_EXPORTER_OTLP_ENDPOINT",
                        $"{deployment} must not assume a collector: sites add the endpoint, the base only prepares");
            }
        }

        var readme = File.ReadAllText(Path.Combine(K8sDir(), "README.md"));
        readme.Should().Contain("OTEL_EXPORTER_OTLP_ENDPOINT",
            "the README must say how a site turns telemetry on");
        readme.Should().Contain("http://otel-collector.observability:4317",
            "the README shows the endpoint a site's collector lives at");
    }

    [Fact]
    public void Readme_TelemetrySection_ExplainsExportsVariablesAndThePrivacyRule()
    {
        var readme = File.ReadAllText(Path.Combine(K8sDir(), "README.md"));

        readme.Should().Contain("Telemetry", "the README gains a Telemetry section");
        readme.Should().Contain("metrics").And.Contain("traces").And.Contain("logs",
            "the section says what is exported");
        readme.Should().Contain("OTEL_SERVICE_NAME").And.Contain("OTEL_RESOURCE_ATTRIBUTES",
            "the section lists the variables the base sets");
        readme.Should().Contain("client IP").And.Contain("token").And.Contain("cookie")
            .And.Contain("email").And.Contain("query-string",
                "the section carries the privacy rule: none of these ever reach the collector");
    }

    [Fact]
    public void ApiAndMigrateContainers_BuildTheConnectionString_FromTheBlogDbSecretKeys()
    {
        var pod = PodSpec(Resource("Deployment", "blog-api"));
        var migrate = Named(AsItems(SeqAt(pod, "initContainers")), "migrate");
        var api = Named(AsItems(SeqAt(pod, "containers")), "api");

        foreach (var container in new[] { migrate, api })
        {
            var env = AsItems(SeqAt(container, "env"));
            var names = env.Select(e => StrAt(e, "name")).ToList();
            var connIndex = names.IndexOf("ConnectionStrings__BlogDb");

            foreach (var (variable, key) in new Dictionary<string, string>
            {
                ["DB_HOST"] = "host",
                ["DB_PORT"] = "port",
                ["DB_NAME"] = "dbname",
                ["DB_USER"] = "username",
                ["DB_PASSWORD"] = "password",
            })
            {
                var index = names.IndexOf(variable);
                index.Should().BeGreaterThanOrEqualTo(0, $"{variable} must be declared before ConnectionStrings__BlogDb");
                index.Should().BeLessThan(connIndex, "Kubernetes expands $(VAR) only from env entries declared above it");

                var secretKeyRef = MapAt(MapAt(Named(env, variable), "valueFrom"), "secretKeyRef");
                StrAt(secretKeyRef, "name").Should().Be("blog-db", "the base reads one Secret with the operator-agnostic keys");
                StrAt(secretKeyRef, "key").Should().Be(key);
            }

            StrAt(Named(env, "ConnectionStrings__BlogDb"), "value").Should().Be(
                "Host=$(DB_HOST);Port=$(DB_PORT);Database=$(DB_NAME);Username=$(DB_USER);Password=$(DB_PASSWORD)");
        }
    }

    [Fact]
    public void Web_BrandingVolume_IsOptional_AndMountedReadOnlyAtAppBranding()
    {
        var pod = PodSpec(Resource("Deployment", "blog-web"));

        var configMap = MapAt(Named(AsItems(SeqAt(pod, "volumes")), "branding"), "configMap");
        StrAt(configMap, "name").Should().Be("blog-branding");
        StrAt(configMap, "optional").Should().Be("true", "the base deploys alone with the default branding");

        var web = Named(AsItems(SeqAt(pod, "containers")), "web");
        var mount = Named(AsItems(SeqAt(web, "volumeMounts")), "branding");
        StrAt(mount, "mountPath").Should().Be("/app/branding", "the app's branding contract is the folder");
        StrAt(mount, "readOnly").Should().Be("true", "the app never writes its branding");
    }

    [Fact]
    public void Web_SiteFilesVolume_IsOptional_AndMountedReadOnlyAtAppSiteFiles()
    {
        var pod = PodSpec(Resource("Deployment", "blog-web"));

        var configMap = MapAt(Named(AsItems(SeqAt(pod, "volumes")), "site-files"), "configMap");
        StrAt(configMap, "name").Should().Be("blog-site-files");
        StrAt(configMap, "optional").Should().Be("true", "the base deploys alone with no site files");

        var web = Named(AsItems(SeqAt(pod, "containers")), "web");
        var mount = Named(AsItems(SeqAt(web, "volumeMounts")), "site-files");
        StrAt(mount, "mountPath").Should().Be("/app/site-files", "Blog:SiteFilesPath defaults to this folder");
        StrAt(mount, "readOnly").Should().Be("true", "the app never writes its site files");
    }

    [Fact]
    public void ConfigMaps_HoldTheContractsNonSecretKeys_ForEachApp()
    {
        // The expected sets pin each map completely. ASPNETCORE_ENVIRONMENT and ApiBaseUrl are
        // deployment plumbing the apps need at startup (Web's API client reads ApiBaseUrl), not
        // site-supplied contract keys; they keep today's values so an overlay changes only what it
        // means to. Everything else is the Oidc/BaseUrl contract; Oidc__ClientSecret and the
        // database keys are Secrets and must never appear here.
        string[] apiKeys =
        [
            "ASPNETCORE_ENVIRONMENT", "Oidc__Authority", "Oidc__Audience",
            "Oidc__RoleGroups__Admin", "Oidc__RoleGroups__Editor",
            "Warmup__Enabled", "Warmup__Timeout", "Warmup__RequestTimeout",
            "Blog__BaseUrl", "Social__Enabled", "Social__Mastodon__Server", "Social__Mastodon__Account", "Social__Bluesky__Did",
        ];
        string[] webKeys =
        [
            "ASPNETCORE_ENVIRONMENT", "ApiBaseUrl", "Oidc__Authority", "Oidc__ClientId",
            "Oidc__RoleGroups__Admin", "Oidc__RoleGroups__Editor", "Blog__BaseUrl",
            "ForwardedHeaders__KnownNetworks__0", "DataProtection__CertificatePath", "DataProtection__CertificateKeyPath",
            "Warmup__Enabled", "Warmup__Timeout", "Warmup__RequestTimeout",
            "Croft__Web__OutputCache__Duration", "Croft__Web__OutputCache__FeedDuration",
            "Croft__Web__OutputCache__SizeLimitMegabytes", "Croft__Web__OutputCache__MaximumBodyKilobytes",
            "Croft__Web__OutputCache__MediaMaximumBodyKilobytes",
        ];

        var apiData = MapAt(Resource("ConfigMap", "blog-api-config").Root, "data");
        apiData.Keys.Should().BeEquivalentTo(apiKeys, "the API reads exactly these non-secret keys");
        StrAt(apiData, "Oidc__Authority").Should().Be("https://idp.example.com/application/o/blog/");
        StrAt(apiData, "Oidc__Audience").Should().Be("your-client-id");
        StrAt(apiData, "Oidc__RoleGroups__Admin").Should().Be("Admin");
        StrAt(apiData, "Oidc__RoleGroups__Editor").Should().Be("Editor");
        AssertWarmupDefaults(apiData);
        StrAt(apiData, "Blog__BaseUrl").Should().Be("https://blog.example.com",
            "the User-Agent of the social clients names the site; an overlay patches it like the Web map's");
        // Brief 12a: social replies are off by default and name nobody: a site switches them on and fills in its own accounts.
        StrAt(apiData, "Social__Enabled").Should().Be("false");
        StrAt(apiData, "Social__Mastodon__Server").Should().Be("");
        StrAt(apiData, "Social__Mastodon__Account").Should().Be("");
        StrAt(apiData, "Social__Bluesky__Did").Should().Be("");

        var webData = MapAt(Resource("ConfigMap", "blog-web-config").Root, "data");
        webData.Keys.Should().BeEquivalentTo(webKeys, "Web reads exactly these non-secret keys");
        StrAt(webData, "Oidc__Authority").Should().Be("https://idp.example.com/application/o/blog/");
        StrAt(webData, "Oidc__ClientId").Should().Be("your-client-id");
        StrAt(webData, "Oidc__RoleGroups__Admin").Should().Be("Admin");
        StrAt(webData, "Oidc__RoleGroups__Editor").Should().Be("Editor");
        StrAt(webData, "Blog__BaseUrl").Should().Be("https://blog.example.com");
        StrAt(webData, "ForwardedHeaders__KnownNetworks__0").Should().Be("10.42.0.0/16",
            "only the k3s default pod network (where the ingress controller runs) may set a client address; the README says to change it");
        AssertWarmupDefaults(webData);
        AssertOutputCacheDefaults(webData);
    }

    /// <summary>Brief 14c item 6: the README's output-cache table lists the media setting beside the others.</summary>
    [Fact]
    public void TheReadmeTable_ListsEveryOutputCacheSetting()
    {
        var readme = File.ReadAllText(Path.Combine(K8sDir(), "README.md"));

        foreach (var setting in new[] { "Duration", "FeedDuration", "SizeLimitMegabytes", "MaximumBodyKilobytes", "MediaMaximumBodyKilobytes" })
        {
            readme.Should().Contain($"| `Croft__Web__OutputCache__{setting}` |", $"the README table names {setting}");
        }
    }

    /// <summary>Brief 14b: the Web map carries the output-cache settings at the code's own defaults (the
    /// README table says the same).</summary>
    private static void AssertOutputCacheDefaults(Dictionary<string, object?> data)
    {
        var defaults = Croft.Web.Options.OutputCacheSettings.Read(new ConfigurationBuilder().Build());
        TimeSpan.Parse(StrAt(data, "Croft__Web__OutputCache__Duration")!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(defaults.Duration);
        TimeSpan.Parse(StrAt(data, "Croft__Web__OutputCache__FeedDuration")!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(defaults.FeedDuration);
        StrAt(data, "Croft__Web__OutputCache__SizeLimitMegabytes").Should().Be(defaults.SizeLimitMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        StrAt(data, "Croft__Web__OutputCache__MaximumBodyKilobytes").Should().Be(defaults.MaximumBodyKilobytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        StrAt(data, "Croft__Web__OutputCache__MediaMaximumBodyKilobytes").Should().Be(defaults.MediaMaximumBodyKilobytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Brief 14a: both maps carry the warm-up settings at the code's own defaults (the
    /// README table says the same), so the manifest documents what runs without it.</summary>
    private static void AssertWarmupDefaults(Dictionary<string, object?> data)
    {
        var defaults = new Croft.Shared.Warmup.WarmupSettings();
        StrAt(data, "Warmup__Enabled").Should().Be(defaults.Enabled ? "true" : "false");
        TimeSpan.Parse(StrAt(data, "Warmup__Timeout")!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(defaults.Timeout);
        TimeSpan.Parse(StrAt(data, "Warmup__RequestTimeout")!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(defaults.RequestTimeout);
    }

    [Fact]
    public void Web_KeyRingCertificate_IsAnOptionalReadOnlySecretMount_TheSettingsPointAt()
    {
        var pod = PodSpec(Resource("Deployment", "blog-web"));

        var secret = MapAt(Named(AsItems(SeqAt(pod, "volumes")), "dataprotection-cert"), "secret");
        StrAt(secret, "secretName").Should().Be("croft-dataprotection-cert");
        StrAt(secret, "optional").Should().Be("true", "a site without the Secret still starts (with the unencrypted-key-ring Warning)");

        var web = Named(AsItems(SeqAt(pod, "containers")), "web");
        var mount = Named(AsItems(SeqAt(web, "volumeMounts")), "dataprotection-cert");
        StrAt(mount, "mountPath").Should().Be("/etc/croft/dataprotection");
        StrAt(mount, "readOnly").Should().Be("true");

        var data = MapAt(Resource("ConfigMap", "blog-web-config").Root, "data");
        StrAt(data, "DataProtection__CertificatePath").Should().Be("/etc/croft/dataprotection/tls.crt");
        StrAt(data, "DataProtection__CertificateKeyPath").Should().Be("/etc/croft/dataprotection/tls.key");
    }

    [Fact]
    public void NoContainer_EnablesForwardedHeadersByEnvironmentVariable_WhichTrustsEveryPeer()
    {
        foreach (var deployment in new[] { "blog-web", "blog-api" })
        {
            var pod = PodSpec(Resource("Deployment", deployment));
            foreach (var container in AsItems(SeqAt(pod, "containers")).Concat(AsItems(SeqAt(pod, "initContainers"))))
            {
                AsItems(SeqAt(container, "env")).Select(e => StrAt(e, "name")).Should().NotContain(
                    "ASPNETCORE_FORWARDEDHEADERS_ENABLED",
                    $"{deployment}/{StrAt(container, "name")}: the variable clears the trusted networks, so any peer could choose the client address");
            }
        }
    }

    [Fact]
    public void Deployments_UseOverridableImageNames()
    {
        var apiPod = PodSpec(Resource("Deployment", "blog-api"));
        var webPod = PodSpec(Resource("Deployment", "blog-web"));

        StrAt(Named(AsItems(SeqAt(apiPod, "containers")), "api"), "image").Should().Be("croft-api",
            "no registry and no tag: overlays retag with images:");
        StrAt(Named(AsItems(SeqAt(apiPod, "initContainers")), "migrate"), "image").Should().Be("croft-api",
            "migrations run from the API image");
        StrAt(Named(AsItems(SeqAt(webPod, "containers")), "web"), "image").Should().Be("croft-web",
            "no registry and no tag: overlays retag with images:");
    }

    [Fact]
    public void EveryResource_KeepsItsName_InTheBlogNamespace()
    {
        // Names are load-bearing: a site overlay changes nothing it doesn't mean to, and a
        // renamed resource is deleted and recreated at migration time.
        var resources = Manifests()
            .Select(doc => $"{StrAt(doc.Root, "kind")}/{StrAt(MapAt(doc.Root, "metadata"), "name")}")
            .Order()
            .ToList();
        resources.Should().BeEquivalentTo(
        [
            "ConfigMap/blog-api-config",
            "ConfigMap/blog-web-config",
            "Deployment/blog-api",
            "Deployment/blog-web",
            "Ingress/blog-web",
            "Namespace/blog",
            "PersistentVolumeClaim/blog-media-pvc",
            "PodDisruptionBudget/blog-api",
            "PodDisruptionBudget/blog-web",
            "Service/blog-api",
            "Service/blog-web",
        ]);

        foreach (var doc in Manifests().Where(d => StrAt(d.Root, "kind") != "Namespace"))
        {
            StrAt(MapAt(doc.Root, "metadata"), "namespace").Should().Be("blog",
                $"{doc.File} resources live in the blog namespace");
        }

        // The claims the Deployments point at: a rename silently adopts or recreates storage
        // at migration time. (The blog-db Secret name is pinned by the connection-string test;
        // the Web's Data Protection keys live in the Web database and have no claim in the
        // base — WebPod_CarriesNoDataProtectionKeysVolume.)
        var apiPod = PodSpec(Resource("Deployment", "blog-api"));
        StrAt(MapAt(Named(AsItems(SeqAt(apiPod, "volumes")), "media"), "persistentVolumeClaim"), "claimName")
            .Should().Be("blog-media-pvc");
    }

    [Fact]
    public void Deployments_RunTwoReplicas()
    {
        // Two of everything: losing one pod must not take the app down.
        string[] apps = ["blog-api", "blog-web"];

        foreach (var name in apps)
        {
            var spec = MapAt(Resource("Deployment", name).Root, "spec");

            StrAt(spec, "replicas").Should().Be("2", $"{name} runs two pods so one can fail or be drained");
        }
    }

    [Fact]
    public void Deployments_RollWithZeroUnavailableAndOneSurge()
    {
        // RollingUpdate: old pods keep serving until their replacements are ready.
        string[] apps = ["blog-api", "blog-web"];

        foreach (var name in apps)
        {
            var strategy = MapAt(MapAt(Resource("Deployment", name).Root, "spec"), "strategy");

            StrAt(strategy, "type").Should().Be("RollingUpdate", $"{name} rolls instead of stopping first");
            var rolling = MapAt(strategy, "rollingUpdate");
            StrAt(rolling, "maxUnavailable").Should().Be("0", "a rollout never removes a serving pod first");
            StrAt(rolling, "maxSurge").Should().Be("1", "one extra pod at a time");
        }
    }

    [Fact]
    public void Deployments_SpreadPodsAcrossHostnames_ButScheduleAnyway()
    {
        string[] apps = ["blog-api", "blog-web"];

        foreach (var name in apps)
        {
            var pod = PodSpec(Resource("Deployment", name));
            var spread = AsItems(SeqAt(pod, "topologySpreadConstraints"))
                .Should().ContainSingle($"{name} spreads its pods over nodes").Subject;

            StrAt(spread, "topologyKey").Should().Be("kubernetes.io/hostname", "the spread is over nodes");
            StrAt(spread, "whenUnsatisfiable").Should().Be("ScheduleAnyway",
                "a single-node cluster must still schedule the pods");
        }
    }

    [Fact]
    public void EachApp_HasAPodDisruptionBudget_KeepingOnePodAvailable()
    {
        string[] apps = ["blog-api", "blog-web"];

        foreach (var name in apps)
        {
            var spec = MapAt(Resource("PodDisruptionBudget", name).Root, "spec");

            StrAt(spec, "minAvailable").Should().Be("1", "voluntary evictions leave one pod serving");
            StrAt(MapAt(MapAt(spec, "selector"), "matchLabels"), "app").Should().Be(name,
                "the budget covers exactly this app's pods");
        }
    }

    [Fact]
    public void MediaVolume_IsReadWriteMany()
    {
        var media = MapAt(Resource("PersistentVolumeClaim", "blog-media-pvc").Root, "spec");

        SeqAt(media, "accessModes").Should().Equal(["ReadWriteMany"],
            "both API pods mount the uploads at once");
    }

    [Fact]
    public void Pods_GetFortySecondsToStop()
    {
        // The 10s preStop plus time to finish in-flight work before SIGKILL.
        string[] apps = ["blog-api", "blog-web"];

        foreach (var name in apps)
        {
            var pod = PodSpec(Resource("Deployment", name));

            StrAt(pod, "terminationGracePeriodSeconds").Should().Be("40", $"{name} drains before it is killed");
        }
    }

    [Fact]
    public void AppContainers_SleepTenSecondsInPreStop()
    {
        (string Deployment, string Container)[] apps = [("blog-api", "api"), ("blog-web", "web")];

        foreach (var (deployment, container) in apps)
        {
            var pod = PodSpec(Resource("Deployment", deployment));
            var preStop = MapAt(MapAt(Named(AsItems(SeqAt(pod, "containers")), container), "lifecycle"), "preStop");

            SeqAt(MapAt(preStop, "exec"), "command").Should().Equal(["sleep", "10"],
                $"{container} stays in Endpoints while the ingress notices the deletion");
        }
    }

    private sealed record Doc(string File, Dictionary<string, object?> Root);

    private static string K8sDir() => Path.Combine(RepoRoot(), "k8s");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && Directory.GetFiles(dir.FullName, "*.slnx").Length == 0)
        {
            dir = dir.Parent!;
        }

        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }

    /// <summary>Every document in every <c>k8s/*.yaml</c> except the kustomization itself.</summary>
    private static List<Doc> Manifests() =>
    [
        .. Directory.GetFiles(K8sDir(), "*.yaml")
            .Where(f => Path.GetFileName(f) != "kustomization.yaml")
            .Order()
            .SelectMany(DocsIn),
    ];

    private static List<Doc> DocsIn(string path)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(path));
        stream.Load(reader);
        return [.. stream.Documents.Select(d => new Doc(Path.GetFileName(path), AsMap(d.RootNode)))];
    }

    private static Doc Resource(string kind, string name) =>
        Manifests().Single(d => StrAt(d.Root, "kind") == kind && StrAt(MapAt(d.Root, "metadata"), "name") == name);

    private static Dictionary<string, object?> PodSpec(Doc deployment) =>
        MapAt(MapAt(MapAt(deployment.Root, "spec"), "template"), "spec");

    private static Dictionary<string, object?> AsMap(YamlNode node) =>
        ((YamlMappingNode)node).Children.ToDictionary(
            pair => ((YamlScalarNode)pair.Key).Value ?? string.Empty,
            pair => AsObject(pair.Value));

    private static object? AsObject(YamlNode node) => node switch
    {
        YamlMappingNode mapping => AsMap(mapping),
        YamlSequenceNode sequence => sequence.Children.Select(AsObject).ToList(),
        YamlScalarNode scalar => scalar.Value,
        _ => throw new InvalidOperationException($"Unsupported YAML node {node.GetType().Name}"),
    };

    private static IEnumerable<string> AllKeys(object? node)
    {
        switch (node)
        {
            case Dictionary<string, object?> map:
                foreach (var pair in map)
                {
                    yield return pair.Key;
                    foreach (var key in AllKeys(pair.Value))
                    {
                        yield return key;
                    }
                }

                break;
            case List<object?> list:
                foreach (var item in list)
                {
                    foreach (var key in AllKeys(item))
                    {
                        yield return key;
                    }
                }

                break;
        }
    }

    private static IEnumerable<string> ImageValues(object? node)
    {
        switch (node)
        {
            case Dictionary<string, object?> map:
                foreach (var pair in map)
                {
                    if (pair.Key is "containers" or "initContainers" or "ephemeralContainers" && pair.Value is List<object?> entries)
                    {
                        foreach (var image in entries.OfType<Dictionary<string, object?>>().Select(c => StrAt(c, "image")))
                        {
                            if (image is not null)
                            {
                                yield return image;
                            }
                        }
                    }

                    foreach (var nested in ImageValues(pair.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case List<object?> list:
                foreach (var item in list)
                {
                    foreach (var nested in ImageValues(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static Dictionary<string, object?> MapAt(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is Dictionary<string, object?> nested
            ? nested
            : throw new InvalidOperationException($"'{key}' is not a mapping");

    private static List<object?> SeqAt(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is List<object?> list
            ? list
            : throw new InvalidOperationException($"'{key}' is not a YAML sequence");

    private static string? StrAt(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value as string : null;

    private static List<Dictionary<string, object?>> AsItems(List<object?> list) =>
        [.. list.OfType<Dictionary<string, object?>>()];

    private static Dictionary<string, object?> Named(List<Dictionary<string, object?>> items, string name) =>
        items.SingleOrDefault(item => StrAt(item, "name") == name)
            ?? throw new InvalidOperationException($"no entry named '{name}'");
}
