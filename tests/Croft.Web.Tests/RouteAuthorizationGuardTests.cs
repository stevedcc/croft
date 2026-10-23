using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace Croft.Web.Tests;

/// <summary>
/// The Web side of the authorisation guard (test-suite review 2026-10-19, C-1): every routable component
/// of the staff areas carries <c>[Authorize]</c> with the roles it should, and no public page does. The
/// pages' data comes from the API, which refuses the wrong roles itself, but the page chrome, the
/// forms and the staff navigation are the Web app's own: a page that loses its attribute would render
/// for anyone who types the URL. The table is the list of every route: a new routable component with
/// no row turns this red, so a page cannot be added without deciding who may open it.
/// </summary>
public class RouteAuthorizationGuardTests
{
    private const string Admin = "Admin";
    private const string Editor = "Editor,Admin";

    private static readonly IReadOnlyDictionary<string, string?> Routes = new Dictionary<string, string?>
    {
        // Staff pages: the roles [Authorize] must name.
        ["/admin"] = Admin,
        ["/admin/posts"] = Admin,
        ["/admin/posts/new"] = Admin,
        ["/admin/posts/{id:guid}/edit"] = Admin,
        ["/admin/posts/{id:guid}/analytics"] = Admin,
        ["/admin/pages"] = Admin,
        ["/admin/pages/new"] = Admin,
        ["/admin/pages/{id:guid}/edit"] = Admin,
        ["/admin/tags"] = Admin,
        ["/admin/discussion/blocks"] = Admin,
        ["/admin/media"] = Admin,
        ["/editor"] = Editor,
        ["/editor/posts/{slug}"] = Editor,

        // Public pages: no [Authorize] at all.
        ["/"] = null,
        ["/categories"] = null,
        ["/category/{slug}"] = null,
        ["/tag/{slug}"] = null,
        ["/post/{slug}"] = null,
        ["/search"] = null,
        ["/discussion/mastodon/reply"] = null,
        ["/not-found"] = null,
        ["/Error"] = null,
        ["/{slug}"] = null,
        ["/{**path:nonfile}"] = null,
    };

    [Fact]
    public void EveryRoutableComponent_HasARowInTheTable_AndTheTableHasNoStaleRow()
    {
        var actual = RoutableComponents().SelectMany(c => c.Routes).Distinct();

        actual.Should().BeEquivalentTo(Routes.Keys,
            "every route needs a decision (staff roles, or public) in RouteAuthorizationGuardTests.Routes");
    }

    [Fact]
    public void EveryRoutableComponent_AuthorizesExactlyTheRolesOfItsRoutesRow()
    {
        foreach (var (type, routes) in RoutableComponents())
        {
            var expected = routes.Select(route => Routes[route]).Distinct().ToList();
            expected.Should().ContainSingle($"{type.Name}'s routes must all carry the same rule");

            var authorize = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
            if (expected[0] is null)
            {
                authorize.Should().BeEmpty($"{type.Name} is a public page and must not require a sign-in");
            }
            else
            {
                authorize.Should().ContainSingle($"{type.Name} ({string.Join(", ", routes)}) is a staff page: it needs its [Authorize]")
                    .Which.Roles.Should().Be(expected[0], $"{type.Name} is for {expected[0]}");
            }
        }
    }

    private static IEnumerable<(Type Type, string[] Routes)> RoutableComponents() =>
        typeof(Program).Assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type))
            .Select(type => (Type: type, Routes: type.GetCustomAttributes<RouteAttribute>().Select(route => route.Template).ToArray()))
            .Where(component => component.Routes.Length > 0);
}
