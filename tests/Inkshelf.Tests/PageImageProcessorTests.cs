using Inkshelf.Convert;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Inkshelf.Tests;

public class PageImageProcessorTests
{
    private static byte[] Img(int w, int h, SixLabors.ImageSharp.Formats.IImageEncoder enc)
    {
        using var img = new Image<Rgba32>(w, h);
        using var ms = new MemoryStream(); img.Save(ms, enc); return ms.ToArray();
    }

    private static byte[] Solid(int w, int h, byte r, byte g, byte b, SixLabors.ImageSharp.Formats.IImageEncoder enc)
    {
        using var img = new Image<Rgba32>(w, h, new Rgba32(r, g, b));
        using var ms = new MemoryStream(); img.Save(ms, enc); return ms.ToArray();
    }

    [Fact]
    public async Task ProcessAsync_transcodes_webp_to_jpeg_keeping_size()
    {
        var r = await PageImageProcessor.ProcessAsync(Img(80, 120, new WebpEncoder()), ".webp", 0, 0, grayscale: false, default);
        Assert.Equal(".jpg", r.Extension);
        Assert.Equal(80, r.Width);
        Assert.Equal(120, r.Height);
    }

    [Fact]
    public async Task ProcessAsync_downscales_oversized_keeping_aspect()
    {
        var r = await PageImageProcessor.ProcessAsync(Img(400, 600, new JpegEncoder()), ".jpg", 200, 200, grayscale: false, default);
        Assert.True(r.Width <= 200 && r.Height <= 200, $"got {r.Width}×{r.Height}");
        Assert.Equal(".jpg", r.Extension);
    }

    [Fact]
    public async Task ProcessAsync_passes_in_bounds_image_through_untouched()
    {
        var bytes = Img(80, 120, new JpegEncoder());
        var r = await PageImageProcessor.ProcessAsync(bytes, ".jpg", 0, 0, grayscale: false, default);
        Assert.Same(bytes, r.Bytes);      // no re-encode
        Assert.Equal(".jpg", r.Extension);
        Assert.Equal(80, r.Width);
        Assert.Equal(120, r.Height);
    }

    [Fact]
    public async Task ProcessAsync_grayscale_desaturates_in_bounds_image()
    {
        var red = Solid(80, 120, 255, 0, 0, new JpegEncoder());
        var r = await PageImageProcessor.ProcessAsync(red, ".jpg", 0, 0, grayscale: true, default);
        Assert.NotSame(red, r.Bytes); // re-encoded, not passed through
        Assert.Equal(".jpg", r.Extension);

        using var outImg = Image.Load<Rgba32>(r.Bytes);
        var px = outImg[40, 60];
        Assert.True(Math.Abs(px.R - px.G) <= 4 && Math.Abs(px.G - px.B) <= 4,
            $"expected gray, got ({px.R},{px.G},{px.B})");
    }

    [Fact]
    public async Task ProcessAsync_non_grayscale_still_passes_in_bounds_through()
    {
        var bytes = Solid(80, 120, 255, 0, 0, new JpegEncoder());
        var r = await PageImageProcessor.ProcessAsync(bytes, ".jpg", 0, 0, grayscale: false, default);
        Assert.Same(bytes, r.Bytes);
    }
}
