using System.IO;

namespace GrokMcp.Services;

public class ImageWriter
{
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
            File.WriteAllBytes(outputPath, images[0]);
            results.Add(new SavedImage(outputPath, images[0]));
            return results;
        }

        var basePath = Path.GetDirectoryName(outputPath) ?? "";
        var nameNoExt = Path.GetFileNameWithoutExtension(outputPath);
        var ext = Path.GetExtension(outputPath);
        for (var i = 0; i < images.Count; i++)
        {
            var p = Path.Combine(basePath, $"{nameNoExt}-{i + 1}{ext}");
            File.WriteAllBytes(p, images[i]);
            results.Add(new SavedImage(p, images[i]));
        }
        return results;
    }
}
