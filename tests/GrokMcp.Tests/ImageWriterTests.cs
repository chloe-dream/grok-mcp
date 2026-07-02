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

    // "ftyp" box: 4-byte size, then ASCII "ftyp" at offset 4 (ISO base media / MP4 container).
    private static readonly byte[] Mp4Bytes = { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D };
    private static readonly byte[] NotAVideo = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

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

    // ----- WriteVideo -----

    [Fact]
    public void WriteVideo_rejects_relative_paths()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _writer.WriteVideo("relative.mp4", Mp4Bytes));
        Assert.Contains("absolute path", ex.Message);
    }

    [Fact]
    public void WriteVideo_matching_mp4_extension_is_kept()
    {
        var target = Path.Combine(_tmp, "clip.mp4");
        var saved = _writer.WriteVideo(target, Mp4Bytes);
        Assert.Equal(target, saved.Path);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void WriteVideo_mismatched_known_video_extension_is_replaced()
    {
        var target = Path.Combine(_tmp, "clip.mov");
        var saved = _writer.WriteVideo(target, Mp4Bytes);
        var expected = Path.Combine(_tmp, "clip.mp4");
        Assert.Equal(expected, saved.Path);
        Assert.False(File.Exists(target), "Old .mov path must not exist after replace");
    }

    [Fact]
    public void WriteVideo_no_extension_appends_mp4()
    {
        var target = Path.Combine(_tmp, "output");
        var saved = _writer.WriteVideo(target, Mp4Bytes);
        var expected = Path.Combine(_tmp, "output.mp4");
        Assert.Equal(expected, saved.Path);
    }

    [Fact]
    public void WriteVideo_unknown_extension_appends_mp4()
    {
        var target = Path.Combine(_tmp, "data.bin");
        var saved = _writer.WriteVideo(target, Mp4Bytes);
        var expected = Path.Combine(_tmp, "data.bin.mp4");
        Assert.Equal(expected, saved.Path);
    }

    [Fact]
    public void WriteVideo_unrecognized_bytes_leave_path_unchanged()
    {
        var target = Path.Combine(_tmp, "clip.mp4");
        var saved = _writer.WriteVideo(target, NotAVideo);
        Assert.Equal(target, saved.Path);
    }

    [Fact]
    public void WriteVideo_empty_bytes_throws()
    {
        var target = Path.Combine(_tmp, "clip.mp4");
        Assert.Throws<ArgumentException>(() => _writer.WriteVideo(target, Array.Empty<byte>()));
    }

    [Fact]
    public void WriteVideo_creates_missing_parent_directory()
    {
        var target = Path.Combine(_tmp, "nested", "deep", "clip.mp4");
        var saved = _writer.WriteVideo(target, Mp4Bytes);
        Assert.True(File.Exists(saved.Path));
    }
}
