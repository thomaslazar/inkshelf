using Inkshelf.Convert;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;

namespace Inkshelf.Tests;

public class PageImageProcessorTests
{
    private static byte[] Img(int w, int h, SixLabors.ImageSharp.Formats.IImageEncoder enc)
    {
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        using var ms = new MemoryStream(); img.Save(ms, enc); return ms.ToArray();
    }

    [Fact]
    public async Task ProcessAsync_transcodes_webp_to_jpeg_keeping_size()
    {
        var r = await PageImageProcessor.ProcessAsync(Img(80, 120, new WebpEncoder()), ".webp", 0, 0, default);
        Assert.Equal(".jpg", r.Extension);
        Assert.Equal(80, r.Width);
        Assert.Equal(120, r.Height);
    }

    [Fact]
    public async Task ProcessAsync_downscales_oversized_keeping_aspect()
    {
        var r = await PageImageProcessor.ProcessAsync(Img(400, 600, new JpegEncoder()), ".jpg", 200, 200, default);
        Assert.True(r.Width <= 200 && r.Height <= 200, $"got {r.Width}×{r.Height}");
        Assert.Equal(".jpg", r.Extension);
    }

    [Fact]
    public async Task ProcessAsync_passes_in_bounds_image_through_untouched()
    {
        var bytes = Img(80, 120, new JpegEncoder());
        var r = await PageImageProcessor.ProcessAsync(bytes, ".jpg", 0, 0, default);
        Assert.Same(bytes, r.Bytes);      // no re-encode
        Assert.Equal(".jpg", r.Extension);
        Assert.Equal(80, r.Width);
        Assert.Equal(120, r.Height);
    }
}
