using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// MediaPickerDialog (spec §6.4/§7.4): the MudDialog the editor opens over MediaGrid in Pick
/// mode. Picking a tile raises OnPicked and closes the dialog with the asset; Cancel closes it
/// without a choice.
/// </summary>
public class MediaPickerDialogTests : BlogBunitContext
{
    private static MediaAssetDto Asset(string name = "photo.png")
    {
        var id = Guid.CreateVersion7();
        return new MediaAssetDto(id, $"{id:N}", name, "image/png", $"/api/media/{id}",
            MediaAssetScope.Global, null, null);
    }

    [Fact]
    public async Task PickingTile_RaisesOnPicked_AndClosesTheDialogWithTheAsset()
    {
        var asset = Asset();
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient { GlobalMedia = [asset] });
        var provider = Render<MudDialogProvider>();
        var picked = new List<MediaAssetDto>();
        var parameters = new DialogParameters
        {
            [nameof(MediaPickerDialog.OnPicked)] = EventCallback.Factory.Create<MediaAssetDto>(this, picked.Add),
        };

        var reference = await Services.GetRequiredService<IDialogService>().ShowAsync<MediaPickerDialog>(parameters);

        provider.WaitForAssertion(() => provider.Find(".media-tile__pick").Should().NotBeNull(), TimeSpan.FromSeconds(5));
        provider.Find(".media-tile__pick").Click(new MouseEventArgs());

        picked.Should().ContainSingle().Which.Id.Should().Be(asset.Id);
        var result = await reference.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeFalse();
        result.Data.Should().BeOfType<MediaAssetDto>().Which.Id.Should().Be(asset.Id,
            "the caller gets the picked asset back from the dialog");
    }

    [Fact]
    public async Task Cancel_ClosesTheDialogWithoutAChoice()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());
        var provider = Render<MudDialogProvider>();

        var reference = await Services.GetRequiredService<IDialogService>().ShowAsync<MediaPickerDialog>();

        provider.WaitForAssertion(() => provider.FindAll("button").Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        provider.FindAll("button").Single(b => b.TextContent == "Cancel").Click(new MouseEventArgs());

        var result = await reference.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeTrue("closing without picking is a cancellation");
    }
}
