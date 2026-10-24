using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// Keeps docs/workarounds.md and the markers in the code in step. A block marker is a start
/// comment (the word WORKAROUND, an opening parenthesis, an ID of the form W-NNN, a comma and
/// the library) with a matching end comment (END, then the same word, parenthesis and ID)
/// later in the same file; a file that exists only as a workaround carries one file marker
/// (WORKAROUND-FILE, same shape as the start) in its first five lines. The register has one
/// "### W-NNN: title" heading per ID. Every marker needs an entry, every entry needs a marker,
/// IDs are three digits and unique, a marker without an ID is an error, every start has
/// exactly one end after it, and blocks of one ID do not nest. IDs listed under "## Retired" in
/// the register have no entry and no marker. Each failure names the file and line of every
/// offender.
/// </summary>
public partial class WorkaroundRegisterTests
{
    private const string RegisterPath = "docs/workarounds.md";

    /// <summary>Joined here so this file's own source does not contain the needle it scans for.</summary>
    private const string StartWord = "WORKAROUND" + "(";

    private const string EndWord = "END " + StartWord;

    private const string FileWord = "WORKAROUND-FILE" + "(";

    private const int FileMarkerLines = 5;

    private static readonly string[] ScannedRoots = ["src", "tests", "k8s", "dev"];

    // The repository root's own MSBuild files (Directory.Build.props and the like), not the root
    // folder recursively: the extension filter below still applies to them.
    private static readonly string[] RootFilePatterns = ["*.props", "*.targets"];

    private static readonly string[] TextExtensions =
        [".cs", ".razor", ".js", ".css", ".yaml", ".yml", ".sh", ".csproj", ".props", ".targets", ".xml", ".html", ".cshtml"];

    private static readonly string[] SkippedSegments = ["bin", "obj", "node_modules", ".git", ".ha-traefik"];

    private static readonly Lazy<Scan> Result = new(ScanRepository);

    [Fact]
    public void EveryMarker_HasAnIdWithTheRightShape()
    {
        var offenders = Result.Value.Markers.Where(marker => marker.Id is null).Select(Describe).ToList();

        offenders.Should().BeEmpty(
            "every marker names an ID as W- plus three digits then the library: a marker without an ID, or with an ID that is not W- plus three digits, cannot be matched to an entry");
    }

    [Fact]
    public void EveryStart_HasExactlyOneEndAfterIt_AndBlocksOfOneIdDoNotNest()
    {
        List<string> offenders = [];
        foreach (var file in Result.Value.Markers.Where(marker => marker.Id is not null).GroupBy(marker => marker.File))
        {
            Dictionary<string, Occurrence> open = [];
            foreach (var marker in file.OrderBy(marker => marker.Line))
            {
                switch (marker.Kind)
                {
                    case MarkerKind.Start when open.TryGetValue(marker.Id!, out var outer):
                        offenders.Add($"{Describe(marker)} (starts inside the block of the same ID opened at line {outer.Line})");
                        break;
                    case MarkerKind.Start:
                        open[marker.Id!] = marker;
                        break;
                    case MarkerKind.End when !open.Remove(marker.Id!):
                        offenders.Add($"{Describe(marker)} (end without a start before it)");
                        break;
                }
            }

            offenders.AddRange(open.Values.Select(start => $"{Describe(start)} (start without an end after it)"));
        }

        offenders.Should().BeEmpty("every start marker is closed by one end marker with the same ID later in the same file");
    }

    [Fact]
    public void FileMarkers_AreWithinTheFirstLines()
    {
        var offenders = Result.Value.Markers
            .Where(marker => marker.Kind == MarkerKind.File && marker.Line > FileMarkerLines)
            .Select(Describe)
            .ToList();

        offenders.Should().BeEmpty($"a whole-file marker sits within the first {FileMarkerLines} lines");
    }

    [Fact]
    public void EveryMarkerId_HasAnEntryInTheRegister()
    {
        var entryIds = Result.Value.Entries.Where(entry => entry.Id is not null).Select(entry => entry.Id).ToHashSet();

        var offenders = Result.Value.Markers
            .Where(marker => marker.Kind != MarkerKind.End && marker.Id is not null && !entryIds.Contains(marker.Id))
            .Select(Describe)
            .ToList();

        offenders.Should().BeEmpty($"every marked ID needs a '### W-NNN:' heading in {RegisterPath}");
    }

    [Fact]
    public void EveryEntry_HasAtLeastOneMarker()
    {
        var markedIds = MarkedIds();

        var offenders = Result.Value.Entries
            .Where(entry => entry.Id is not null && !markedIds.Contains(entry.Id))
            .Select(Describe)
            .ToList();

        offenders.Should().BeEmpty("every register entry needs a marker in the code, so it can be found and removed with one grep");
    }

    [Fact]
    public void EntryIds_AreWellFormedAndUnique()
    {
        var entries = Result.Value.Entries;

        var malformed = entries.Where(entry => entry.Id is null).Select(Describe).ToList();
        malformed.Should().BeEmpty("every register heading is '### W-NNN: title' with three digits");

        var duplicates = entries
            .Where(entry => entry.Id is not null)
            .GroupBy(entry => entry.Id)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(Describe)
            .ToList();
        duplicates.Should().BeEmpty("an ID names one entry");
    }

    [Fact]
    public void TheScan_FindsTheRetiredHeading_TheEntriesAndTheMarkers()
    {
        Result.Value.HasRetiredHeading.Should().BeTrue(
            "the register has an exact '## Retired' heading; renaming it would disarm the retired-ID check");
        Result.Value.Entries.Should().NotBeEmpty("the register's '### W-NNN:' headings were found");
        Result.Value.Markers.Should().NotBeEmpty("the markers under src, tests, k8s and dev were found");
    }

    [Fact]
    public void RetiredIds_HaveNoEntryAndNoMarker()
    {
        var retired = Result.Value.Retired;
        var entryIds = Result.Value.Entries.Where(entry => entry.Id is not null).Select(entry => entry.Id).ToHashSet();
        var markedIds = MarkedIds();

        retired.Where(entry => entry.Id is null).Select(Describe).Should().BeEmpty(
            "every line under '## Retired' starts '- W-NNN: ' with three digits");
        retired.Where(entry => entry.Id is not null && entryIds.Contains(entry.Id)).Select(Describe).Should().BeEmpty(
            "a retired ID is never reused for an entry");
        retired.Where(entry => entry.Id is not null && markedIds.Contains(entry.Id)).Select(Describe).Should().BeEmpty(
            "no marker may still carry a retired ID");
        retired.Where(entry => entry.Id is not null).GroupBy(entry => entry.Id).Where(group => group.Count() > 1)
            .SelectMany(group => group).Select(Describe).Should().BeEmpty("a retired ID is listed once");
    }

    private static HashSet<string?> MarkedIds() =>
    [
        .. Result.Value.Markers
            .Where(marker => marker.Kind != MarkerKind.End && marker.Id is not null)
            .Select(marker => marker.Id),
    ];

    private static string Describe(Occurrence occurrence) =>
        $"{occurrence.File}:{occurrence.Line}: {occurrence.Text.Trim()}";

    private static Scan ScanRepository()
    {
        var root = RepoRoot.Find();

        List<Occurrence> markers = [];
        var files = ScannedRoots.Select(name => Path.Combine(root, name)).Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Concat(RootFilePatterns.SelectMany(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)));
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!IsScanned(relative))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var (kind, pattern) = line.Contains(EndWord, StringComparison.Ordinal) ? (MarkerKind.End, EndId())
                    : line.Contains(FileWord, StringComparison.Ordinal) ? (MarkerKind.File, FileId())
                    : line.Contains(StartWord, StringComparison.Ordinal) ? (MarkerKind.Start, StartId())
                    : (MarkerKind.Start, null);
                if (pattern is null)
                {
                    continue;
                }

                var match = pattern.Match(line);
                markers.Add(new Occurrence(relative, index + 1, match.Success ? match.Groups["id"].Value : null, line, kind));
            }
        }

        List<Occurrence> entries = [];
        var register = File.ReadAllLines(Path.Combine(root, RegisterPath));
        for (var index = 0; index < register.Length; index++)
        {
            if (!register[index].StartsWith("### W-", StringComparison.Ordinal))
            {
                continue;
            }

            var match = EntryId().Match(register[index]);
            entries.Add(new Occurrence(RegisterPath, index + 1, match.Success ? match.Groups["id"].Value : null, register[index], MarkerKind.Start));
        }

        List<Occurrence> retired = [];
        var inRetired = false;
        var hasRetiredHeading = false;
        for (var index = 0; index < register.Length; index++)
        {
            if (register[index].StartsWith("## ", StringComparison.Ordinal))
            {
                inRetired = register[index].TrimEnd() == "## Retired";
                hasRetiredHeading |= inRetired;
                continue;
            }

            if (inRetired && register[index].StartsWith("- ", StringComparison.Ordinal))
            {
                var match = RetiredId().Match(register[index]);
                retired.Add(new Occurrence(RegisterPath, index + 1, match.Success ? match.Groups["id"].Value : null, register[index], MarkerKind.Start));
            }
        }

        return new Scan(markers, entries, retired, hasRetiredHeading);
    }

    private static bool IsScanned(string relativePath)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(SkippedSegments.Contains) || relativePath.Contains("/wwwroot/lib/", StringComparison.Ordinal))
        {
            return false;
        }

        var name = segments[^1];
        return name.StartsWith("Dockerfile", StringComparison.Ordinal)
            || TextExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"WORKAROUND\((?<id>W-\d{3}), ")]
    private static partial Regex StartId();

    [GeneratedRegex(@"END WORKAROUND\((?<id>W-\d{3})\)")]
    private static partial Regex EndId();

    [GeneratedRegex(@"WORKAROUND-FILE\((?<id>W-\d{3}), ")]
    private static partial Regex FileId();

    [GeneratedRegex(@"^### (?<id>W-\d{3}): ")]
    private static partial Regex EntryId();

    [GeneratedRegex(@"^- (?<id>W-\d{3}): ")]
    private static partial Regex RetiredId();

    private enum MarkerKind { Start, End, File }

    private sealed record Occurrence(string File, int Line, string? Id, string Text, MarkerKind Kind);

    private sealed record Scan(IReadOnlyList<Occurrence> Markers, IReadOnlyList<Occurrence> Entries, IReadOnlyList<Occurrence> Retired, bool HasRetiredHeading);
}
