using Markdig;

namespace Croft.Shared.Markdown;

/// <summary>
/// The one Markdown pipeline both apps build (the Web renderer for posts and pages, the API's
/// summary extractor). It is what <c>UseAdvancedExtensions()</c> gives in Markdig 1.4.0, written
/// out, minus GenericAttributes (which writes any <c>{key=value}</c> an author types straight into
/// the tag: <c>{onerror=alert(1)}</c>, <c>{style=...}</c>), and links and images limited to safe
/// URLs (<see cref="SafeLinkExtension"/>). The Web renderer adds <c>DisableHtml()</c> (raw HTML in
/// the source is escaped, never emitted); the API's summary extractor does not render HTML. The advanced set is spelled out
/// so a Markdig upgrade that adds an extension cannot change what authors can write unnoticed;
/// the test <c>CroftMarkdownTests</c> compares this list to <c>UseAdvancedExtensions()</c> and
/// fails when Markdig's changes.
/// </summary>
public static class CroftMarkdown
{
    /// <summary>A new pipeline builder with the extensions below, in Markdig's own order.</summary>
    public static MarkdownPipelineBuilder CreateBuilder() => new MarkdownPipelineBuilder()
        // The advanced set, minus UseGenericAttributes():
        .UseAlertBlocks()
        .UseAbbreviations()
        .UseAutoIdentifiers()
        .UseCitations()
        .UseCustomContainers()
        .UseDefinitionLists()
        .UseEmphasisExtras()
        .UseFigures()
        .UseFooters()
        .UseFootnotes()
        .UseGridTables()
        .UseMathematics()
        .UseMediaLinks()
        .UsePipeTables()
        .UseListExtras()
        .UseTaskLists()
        .UseDiagrams()
        .UseAutoLinks()
        // Only http, https, mailto, relative URLs and #fragments become links or images.
        .Use<SafeLinkExtension>();
}
