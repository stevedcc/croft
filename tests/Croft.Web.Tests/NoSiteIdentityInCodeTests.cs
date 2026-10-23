using System.Text;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// Croft is an engine: nothing in <c>src/</c> or <c>tests/</c> names a site or its owner (README,
/// "Deploying a site"; AGENTS.md rule 3). A test fixture that needs a domain or a handle uses an
/// <c>example.com</c>-style value. There is no allow-list: if something must stay, change this
/// test with the owner's decision, not around it.
/// </summary>
public class NoSiteIdentityInCodeTests
{
    // Assembled so that this file does not contain the word it forbids.
    private static readonly string Forbidden = string.Concat("craw", "ford");

    [Fact]
    public void No_file_under_src_or_tests_contains_the_owners_site_name()
    {
        var root = RepoRoot.Find();
        var offenders = new List<string>();

        foreach (var top in new[] { "src", "tests" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, top), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path);
                var parts = relative.Split(Path.DirectorySeparatorChar);
                // Build output and restore state are not sources.
                if (parts.Any(part => part is "bin" or "obj" or "node_modules"))
                {
                    continue;
                }

                if (relative.Contains(Forbidden, StringComparison.OrdinalIgnoreCase)
                    || ContainsForbidden(File.ReadAllBytes(path)))
                {
                    offenders.Add(relative);
                }
            }
        }

        offenders.Order().Should().BeEmpty(
            "nothing in src/ or tests/ may identify the owner's site; use example.com-style values");
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    [InlineData("utf-32BE")]
    public void The_scan_finds_the_word_in_every_unicode_encoding(string encodingName)
    {
        var bytes = Encoding.GetEncoding(encodingName).GetBytes($"// the {Forbidden.ToUpperInvariant()} site");

        ContainsForbidden(bytes).Should().BeTrue($"a {encodingName} file must not hide the word");
    }

    [Fact]
    public void The_scan_does_not_flag_unrelated_bytes()
    {
        ContainsForbidden(Encoding.Unicode.GetBytes("example.com")).Should().BeFalse();
    }

    // Searches the raw bytes, case-insensitively, as the word's ASCII letters in Latin-1/UTF-8
    // (one byte per letter) and in UTF-16 and UTF-32, either byte order (so a file saved by an
    // editor in one of those encodings cannot hide it); binary files are searched too.
    private static bool ContainsForbidden(byte[] bytes)
    {
        // (bytes per letter, index of the letter's byte within its code unit)
        (int Width, int Index)[] layouts = [(1, 0), (2, 0), (2, 1), (4, 0), (4, 3)];
        foreach (var (width, index) in layouts)
        {
            for (var start = 0; start + (Forbidden.Length - 1) * width + index < bytes.Length; start++)
            {
                var match = true;
                for (var i = 0; i < Forbidden.Length && match; i++)
                {
                    var b = bytes[start + i * width + index];
                    match = char.ToLowerInvariant((char)b) == Forbidden[i];
                }

                if (match)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
