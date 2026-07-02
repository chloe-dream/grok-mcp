using System.IO;

namespace GrokMcp.Services;

public class ImageWriter
{
    private static readonly HashSet<string> KnownImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp"
    };

    private static readonly HashSet<string> KnownVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".webm", ".avi", ".mkv"
    };

    public record SavedImage(string Path, byte[] Bytes);

    public List<SavedImage> WriteAll(string outputPath, IReadOnlyList<byte[]> images)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("output_path must not be empty", nameof(outputPath));

        if (!Path.IsPathFullyQualified(outputPath))
            throw new ArgumentException(
                $"output_path must be an absolute path. Got: {outputPath}. " +
                "Reason: the MCP server has its own working directory, separate from the calling agent.",
                nameof(outputPath));

        if (images == null || images.Count == 0)
            throw new ArgumentException("No image bytes to write", nameof(images));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var results = new List<SavedImage>(images.Count);
        if (images.Count == 1)
        {
            var p = EnsureExtension(outputPath, DetectImageExtension(images[0]), KnownImageExtensions);
            File.WriteAllBytes(p, images[0]);
            results.Add(new SavedImage(p, images[0]));
            return results;
        }

        var basePath = Path.GetDirectoryName(outputPath) ?? "";
        var nameNoExt = Path.GetFileNameWithoutExtension(outputPath);
        var ext = Path.GetExtension(outputPath);
        for (var i = 0; i < images.Count; i++)
        {
            var p = Path.Combine(basePath, $"{nameNoExt}-{i + 1}{ext}");
            p = EnsureExtension(p, DetectImageExtension(images[i]), KnownImageExtensions);
            File.WriteAllBytes(p, images[i]);
            results.Add(new SavedImage(p, images[i]));
        }
        return results;
    }

    // Same absolute-path enforcement and extension-reconciliation rules as WriteAll, but for
    // the single MP4 a video generation call produces. Reuses SavedImage (Path, Bytes) rather
    // than adding a near-identical record type.
    public SavedImage WriteVideo(string outputPath, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("output_path must not be empty", nameof(outputPath));

        if (!Path.IsPathFullyQualified(outputPath))
            throw new ArgumentException(
                $"output_path must be an absolute path. Got: {outputPath}. " +
                "Reason: the MCP server has its own working directory, separate from the calling agent.",
                nameof(outputPath));

        if (bytes == null || bytes.Length == 0)
            throw new ArgumentException("No video bytes to write", nameof(bytes));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var p = EnsureExtension(outputPath, DetectVideoExtension(bytes), KnownVideoExtensions);
        File.WriteAllBytes(p, bytes);
        return new SavedImage(p, bytes);
    }

    // Make the on-disk extension reflect the real bytes.
    // - matching ext → keep as-is
    // - mismatched but known ext for this media kind (e.g. .png on JPG bytes) → replace
    // - no ext or unknown ext → append the real one
    private static string EnsureExtension(string path, string? detected, HashSet<string> knownExtensions)
    {
        if (detected is null) return path;
        if (path.EndsWith(detected, StringComparison.OrdinalIgnoreCase)) return path;

        var currentExt = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(currentExt) && knownExtensions.Contains(currentExt))
            return Path.ChangeExtension(path, detected);

        return path + detected;
    }

    // Return the file extension implied by the bytes' magic header, or null if unrecognized.
    private static string? DetectImageExtension(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 4) return null;
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38) return ".gif";
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return ".webp";
        return null;
    }

    // MP4 (and other ISO base media format containers) start with a 4-byte box size followed
    // by the ASCII box type "ftyp" at offset 4. Best-effort: bytes not recognized as MP4 are
    // left with the path unchanged, same as unknown image formats in DetectImageExtension.
    private static string? DetectVideoExtension(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 8) return null;
        if (bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70) return ".mp4";
        return null;
    }
}
