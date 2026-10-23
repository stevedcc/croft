using System.IO.Compression;

namespace Croft.Web.E2E;

/// <summary>Decodes PNG screenshots by hand rather than taking an image library dependency (the
/// ThemeTests pattern this is extracted from). The suite samples clipped 1x1 px screenshots to
/// assert rendered colours through <see cref="PixelAt"/>; <see cref="Decode"/> returns every
/// pixel for whole-image comparisons (the R15 /search pixel check).</summary>
internal static class PngPixelDecoder
{
    /// <summary>A decoded image: every pixel as RGBA bytes, row-major.</summary>
    public sealed record DecodedImage(int Width, int Height, byte[] Rgba);

    /// <summary>Decodes the single pixel of a 1x1 px PNG screenshot.</summary>
    public static (int R, int G, int B) PixelAt(byte[] png)
    {
        var image = Decode(png);
        Assert.That(image.Width, Is.EqualTo(1), "Expected a 1x1 px screenshot.");
        Assert.That(image.Height, Is.EqualTo(1), "Expected a 1x1 px screenshot.");
        return (image.Rgba[0], image.Rgba[1], image.Rgba[2]);
    }

    /// <summary>Decodes an 8-bit RGB or RGBA PNG to its RGBA pixels (per-scanline filters 0-4).</summary>
    public static DecodedImage Decode(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var signature = new byte[8];
        ms.ReadExactly(signature);
        var expected = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        Assert.That(signature, Is.EqualTo(expected), "Screenshot is not a PNG.");

        var idat = new MemoryStream();
        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        var buffer = new byte[4];
        while (ms.Position < ms.Length)
        {
            ms.ReadExactly(buffer);
            var length = (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
            ms.ReadExactly(buffer);
            var type = System.Text.Encoding.ASCII.GetString(buffer);

            var data = new byte[length];
            ms.ReadExactly(data);
            ms.ReadExactly(new byte[4]); // CRC

            switch (type)
            {
                case "IHDR":
                    width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                    height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                    bitDepth = data[8];
                    colorType = data[9];
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    break;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(width, Is.GreaterThan(0), "IHDR must name a width.");
            Assert.That(height, Is.GreaterThan(0), "IHDR must name a height.");
            Assert.That(bitDepth, Is.EqualTo(8), "Expected 8 bits per channel.");
            Assert.That(colorType, Is.AnyOf(2, 6), "Expected an RGB or RGBA PNG.");
        });

        var channels = colorType == 6 ? 4 : 3;
        var stride = width * channels;

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var scanlines = raw.ToArray();
        Assert.That(scanlines.Length, Is.EqualTo(height * (1 + stride)), "Unexpected PNG scanline layout.");

        // Undo the per-scanline filter (PNG row filters 0-4).
        var pixels = new byte[height * stride];
        Span<byte> prior = stackalloc byte[stride];
        prior.Clear(); // no row above the first one
        for (var row = 0; row < height; row++)
        {
            var filter = scanlines[row * (1 + stride)];
            var line = scanlines.AsSpan(row * (1 + stride) + 1, stride).ToArray();
            switch (filter)
            {
                case 0:
                    break;
                case 1:
                    for (var i = channels; i < stride; i++) line[i] = (byte)(line[i] + line[i - channels]);
                    break;
                case 2:
                    for (var i = 0; i < stride; i++) line[i] = (byte)(line[i] + prior[i]);
                    break;
                case 3:
                    for (var i = 0; i < stride; i++)
                    {
                        var left = i >= channels ? line[i - channels] : 0;
                        line[i] = (byte)(line[i] + (left + prior[i]) / 2);
                    }
                    break;
                case 4:
                    for (var i = 0; i < stride; i++)
                    {
                        var left = i >= channels ? line[i - channels] : 0;
                        var up = prior[i];
                        var upLeft = i >= channels ? prior[i - channels] : 0;
                        var p = left + up - upLeft;
                        var pa = Math.Abs(p - left);
                        var pb = Math.Abs(p - up);
                        var pc = Math.Abs(p - upLeft);
                        var predictor = pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
                        line[i] = (byte)(line[i] + predictor);
                    }
                    break;
                default:
                    Assert.Fail($"Unsupported PNG row filter {filter}.");
                    break;
            }

            line.CopyTo(pixels.AsSpan(row * stride));
            line.CopyTo(prior);
        }

        // Normalise to RGBA.
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = pixels[i * channels];
            rgba[i * 4 + 1] = pixels[i * channels + 1];
            rgba[i * 4 + 2] = pixels[i * channels + 2];
            rgba[i * 4 + 3] = channels == 4 ? pixels[i * channels + 3] : (byte)255;
        }

        return new(width, height, rgba);
    }
}
