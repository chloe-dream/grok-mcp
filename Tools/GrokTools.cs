using System.ComponentModel;
using GrokMcp.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GrokMcp.Tools;

[McpServerToolType]
public class GrokTools
{
    private readonly GrokClient _grok;
    private readonly ChatSessionStore _sessions;
    private readonly ImageInputResolver _imgResolver;
    private readonly ImageWriter _imgWriter;
    private readonly ILogger<GrokTools> _log;

    public GrokTools(
        GrokClient grok,
        ChatSessionStore sessions,
        ImageInputResolver imgResolver,
        ImageWriter imgWriter,
        ILogger<GrokTools> log)
    {
        _grok = grok;
        _sessions = sessions;
        _imgResolver = imgResolver;
        _imgWriter = imgWriter;
        _log = log;
    }

    [McpServerTool(Name = "grok_chat"), Description(
        "Send a chat completion to xAI Grok. Stateless by default. " +
        "Pass session_id to enable an in-memory thread that persists for the lifetime of this server process; " +
        "the same session_id on subsequent calls appends to and replays prior turns. " +
        "Returns the assistant text only.")]
    public async Task<string> GrokChat(
        [Description("User message (required).")] string message,
        [Description("Optional system prompt prepended to the conversation. On a session, replaces any prior system prompt.")] string? system = null,
        [Description("Model override. Defaults to grok-3-mini (fast). Use grok-4-latest for heavy/creative work.")] string? model = null,
        [Description("Sampling temperature 0.0-2.0. Default 0.7.")] float temperature = 0.7f,
        [Description("Optional session id. Same id reuses in-memory chat history within this server process. Omit for stateless one-shot.")] string? session_id = null,
        [Description("If true, clears the named session before this turn. No effect when session_id is null.")] bool reset_session = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("message must not be empty.", nameof(message));

        var messages = new List<object>();

        if (!string.IsNullOrEmpty(session_id))
        {
            if (reset_session) _sessions.Reset(session_id);

            if (!string.IsNullOrWhiteSpace(system))
                messages.Add(new { role = "system", content = system });

            foreach (var turn in _sessions.Snapshot(session_id))
                messages.Add(new { role = turn.Role, content = turn.Content });

            messages.Add(new { role = "user", content = message });

            var result = await _grok.ChatAsync(messages, model, temperature, ct);

            _sessions.Append(session_id, new ChatTurn("user", message));
            _sessions.Append(session_id, new ChatTurn("assistant", result.Content));
            return result.Content;
        }

        // Stateless single-shot
        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new { role = "system", content = system });
        messages.Add(new { role = "user", content = message });

        var oneShot = await _grok.ChatAsync(messages, model, temperature, ct);
        return oneShot.Content;
    }

    [McpServerTool(Name = "grok_generate_image"), Description(
        "Generate one or more images with Grok and save them to disk. " +
        "Saves the bytes to the caller-supplied output_path (must be absolute) and ALSO returns inline image content so the calling agent can see them. " +
        "When n>1, output_path is treated as a template: '-1', '-2', ... is inserted before the extension.")]
    public async Task<CallToolResult> GrokGenerateImage(
        [Description("The image prompt (required).")] string prompt,
        [Description("Absolute filesystem path where the image is saved. Required. For n>1 the index is inserted before the extension. Parent directory will be created if missing.")] string output_path,
        [Description("Number of images. 1-10. Default 1.")] int n = 1,
        [Description("Aspect ratio, e.g. '1:1', '3:2', '16:9', '9:16'. Default '1:1'.")] string aspect_ratio = "1:1",
        [Description("Model override. Defaults to grok-imagine-image.")] string? model = null,
        [Description("'b64_json' (default) or 'url'. The server fetches URL bytes either way.")] string response_format = "b64_json",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("prompt must not be empty.");
        if (n < 1 || n > 10)
            return Error("n must be between 1 and 10.");

        try
        {
            var bytesList = await _grok.ImagesAsync(prompt, model, n, aspect_ratio, response_format, inputs: null, ct);
            return SaveAndPackage(output_path, bytesList);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "grok_generate_image failed");
            return Error($"Image generation failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "grok_edit_image"), Description(
        "Edit / composite one or more input images with a prompt. " +
        "Inputs are resolved per-item: an http(s) URL is forwarded as-is to xAI; an absolute filesystem path is read and base64-encoded; " +
        "a string starting with 'data:' is forwarded; any other string is treated as raw base64 (wrapped as a data URI). " +
        "Saves the result to output_path and returns the inline image.")]
    public async Task<CallToolResult> GrokEditImage(
        [Description("Edit instructions / prompt (required).")] string prompt,
        [Description("One or more input images. Each item may be http(s) URL, absolute file path, data: URI, or raw base64. Required.")] string[] images,
        [Description("Absolute output path for the resulting image (required).")] string output_path,
        [Description("Number of result images. 1-4. Default 1.")] int n = 1,
        [Description("Aspect ratio. Default '1:1'.")] string aspect_ratio = "1:1",
        [Description("Model override. Defaults to grok-imagine-image.")] string? model = null,
        [Description("'b64_json' (default) or 'url'.")] string response_format = "b64_json",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("prompt must not be empty.");
        if (images == null || images.Length == 0)
            return Error("images must contain at least one entry.");
        if (n < 1 || n > 4)
            return Error("n must be between 1 and 4 for edits.");

        try
        {
            var resolved = images.Select(_imgResolver.Resolve).ToList();
            var bytesList = await _grok.ImagesAsync(prompt, model, n, aspect_ratio, response_format, resolved, ct);
            return SaveAndPackage(output_path, bytesList);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "grok_edit_image failed");
            return Error($"Image edit failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "grok_describe_image"), Description(
        "Vision: ask Grok to describe / analyze one or more images. " +
        "Inputs follow the same resolution rules as grok_edit_image (URL / absolute path / data URI / raw base64). " +
        "Returns plain text.")]
    public async Task<string> GrokDescribeImage(
        [Description("The question or instruction (e.g. 'Describe this image', 'Read the text', 'What style is this art in?'). Required.")] string prompt,
        [Description("One or more images to analyze. Required.")] string[] images,
        [Description("Vision-capable model. Defaults to grok-4-latest.")] string? model = null,
        [Description("Detail level passed through to xAI ('low' | 'high' | 'auto'). Default 'auto'.")] string detail = "auto",
        [Description("Sampling temperature. Default 0.2 for descriptive accuracy.")] float temperature = 0.2f,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("prompt must not be empty.", nameof(prompt));
        if (images == null || images.Length == 0)
            throw new ArgumentException("images must contain at least one entry.", nameof(images));

        var resolved = images.Select(_imgResolver.Resolve).ToList();
        return await _grok.VisionAsync(prompt, resolved, model, detail, temperature, ct);
    }

    private CallToolResult SaveAndPackage(string outputPath, IReadOnlyList<byte[]> bytesList)
    {
        var saved = _imgWriter.WriteAll(outputPath, bytesList);
        var content = new List<ContentBlock>();

        var pathSummary = saved.Count == 1
            ? $"Saved image to: {saved[0].Path}"
            : "Saved images to:\n" + string.Join("\n", saved.Select(s => $"  - {s.Path}"));
        content.Add(new TextContentBlock { Text = pathSummary });

        foreach (var s in saved)
        {
            content.Add(ImageContentBlock.FromBytes(s.Bytes, "image/png"));
        }

        return new CallToolResult { Content = content, IsError = false };
    }

    private static CallToolResult Error(string message)
    {
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
            IsError = true,
        };
    }
}
