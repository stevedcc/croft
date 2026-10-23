using SkiaSharp;

namespace Croft.Api.Social;

/// <summary>
/// Makes a post's banner fit Bluesky's link card (brief 12d): a JPEG of at most <see cref="MaxBytes"/>, the long side at most <see cref="MaxSide"/>, flattened onto
/// white (a JPEG has no transparency). It tries quality 80, 70 and 60, then scales by 0.75 until it fits or the long side would drop below
/// <see cref="MinSide"/>. Anything that is not a picture Skia can decode completely, or a side over <see cref="MaxSource"/> (checked from the header,
/// before any pixel is decoded), gives null: the card then has no thumbnail.
/// </summary>
public static class SocialImageFitter
{
    public const int MaxBytes = 950_000;
    public const int MaxSide = 1200;
    public const int MinSide = 400;
    public const int MaxSource = 8192;

    /// <summary>
    /// The most pixels that are ever decoded. A decoded bitmap is pixels x 4 bytes (RGBA): 4096 x 4096 is 64 MiB, where the largest
    /// source the header check allows (8192 x 8192) would be 256 MiB, half the API pod's 512 MiB limit before the image copy and the
    /// surfaces. A banner that cannot be brought under this by a native decode scale is left out (the card has no thumbnail) rather than risking the pod.
    /// </summary>
    public const long MaxDecodedPixels = 4096L * 4096;

    private static readonly int[] Qualities = [80, 70, 60];
    private static readonly float[] NativeScales = [1f / 8, 1f / 4, 1f / 2, 1f];

    /// <summary>
    /// The size to decode at, from the header alone: the smallest native scale (JPEG and WebP decode at 1/8, 1/4 and 1/2 natively; others only at 1)
    /// whose long side is still at least <see cref="MaxSide"/>, or the source's own size when it is smaller. Null when even that has more than
    /// <see cref="MaxDecodedPixels"/> pixels.
    /// </summary>
    public static SKSizeI? DecodeSize(SKCodec codec)
    {
        SKSizeI? chosen = null;
        foreach (var scale in NativeScales)
        {
            var size = codec.GetScaledDimensions(scale);
            if (size.Width <= 0 || size.Height <= 0)
                continue;

            chosen = size;
            if (Math.Max(size.Width, size.Height) >= MaxSide)
                break;
        }

        return chosen is { } c && (long)c.Width * c.Height <= MaxDecodedPixels ? c : null;
    }

    public static byte[]? Fit(byte[] source) => Fit(source, MaxBytes);

    /// <summary>As <see cref="Fit(byte[])"/> with another byte limit (the tests use a small one to reach the scaling steps, which a real banner at 1200 pixels rarely needs).</summary>
    public static byte[]? Fit(byte[] source, int maxBytes)
    {
        if (source.Length == 0)
            return null;

        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            return null;

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width is <= 0 or > MaxSource || height is <= 0 or > MaxSource)
            return null;

        // Chosen from the header, before any pixel is allocated: the decode is at a reduced size and within the pixel budget.
        if (DecodeSize(codec) is not { } decode)
            return null;

        (width, height) = (decode.Width, decode.Height);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            return null; // truncated or corrupt: a half-decoded picture is not sent

        bitmap.SetImmutable(); // so the image shares the pixels instead of copying them
        using var image = SKImage.FromBitmap(bitmap);
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        var first = true;
        while (true)
        {
            var (w, h) = (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
            using var flat = Flatten(image, w, h);
            foreach (var quality in first ? Qualities : [Qualities[^1]])
            {
                using var encoded = flat.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (encoded is not null && encoded.Size <= maxBytes)
                    return encoded.ToArray();
            }

            // Not small enough yet: 0.75 of the size, unless that would put the long side under the minimum.
            first = false;
            scale *= 0.75;
            if (Math.Max(width, height) * scale < MinSide)
                return null;
        }
    }

    private static SKImage Flatten(SKImage image, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawImage(image, SKRect.Create(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell), null);
        return surface.Snapshot();
    }
}
