using System.Reflection;
using AwesomeAssertions;
using Croft.Web.Caching;
using Microsoft.AspNetCore.Components;

namespace Croft.Web.Tests.OutputCache;

/// <summary>
/// Brief 14b item 1, the drift guard: the output cache keys a reading page on its path plus the
/// query parameters the page reads, so a page that starts reading another parameter must say so in
/// <see cref="ReadingPages"/>, or two different pages would share one entry. Everything is read from
/// the page components by reflection.
/// </summary>
public class ReadingPageCacheRegistryTests
{
    private static string QueryName(PropertyInfo property) =>
        property.GetCustomAttribute<SupplyParameterFromQueryAttribute>()!.Name ?? property.Name;

    [Fact]
    public void EveryQueryParameterAnyCachedPageReads_IsInItsVaryList()
    {
        foreach (var route in ReadingPages.Routes)
        {
            var read = route.Page.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<SupplyParameterFromQueryAttribute>() is not null)
                .Select(QueryName);

            read.Except(route.QueryKeys).Should().BeEmpty(
                $"{route.Page.Name} reads these query parameters, so they must be part of its cache key");
        }
    }

    [Fact]
    public void EveryCachedRoute_IsThePagesOwnRoute()
    {
        foreach (var route in ReadingPages.Routes)
        {
            route.Page.GetCustomAttributes<RouteAttribute>().Select(a => a.Template)
                .Should().Contain(route.Pattern, $"{route.Page.Name} is registered under the route it serves");
        }
    }

    [Fact]
    public void EveryStaticPageOfTheWebApp_IsEitherCached_OrExplicitlyNot()
    {
        var notCached = new Dictionary<string, string>
        {
            ["Search"] = "free text: cached in the API instead (brief 14b item 3)",
            ["NotFound"] = "a 404 page",
            ["Error"] = "an error page",
            ["MastodonReply"] = "the Mastodon reply landing page (brief 12b): depends on the query, never shared between readers",
        };
        var staticPages = typeof(Program).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any()
                && t.GetCustomAttribute<ExcludeFromInteractiveRoutingAttribute>() is not null)
            .Select(t => t.Name)
            .ToList();

        staticPages.Should().NotBeEmpty();
        staticPages.Where(name => ReadingPages.Routes.All(r => r.Page.Name != name) && !notCached.ContainsKey(name))
            .Should().BeEmpty("a new static page must be added to ReadingPages.Routes or to this list with a reason");
        ReadingPages.Routes.Select(r => r.Page.Name).Should().OnlyHaveUniqueItems();
    }
}
