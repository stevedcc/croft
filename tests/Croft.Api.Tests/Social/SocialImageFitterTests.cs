using AwesomeAssertions;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures.Social;
using SkiaSharp;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12d item 4: the banner becomes a JPEG of at most 950,000 bytes for Bluesky's link card, or nothing.</summary>
public class SocialImageFitterTests
{
    [Fact]
    public void ABigPng_BecomesAJpegWithinTheLimit_AndTheLongSideIsAtMost1200()
    {
        var source = TestImages.Gradient(SKEncodedImageFormat.Png, 3000, 2000);

        var fitted = SocialImageFitter.Fit(source);

        fitted.Should().NotBeNull();
        fitted!.Length.Should().BeLessThanOrEqualTo(SocialImageFitter.MaxBytes);
        var (format, width, height) = TestImages.Inspect(fitted);
        format.Should().Be(SKEncodedImageFormat.Jpeg);
        Math.Max(width, height).Should().Be(1200);
        (width, height).Should().Be((1200, 800), "the aspect ratio stays");
    }

    [Fact]
    public void ALargeJpeg_IsDecodedAtAReducedSize_AndStillFits()
    {
        var source = TestImages.Gradient(SKEncodedImageFormat.Jpeg, 6000, 4000);

        using var codec = SKCodec.Create(SKData.CreateCopy(source))!;
        // The smallest native scale whose long side is still >= 1200: 1/4 (1500 x 1000); 1/8 would be 750.
        SocialImageFitter.DecodeSize(codec).Should().Be(new SKSizeI(1500, 1000));

        var fitted = SocialImageFitter.Fit(source);

        fitted.Should().NotBeNull();
        fitted!.Length.Should().BeLessThanOrEqualTo(SocialImageFitter.MaxBytes);
        var (format, width, height) = TestImages.Inspect(fitted);
        format.Should().Be(SKEncodedImageFormat.Jpeg);
        (width, height).Should().Be((1200, 800));
    }

    [Fact]
    public void APngOverThePixelBudget_IsNull_FromTheHeaderAlone()
    {
        // 5000 x 4000 = 20,000,000 pixels > 4096 x 4096, and a PNG has no native scaling to bring it down; the header is all that is read.
        var source = TestImages.PngClaiming(5000, 4000);
        using var codec = SKCodec.Create(SKData.CreateCopy(source))!;

        SocialImageFitter.DecodeSize(codec).Should().BeNull();
        SocialImageFitter.Fit(source).Should().BeNull();
    }

    [Fact]
    public void ASmallImage_IsNotEnlarged()
    {
        var fitted = SocialImageFitter.Fit(TestImages.Stripes(SKEncodedImageFormat.Png, 50, 100, 100));

        TestImages.Inspect(fitted!).Should().Be((SKEncodedImageFormat.Jpeg, 200, 100));
    }

    [Fact]
    public void ABannerThatDoesNotCompress_IsScaledDownUntilItFits()
    {
        // Noise does not compress. With a limit of 120,000 bytes (a real banner rarely needs the scaling steps at the real limit), 1200 x 1200 does not fit
        // even at quality 60, so the picture is made smaller by 0.75 until it does.
        var source = TestImages.Noise(SKEncodedImageFormat.Png, 1500, 1500);

        var fitted = SocialImageFitter.Fit(source, 120_000);

        fitted.Should().NotBeNull();
        fitted!.Length.Should().BeLessThanOrEqualTo(120_000);
        var (_, width, height) = TestImages.Inspect(fitted);
        Math.Max(width, height).Should().BeLessThan(1200, "it had to be scaled down by 0.75 steps");
        Math.Max(width, height).Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public void WhenNothingAboveTheMinimumSizeFits_TheResultIsNull_SoTheCardHasNoThumbnail()
    {
        SocialImageFitter.Fit(TestImages.Noise(SKEncodedImageFormat.Png, 1500, 1500), 2_000).Should().BeNull();
    }

    [Fact]
    public void ATransparentPng_IsFlattenedOntoWhite()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(40, 40, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using var image = SKImage.FromBitmap(bitmap);
        var png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        var fitted = SocialImageFitter.Fit(png);

        var pixel = TestImages.PixelAt(fitted!, 20, 20);
        (pixel.Red, pixel.Green, pixel.Blue).Should().Match<(byte, byte, byte)>(c => c.Item1 > 240 && c.Item2 > 240 && c.Item3 > 240);
    }

    [Fact]
    public void ASideOver8192_IsRefused_WithoutDecoding()
    {
        SocialImageFitter.Fit(TestImages.PngClaiming(9000, 10)).Should().BeNull();
        SocialImageFitter.Fit(TestImages.PngClaiming(10, 8193)).Should().BeNull();
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    public void SomethingThatIsNoImage_IsNull(byte[] bytes)
    {
        SocialImageFitter.Fit(bytes).Should().BeNull();
    }

    [Fact]
    public void ACorruptImage_IsNull()
    {
        var png = TestImages.Gradient(SKEncodedImageFormat.Png, 200, 200);
        var corrupt = png[..(png.Length / 3)];

        SocialImageFitter.Fit(corrupt).Should().BeNull();
    }
}
