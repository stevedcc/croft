using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// IconPickerDialog (spec §6.5/§7.4): the MudDialog /admin/tags opens over IconPicker. Picking an
/// icon and saving closes the dialog with the icon name; Cancel discards the selection.
/// </summary>
public class IconPickerDialogTests : BlogBunitContext
{
    [Fact]
    public async Task Save_ReturnsTheChosenIcon()
    {
        var provider = Render<MudDialogProvider>();

        var reference = await Services.GetRequiredService<IDialogService>().ShowAsync<IconPickerDialog>();

        provider.WaitForAssertion(() => provider.FindAll("button").Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        provider.FindAll("button").Single(b => b.TextContent.Contains("Terrain")).Click(new MouseEventArgs());
        provider.FindAll("button").Single(b => b.TextContent == "Save").Click(new MouseEventArgs());

        var result = await reference.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeFalse();
        result.Data.Should().BeOfType<string>().Which.Should().Be("Terrain",
            "the caller gets the chosen icon name back from the dialog");
    }

    [Fact]
    public async Task Cancel_DiscardsTheSelection()
    {
        var parameters = new DialogParameters
        {
            [nameof(IconPickerDialog.Value)] = "Terrain",
        };
        var provider = Render<MudDialogProvider>();

        var reference = await Services.GetRequiredService<IDialogService>().ShowAsync<IconPickerDialog>(parameters);

        provider.WaitForAssertion(() => provider.FindAll("button").Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        provider.FindAll("button").Single(b => b.TextContent.Contains("Default (folder)")).Click(new MouseEventArgs());
        provider.FindAll("button").Single(b => b.TextContent == "Cancel").Click(new MouseEventArgs());

        var result = await reference.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeTrue("closing without saving discards the selection");
    }
}
