using GrokMcp.Services;

namespace GrokMcp.Tests;

public class ImageWriterTests : IDisposable
{
    private readonly string _tmp;
    private readonly ImageWriter _writer = new();

    // Minimal valid format headers — enough for magic-byte detection.
    private static readonly byte[] PngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };
    private static readonly byte[] JpgBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x00 };
    private static readonly byte[] GifBytes = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00 };
    private static readonly byte[] WebpBytes = { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x00 };

    public ImageWriterTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "grok-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    [Fact]
    public void Rejects_relative_paths()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _writer.WriteAll("relative.jpg", new[] { JpgBytes }));
        Assert.Contains("absolute path", ex.Message);
    }

    [Fact]
    public void Matching_extension_is_kept()
    {
        var target = Path.Combine(_tmp, "watch.jpg");
        var saved = _writer.WriteAll(target, new[] { JpgBytes });
        Assert.Equal(target, saved[0].Path);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void Mismatched_known_extension_is_replaced()
    {
        var target = Path.Combine(_tmp, "watch.png");
        var saved = _writer.WriteAll(target, new[] { JpgBytes });
        var expected = Path.Combine(_tmp, "watch.jpg");
        Assert.Equal(expected, saved[0].Path);
        Assert.False(File.Exists(target), "Old .png path must not exist after replace");
    }

    [Fact]
    public void No_extension_appends_detected()
    {
        var target = Path.Combine(_tmp, "logo");
        var saved = _writer.WriteAll(target, new[] { PngBytes });
        var expected = Path.Combine(_tmp, "logo.png");
        Assert.Equal(expected, saved[0].Path);
    }

    [Fact]
    public void Unknown_extension_appends_detected()
    {
        var target = Path.Combine(_tmp, "data.bin");
        var saved = _writer.WriteAll(target, new[] { WebpBytes });
        var expected = Path.Combine(_tmp, "data.bin.webp");
        Assert.Equal(expected, saved[0].Path);
    }

    [Fact]
    public void Multiple_images_get_indexed_then_extension_reconciled()
    {
        var target = Path.Combine(_tmp, "v.png");
        var saved = _writer.WriteAll(target, new[] { JpgBytes, GifBytes });
        Assert.Equal(2, saved.Count);
        Assert.Equal(Path.Combine(_tmp, "v-1.jpg"), saved[0].Path);
        Assert.Equal(Path.Combine(_tmp, "v-2.gif"), saved[1].Path);
    }
}
