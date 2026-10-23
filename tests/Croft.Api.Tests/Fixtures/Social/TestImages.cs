using System.Buffers.Binary;
using SkiaSharp;

namespace Croft.Api.Tests.Fixtures.Social;

/// <summary>Small images for the avatar tests, drawn with SkiaSharp (nothing here is a real person's picture).</summary>
internal static class TestImages
{
    /// <summary>The smallest GIF there is: one pixel (the encoder cannot write GIFs, so these are the bytes).</summary>
    public static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    /// <summary>Three vertical stripes, red | green | blue, the middle one as wide as the image is high: a centre-cropped square is all green.</summary>
    public static byte[] Stripes(SKEncodedImageFormat format, int sideStripe = 50, int middle = 100, int height = 100)
    {
        var width = sideStripe * 2 + middle;
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var red = new SKPaint { Color = SKColors.Red };
            using var green = new SKPaint { Color = SKColors.Lime };
            using var blue = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(0, 0, sideStripe, height, red);
            canvas.DrawRect(sideStripe, 0, middle, height, green);
            canvas.DrawRect(sideStripe + middle, 0, sideStripe, height, blue);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    /// <summary>A picture of coloured noise (seeded, so the same every time): it compresses badly, which is what a size limit needs.</summary>
    public static byte[] Noise(SKEncodedImageFormat format, int width, int height, bool withAlpha = false)
    {
        var random = new Random(42);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, withAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque));
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = withAlpha ? (byte)(i / 4 % 2 == 0 ? 255 : 0) : (byte)255;
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    /// <summary>A smooth picture (a diagonal gradient): it compresses well, so even a big one is small.</summary>
    public static byte[] Gradient(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [SKColors.DarkBlue, SKColors.Orange], SKShaderTileMode.Clamp) })
            canvas.DrawRect(0, 0, width, height, paint);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    /// <summary>A PNG whose header claims <paramref name="width"/> x <paramref name="height"/> and has valid checksums and an empty image-data chunk behind it: the header is all a size check needs.</summary>
    public static byte[] PngClaiming(int width, int height)
    {
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: truecolour
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", [0x78, 0x01, 0x01, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x01]); // an empty zlib stream: the decoder finds the image data, there is none to decode
        Chunk(png, "IEND", []);
        return [.. png];
    }

    private static void Chunk(List<byte> png, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.AddRange(length);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.AddRange(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.AddRange(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    /// <summary>The dimensions and format of an encoded image, as the decoder reads them from its header.</summary>
    public static (SKEncodedImageFormat Format, int Width, int Height) Inspect(byte[] bytes)
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(bytes))!;
        return (codec.EncodedFormat, codec.Info.Width, codec.Info.Height);
    }

    /// <summary>The colour of one pixel of an encoded image.</summary>
    public static SKColor PixelAt(byte[] bytes, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(bytes);
        return bitmap.GetPixel(x, y);
    }
}
