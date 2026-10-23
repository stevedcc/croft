using Croft.Web.E2E;

namespace ScreenshotGate.Compare;

/// <summary>
/// The screenshot gate's comparison step (item 7, ruling R17): every {surface}-{width}-{theme}.png
/// of the baseline captures (GATE_BASE) against the reviewed checkout's (GATE_BRANCH), decoded
/// pixel by pixel with the E2E project's <see cref="PngPixelDecoder"/>. The report is one line
/// per pair — differing pixel count and the bounding box of the differences — and any pair that
/// differs gets three crops in GATE_DIFF: the baseline region, the branch region and a diff map
/// (grey where equal, red where not). The test fails on any difference or missing capture; the
/// summary is in its message either way.
/// </summary>
public class CompareTests
{
    private const int CropMargin = 24;

    [Test]
    public async Task BaselineAndBranchCapturesArePixelIdentical()
    {
        var baseDir = Environment.GetEnvironmentVariable("GATE_BASE")
            ?? throw new InvalidOperationException("GATE_BASE is not set.");
        var branchDir = Environment.GetEnvironmentVariable("GATE_BRANCH")
            ?? throw new InvalidOperationException("GATE_BRANCH is not set.");
        var diffDir = Environment.GetEnvironmentVariable("GATE_DIFF")
            ?? Path.GetFullPath(Path.Combine(baseDir, "..", "diff"));
        Directory.CreateDirectory(diffDir);

        var names = Directory.EnumerateFiles(baseDir, "*.png").Select(f => Path.GetFileName(f)!)
            .Concat(Directory.EnumerateFiles(branchDir, "*.png").Select(f => Path.GetFileName(f)!))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var lines = new List<string>();
        var failures = new List<string>();
        foreach (var name in names)
        {
            var basePath = Path.Combine(baseDir, name);
            var branchPath = Path.Combine(branchDir, name);
            if (!File.Exists(basePath) || !File.Exists(branchPath))
            {
                var missing = File.Exists(basePath) ? branchDir : baseDir;
                lines.Add($"{name}: MISSING on the {missing} side");
                failures.Add(name);
                continue;
            }

            var baseline = PngPixelDecoder.Decode(await File.ReadAllBytesAsync(basePath));
            var branch = PngPixelDecoder.Decode(await File.ReadAllBytesAsync(branchPath));
            if (baseline.Width != branch.Width || baseline.Height != branch.Height)
            {
                lines.Add($"{name}: dimensions differ — baseline {baseline.Width}x{baseline.Height}, branch {branch.Width}x{branch.Height}");
                failures.Add(name);
                continue;
            }

            var (count, minX, minY, maxX, maxY) = CountDifferences(baseline, branch);
            lines.Add(count == 0
                ? $"{name}: 0 differing pixels of {baseline.Width * baseline.Height}"
                : $"{name}: {count} differing pixels of {baseline.Width * baseline.Height} " +
                  $"(bbox {minX},{minY}..{maxX},{maxY})");
            if (count == 0)
            {
                continue;
            }

            failures.Add(name);
            WriteCrops(diffDir, name, baseline, branch, (minX, minY, maxX, maxY));
        }

        foreach (var line in lines)
        {
            TestContext.Progress.WriteLine(line);
        }

        Assert.That(failures, Is.Empty,
            "The screenshot gate found differences (crops in " + diffDir + "):\n" + string.Join("\n", lines));
    }

    /// <summary>Counts differing pixels and their bounding box (inclusive).</summary>
    private static (long Count, int MinX, int MinY, int MaxX, int MaxY) CountDifferences(
        PngPixelDecoder.DecodedImage baseline, PngPixelDecoder.DecodedImage branch)
    {
        long count = 0;
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < baseline.Height; y++)
        {
            for (var x = 0; x < baseline.Width; x++)
            {
                var offset = (y * baseline.Width + x) * 4;
                if (baseline.Rgba.AsSpan(offset, 4).SequenceEqual(branch.Rgba.AsSpan(offset, 4)))
                {
                    continue;
                }

                count++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return (count, minX, minY, maxX, maxY);
    }

    /// <summary>Writes the baseline region, the branch region and the diff map around the bbox.</summary>
    private static void WriteCrops(string diffDir, string name,
        PngPixelDecoder.DecodedImage baseline, PngPixelDecoder.DecodedImage branch,
        (int MinX, int MinY, int MaxX, int MaxY) bbox)
    {
        var x0 = Math.Max(0, bbox.MinX - CropMargin);
        var y0 = Math.Max(0, bbox.MinY - CropMargin);
        var x1 = Math.Min(baseline.Width - 1, bbox.MaxX + CropMargin);
        var y1 = Math.Min(baseline.Height - 1, bbox.MaxY + CropMargin);
        var width = x1 - x0 + 1;
        var height = y1 - y0 + 1;

        var baseCrop = new byte[width * height * 4];
        var branchCrop = new byte[width * height * 4];
        var diffMap = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var source = ((y + y0) * baseline.Width + (x + x0)) * 4;
                var target = (y * width + x) * 4;
                baseline.Rgba.AsSpan(source, 4).CopyTo(baseCrop.AsSpan(target, 4));
                branch.Rgba.AsSpan(source, 4).CopyTo(branchCrop.AsSpan(target, 4));
                var equal = baseline.Rgba.AsSpan(source, 4).SequenceEqual(branch.Rgba.AsSpan(source, 4));
                diffMap[target] = equal ? (byte)128 : (byte)255;
                diffMap[target + 1] = equal ? (byte)128 : (byte)0;
                diffMap[target + 2] = equal ? (byte)128 : (byte)0;
                diffMap[target + 3] = 255;
            }
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        File.WriteAllBytes(Path.Combine(diffDir, $"{stem}-crop-baseline.png"), PngWriter.EncodeRgba(width, height, baseCrop));
        File.WriteAllBytes(Path.Combine(diffDir, $"{stem}-crop-branch.png"), PngWriter.EncodeRgba(width, height, branchCrop));
        File.WriteAllBytes(Path.Combine(diffDir, $"{stem}-crop-diffmap.png"), PngWriter.EncodeRgba(width, height, diffMap));
    }
}
