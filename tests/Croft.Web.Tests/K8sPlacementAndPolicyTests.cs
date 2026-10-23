using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// Placement and network rules of the kustomize base: the four Croft pods prefer different nodes
/// (a preference, never a requirement, so rollouts and node outages still schedule), and the
/// optional network-policy component lets only the ingress reach Web and only Web and the
/// ingress reach the API. Both select on app.kubernetes.io labels, which survive a site's renames.
/// </summary>
public partial class K8sBaseTests
{
    [Theory]
    [InlineData("blog-web", "web")]
    [InlineData("blog-api", "api")]
    public void CroftPods_CarryPartOfAndComponentLabels(string deployment, string component)
    {
        var labels = MapAt(MapAt(MapAt(MapAt(Resource("Deployment", deployment).Root, "spec"), "template"), "metadata"), "labels");

        StrAt(labels, "app.kubernetes.io/part-of").Should().Be("croft");
        StrAt(labels, "app.kubernetes.io/component").Should().Be(component);
    }

    [Theory]
    [InlineData("blog-web")]
    [InlineData("blog-api")]
    public void CroftPods_PreferNodesWithoutAnotherCroftPod_AsAPreferenceOnly(string deployment)
    {
        var antiAffinity = MapAt(MapAt(PodSpec(Resource("Deployment", deployment)), "affinity"), "podAntiAffinity");

        antiAffinity.Should().NotContainKey("requiredDuringSchedulingIgnoredDuringExecution",
            "a hard rule would leave the surge pod of a rollout, or a pod of a failed node, unschedulable");
        var term = AsItems(SeqAt(antiAffinity, "preferredDuringSchedulingIgnoredDuringExecution")).Single();
        StrAt(term, "weight").Should().Be("100");
        var podTerm = MapAt(term, "podAffinityTerm");
        StrAt(podTerm, "topologyKey").Should().Be("kubernetes.io/hostname");
        var matchLabels = MapAt(MapAt(podTerm, "labelSelector"), "matchLabels");
        matchLabels.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, object?>("app.kubernetes.io/part-of", "croft"),
                "Web and API pods avoid each other, not only their own kind");
    }

    [Fact]
    public void NetworkPolicyComponent_IsOptional_AndListsItsFiles()
    {
        var dir = Path.Combine(K8sDir(), "components", "network-policies");
        var kustomization = DocsIn(Path.Combine(dir, "kustomization.yaml")).Single().Root;

        StrAt(kustomization, "kind").Should().Be("Component");
        foreach (var file in SeqAt(kustomization, "resources").OfType<string>())
        {
            File.Exists(Path.Combine(dir, file)).Should().BeTrue($"'{file}' is listed in the component");
        }

        var baseResources = SeqAt(DocsIn(Path.Combine(K8sDir(), "kustomization.yaml")).Single().Root, "resources");
        baseResources.OfType<string>().Should().NotContain(r => r.Contains("components"),
            "the policies assume an ingress controller's labels, so a site opts in");
    }

    [Fact]
    public void NetworkPolicyComponent_LetsOnlyTheIngressReachWeb()
    {
        var policy = Policy("croft-web-ingress");

        Selects(policy, "web");
        var from = OnlyIngressRule(policy, "8080");
        from.Should().ContainSingle("only the ingress may reach Web");
        IsIngressController(from[0]);
    }

    [Fact]
    public void NetworkPolicyComponent_LetsOnlyWebAndTheIngressReachTheApi()
    {
        var policy = Policy("croft-api-ingress");

        Selects(policy, "api");
        var from = OnlyIngressRule(policy, "8080");
        from.Should().HaveCount(2);
        var web = from.Single(f => !f.ContainsKey("namespaceSelector"));
        var webLabels = MapAt(MapAt(web, "podSelector"), "matchLabels");
        StrAt(webLabels, "app.kubernetes.io/part-of").Should().Be("croft");
        StrAt(webLabels, "app.kubernetes.io/component").Should().Be("web");
        IsIngressController(from.Single(f => f.ContainsKey("namespaceSelector")));
    }

    private static Dictionary<string, object?> Policy(string name) =>
        Directory.GetFiles(Path.Combine(K8sDir(), "components", "network-policies"), "*.yaml")
            .Where(f => Path.GetFileName(f) != "kustomization.yaml")
            .SelectMany(DocsIn)
            .Single(d => StrAt(d.Root, "kind") == "NetworkPolicy" && StrAt(MapAt(d.Root, "metadata"), "name") == name)
            .Root;

    private static void Selects(Dictionary<string, object?> policy, string component)
    {
        var spec = MapAt(policy, "spec");
        var labels = MapAt(MapAt(spec, "podSelector"), "matchLabels");
        StrAt(labels, "app.kubernetes.io/part-of").Should().Be("croft");
        StrAt(labels, "app.kubernetes.io/component").Should().Be(component);
        SeqAt(spec, "policyTypes").OfType<string>().Should().Equal("Ingress");
    }

    private static List<Dictionary<string, object?>> OnlyIngressRule(Dictionary<string, object?> policy, string port)
    {
        var rule = AsItems(SeqAt(MapAt(policy, "spec"), "ingress")).Single();
        AsItems(SeqAt(rule, "ports")).Select(p => StrAt(p, "port")).Should().Equal(port);
        return AsItems(SeqAt(rule, "from"));
    }

    // The namespace and the pod selector sit in ONE list item: "Traefik's pods in kube-system".
    // As two items they would mean "anything in kube-system, or any pod named traefik here".
    private static void IsIngressController(Dictionary<string, object?> from)
    {
        StrAt(MapAt(MapAt(from, "namespaceSelector"), "matchLabels"), "kubernetes.io/metadata.name").Should().Be("kube-system");
        StrAt(MapAt(MapAt(from, "podSelector"), "matchLabels"), "app.kubernetes.io/name").Should().Be("traefik");
    }
}
