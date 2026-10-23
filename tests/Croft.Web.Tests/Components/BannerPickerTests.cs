using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;

namespace Croft.Web.Tests.Components;

/// <summary>
/// BannerPicker (spec §6.2/§7.4): the banner panel of the editor form — the current banner's
/// thumbnail and name, plus Upload, Choose from library and Remove. The host page does the
/// upload and the dialog; this component reports the user's choices.
/// </summary>
public class BannerPickerTests : BlogBunitContext
{
    private static MediaAssetDto Banner(string name = "hills.png", string? altText = null)
    {
        var id = Guid.CreateVersion7();
        return new MediaAssetDto(id, $"{id:N}", name, "image/png", $"/api/media/{id}",
            MediaAssetScope.Global, null, altText);
    }

    [Fact]
    public void BannerPicker_WithBanner_ShowsTheThumbnailAndName_WithRemove()
    {
        var banner = Banner("hills.png", "Sunset over the hills");
        var cut = Render<BannerPicker>(p => p.Add(x => x.Banner, banner));

        var img = cut.Find(".banner-picker__thumb");
        img.GetAttribute("src").Should().Be(banner.Url);
        img.GetAttribute("alt").Should().Be("Sunset over the hills");
        cut.Find(".banner-picker__name").TextContent.Should().Be("hills.png");
        cut.FindAll("button").Should().Contain(b => b.TextContent == "Remove");
    }

    [Fact]
    public void BannerPicker_WithoutAltText_FallsBackToTheFileNameAsAlt()
    {
        var cut = Render<BannerPicker>(p => p.Add(x => x.Banner, Banner("hills.png")));

        cut.Find(".banner-picker__thumb").GetAttribute("alt").Should().Be("hills.png");
    }

    [Fact]
    public void BannerPicker_NoBanner_ShowsNoThumbnailOrRemove()
    {
        var cut = Render<BannerPicker>(p => p.Add(x => x.Banner, null));

        cut.FindAll(".banner-picker__thumb").Should().BeEmpty();
        cut.FindAll("button").Should().NotContain(b => b.TextContent == "Remove");
    }

    [Fact]
    public async Task BannerPicker_Upload_RaisesOnUploadWithTheChosenFile()
    {
        var uploaded = new List<IBrowserFile>();
        var cut = Render<BannerPicker>(p => p
            .Add(x => x.Banner, null)
            .Add(x => x.OnUpload, EventCallback.Factory.Create<IBrowserFile>(this, uploaded.Add)));

        var input = cut.FindComponent<InputFile>();
        await cut.InvokeAsync(() =>
            input.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([new FakeBrowserFile("hills.png")])));

        uploaded.Should().ContainSingle("the chosen file goes to the host for upload")
            .Which.Name.Should().Be("hills.png");
    }

    [Fact]
    public async Task BannerPicker_ButtonsRaiseTheHostCallbacks()
    {
        var chose = false;
        var removed = false;
        var cut = Render<BannerPicker>(p => p
            .Add(x => x.Banner, Banner())
            .Add(x => x.OnChooseFromLibrary, EventCallback.Factory.Create(this, () => chose = true))
            .Add(x => x.OnRemove, EventCallback.Factory.Create(this, () => removed = true)));

        cut.FindAll("button").Single(b => b.TextContent == "Choose from library").Click(new MouseEventArgs());
        cut.FindAll("button").Single(b => b.TextContent == "Remove").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => chose.Should().BeTrue());
        removed.Should().BeTrue();
    }
}
