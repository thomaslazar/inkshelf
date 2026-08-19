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
        var r = (await PageImageProcessor.ProcessAsync(Img(80, 120, new WebpEncoder()), ".webp", 0, 0, grayscale: false))[0];
        Assert.Equal(".jpg", r.Extension);
        Assert.Equal(80, r.Width);
        Assert.Equal(120, r.Height);
    }

    [Fact]
    public async Task ProcessAsync_downscales_oversized_keeping_aspect()
    {
        var r = (await PageImageProcessor.ProcessAsync(Img(400, 600, new JpegEncoder()), ".jpg", 200, 200, grayscale: false))[0];
        Assert.True(r.Width <= 200 && r.Height <= 200, $"got {r.Width}×{r.Height}");
        Assert.Equal(".jpg", r.Extension);
    }

    [Fact]
    public async Task ProcessAsync_passes_in_bounds_image_through_untouched()
    {
        var bytes = Img(80, 120, new JpegEncoder());
        var r = (await PageImageProcessor.ProcessAsync(bytes, ".jpg", 0, 0, grayscale: false))[0];
        Assert.Same(bytes, r.Bytes);      // no re-encode
        Assert.Equal(".jpg", r.Extension);
        Assert.Equal(80, r.Width);
        Assert.Equal(120, r.Height);
    }

    [Fact]
    public async Task ProcessAsync_grayscale_desaturates_in_bounds_image()
    {
        var red = Solid(80, 120, 255, 0, 0, new JpegEncoder());
        var r = (await PageImageProcessor.ProcessAsync(red, ".jpg", 0, 0, grayscale: true))[0];
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
        var r = (await PageImageProcessor.ProcessAsync(bytes, ".jpg", 0, 0, grayscale: false))[0];
        Assert.Same(bytes, r.Bytes);
    }

    [Fact]
    public async Task ProcessAsync_splits_a_wide_spread_into_two_pages()
    {
        var r = await PageImageProcessor.ProcessAsync(Img(400, 300, new JpegEncoder()), ".jpg",
            0, 0, grayscale: false, SpreadMode.Split);
        Assert.Equal(2, r.Length);
        Assert.All(r, i => Assert.Equal(200, i.Width));
        Assert.All(r, i => Assert.Equal(300, i.Height));
    }

    [Fact]
    public async Task ProcessAsync_split_takes_the_left_half_first()
    {
        // Left half red, right half blue — the first emitted page must be the red one.
        using var img = new Image<Rgba32>(400, 300, new Rgba32(255, 0, 0));
        for (var x = 200; x < 400; x++)
            for (var y = 0; y < 300; y++) img[x, y] = new Rgba32(0, 0, 255);
        using var ms = new MemoryStream(); img.Save(ms, new JpegEncoder());

        var r = await PageImageProcessor.ProcessAsync(ms.ToArray(), ".jpg", 0, 0,
            grayscale: false, SpreadMode.Split);
        Assert.Equal(2, r.Length);
        using var left = Image.Load<Rgba32>(r[0].Bytes);
        using var right = Image.Load<Rgba32>(r[1].Bytes);
        Assert.True(left[100, 150].R > 200 && left[100, 150].B < 60, $"left was {left[100, 150]}");
        Assert.True(right[100, 150].B > 200 && right[100, 150].R < 60, $"right was {right[100, 150]}");
    }

    [Fact]
    public async Task ProcessAsync_rotate_makes_a_wide_spread_portrait()
    {
        var r = (await PageImageProcessor.ProcessAsync(Img(400, 300, new JpegEncoder()), ".jpg",
            0, 0, grayscale: false, SpreadMode.Rotate))[0];
        Assert.Equal(300, r.Width);
        Assert.Equal(400, r.Height);
    }

    [Theory]
    [InlineData(SpreadMode.Fit)]
    [InlineData(SpreadMode.Rotate)]
    public async Task ProcessAsync_padToBox_letterboxes_to_exactly_the_box(SpreadMode mode)
    {
        // The invariant a real e-reader depends on: whatever comes in, the page that
        // comes out is exactly the box, so every page in a book is the same size.
        var r = (await PageImageProcessor.ProcessAsync(Img(400, 300, new JpegEncoder()), ".jpg",
            200, 400, grayscale: false, mode, padToBox: true))[0];
        Assert.Equal(200, r.Width);
        Assert.Equal(400, r.Height);
    }

    [Fact]
    public async Task ProcessAsync_padToBox_letterboxes_both_split_halves()
    {
        var r = await PageImageProcessor.ProcessAsync(Img(400, 300, new JpegEncoder()), ".jpg",
            200, 400, grayscale: false, SpreadMode.Split, padToBox: true);
        Assert.Equal(2, r.Length);
        Assert.All(r, i => Assert.Equal((200, 400), (i.Width, i.Height)));
    }

    [Fact]
    public async Task ProcessAsync_padToBox_leaves_an_exact_fit_on_the_passthrough_path()
    {
        var bytes = Img(200, 400, new JpegEncoder());
        var r = (await PageImageProcessor.ProcessAsync(bytes, ".jpg", 200, 400,
            grayscale: false, SpreadMode.Fit, padToBox: true))[0];
        Assert.Same(bytes, r.Bytes);
    }

    [Fact]
    public async Task ProcessAsync_without_padToBox_does_not_letterbox()
    {
        var bytes = Img(400, 300, new JpegEncoder());
        var r = (await PageImageProcessor.ProcessAsync(bytes, ".jpg", 0, 0,
            grayscale: false, SpreadMode.Fit))[0];
        Assert.Same(bytes, r.Bytes);
    }
}
