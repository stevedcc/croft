using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages;
using Croft.Web.Services;
using Bunit;
using Bunit.Rendering;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 03d §3: every page's browser title uses the configured blog name (the site's Branding.Name,
/// "Test Blog" in this fixture): the home page's title is just the name, every other page's is
/// "&lt;page title&gt; · &lt;name&gt;" (a post: its title). The expected name is read from the
/// configured options, so a name hard-coded in src fails both tests. The pages set the title
/// through the shared BlogPageTitle component; the wrapper here renders a <see cref="HeadOutlet"/>
/// beside the page exactly like App.razor does, so the composed title is observable as the
/// rendered <c>&lt;title&gt;</c> element.
/// </summary>
public class PageTitleTests : BlogBunitContext
{
    private static readonly DateTime UtcNow = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private string BlogName =>
        Services.GetRequiredService<Croft.Web.Branding>().Name;

    [Fact]
    public async Task Home_Title_IsJustTheConfiguredBlogName()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());

        var cut = RenderPage(builder =>
        {
            builder.OpenComponent<Home>(0);
            builder.CloseComponent();
        });

        await cut.WaitForAssertionAsync(() =>
            cut.Find("title").TextContent.Should().Be(BlogName));
    }

    [Fact]
    public async Task Post_Title_IsThePostTitleThenTheBlogName()
    {
        var id = Guid.CreateVersion7();
        var api = new FakeBlogApiClient();
        api.PostsById[id] = new PostDto(id, "a-fine-post", "A Fine Post", null, "Body.", null, null,
            PostStatus.Published, UtcNow, UtcNow, UtcNow, "author-id", "Author", null, [], null, 3);
        Services.AddSingleton<IBlogApiClient>(api);
        Services.AddSingleton<IMarkdownRenderer>(new MarkdownRenderer());

        var cut = RenderPage(builder =>
        {
            builder.OpenComponent<Post>(0);
            builder.AddAttribute(1, "Slug", "a-fine-post");
            builder.CloseComponent();
        });

        await cut.WaitForAssertionAsync(() =>
            cut.Find("title").TextContent.Should().Be($"A Fine Post · {BlogName}"));
    }

    /// <summary>App.razor renders <see cref="HeadOutlet"/> in the document head and the router
    /// below it; this mirrors that shape so the page's <see cref="PageTitle"/> output is
    /// observable in the rendered markup.</summary>
    private IRenderedComponent<ContainerFragment> RenderPage(RenderFragment page) =>
        Render(builder =>
        {
            builder.OpenComponent<HeadOutlet>(0);
            builder.CloseComponent();
            builder.AddContent(1, page);
        });
}
