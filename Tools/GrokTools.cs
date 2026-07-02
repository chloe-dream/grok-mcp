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
    public async Task<CallToolResult> GrokChat(
        [Description("User message (required).")] string message,
        [Description("Optional system prompt prepended to the conversation. On a session, replaces any prior system prompt.")] string? system = null,
        [Description("Model override. Defaults to grok-4.3 (xAI's flagship: 1M context, vision, function calling, reasoning). Alternatives: grok-4.20-0309-reasoning / grok-4.20-0309-non-reasoning, grok-4.20-multi-agent-0309 (multi-agent, 1M context), grok-build-0.1 (coding-focused, 256k context).")] string? model = null,
        [Description("Sampling temperature 0.0-2.0. Default 0.7.")] float temperature = 0.7f,
        [Description("Optional session id. Same id reuses in-memory chat history within this server process. Omit for stateless one-shot. When set, also routes to the same xAI server for prompt-prefix caching (cached input is billed at ~16% of normal).")] string? session_id = null,
        [Description("If true, clears the named session before this turn. No effect when session_id is null.")] bool reset_session = false,
        [Description("Reasoning depth. On grok-4.3: one of 'none', 'low', 'medium', 'high' (xAI default 'low'); use 'none' for cheap/fast non-reasoning calls, 'high' for hard problems. 'xhigh' is only meaningful on grok-4.20-multi-agent-0309, where it controls agent count (4 vs 16) rather than reasoning depth. Omit to let xAI default apply.")] string? reasoning_effort = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Error("message must not be empty.");
        if (!string.IsNullOrWhiteSpace(reasoning_effort) &&
            reasoning_effort is not ("none" or "low" or "medium" or "high" or "xhigh"))
            return Error("reasoning_effort must be one of: none, low, medium, high, xhigh.");

        try
        {
            var messages = new List<object>();

            if (!string.IsNullOrEmpty(session_id))
            {
                if (reset_session) _sessions.Reset(session_id);

                if (!string.IsNullOrWhiteSpace(system))
                    messages.Add(new { role = "system", content = system });

                foreach (var turn in _sessions.Snapshot(session_id))
                    messages.Add(new { role = turn.Role, content = turn.Content });

                messages.Add(new { role = "user", content = message });

                var result = await _grok.ChatAsync(messages, model, temperature, reasoning_effort, session_id, ct);

                _sessions.Append(session_id, new ChatTurn("user", message));
                _sessions.Append(session_id, new ChatTurn("assistant", result.Content));
                return Text(result.Content);
            }

            // Stateless single-shot
            if (!string.IsNullOrWhiteSpace(system))
                messages.Add(new { role = "system", content = system });
            messages.Add(new { role = "user", content = message });

            var oneShot = await _grok.ChatAsync(messages, model, temperature, reasoning_effort, conversationId: null, ct);
            return Text(oneShot.Content);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "grok_chat failed");
            return Error($"Chat failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "grok_generate_image"), Description(
        "Generate one or more images with Grok and save them to disk. " +
        "Saves the bytes to the caller-supplied output_path (must be absolute) and ALSO returns inline image content so the calling agent can see them. " +
        "When n>1, output_path is treated as a template: '-1', '-2', ... is inserted before the extension.")]
    public async Task<CallToolResult> GrokGenerateImage(
        [Description("The image prompt (required).")] string prompt,
        [Description("Absolute filesystem path where the image is saved. Required. For n>1 the index is inserted before the extension. Parent directory will be created if missing.")] string output_path,
        [Description("Number of images. 1-10. Default 1.")] int n = 1,
        [Description("Aspect ratio. One of 1:1, 3:4, 4:3, 9:16, 16:9, 2:3, 3:2, 9:19.5, 19.5:9, 9:20, 20:9, 1:2, 2:1, or 'auto'. Default '1:1'.")] string aspect_ratio = "1:1",
        [Description("Image resolution: '1k' or '2k'. Omit for xAI default.")] string? resolution = null,
        [Description("Model override. Defaults to grok-imagine-image. Use grok-imagine-image-quality for higher-quality output (flat per-image pricing).")] string? model = null,
        [Description("'b64_json' (default) or 'url'. The server fetches URL bytes either way.")] string response_format = "b64_json",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("prompt must not be empty.");
        if (n < 1 || n > 10)
            return Error("n must be between 1 and 10.");
        if (!string.IsNullOrWhiteSpace(resolution) && resolution is not ("1k" or "2k"))
            return Error("resolution must be '1k' or '2k'.");

        try
        {
            var bytesList = await _grok.ImagesAsync(prompt, model, n, aspect_ratio, resolution, response_format, inputs: null, ct);
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
        [Description("Aspect ratio. One of 1:1, 3:4, 4:3, 9:16, 16:9, 2:3, 3:2, 9:19.5, 19.5:9, 9:20, 20:9, 1:2, 2:1, or 'auto'. Default '1:1'.")] string aspect_ratio = "1:1",
        [Description("Image resolution: '1k' or '2k'. Omit for xAI default.")] string? resolution = null,
        [Description("Model override. Defaults to grok-imagine-image. Use grok-imagine-image-quality for higher-quality output (flat per-image pricing).")] string? model = null,
        [Description("'b64_json' (default) or 'url'.")] string response_format = "b64_json",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("prompt must not be empty.");
        if (images == null || images.Length == 0)
            return Error("images must contain at least one entry.");
        if (n < 1 || n > 4)
            return Error("n must be between 1 and 4 for edits.");
        if (!string.IsNullOrWhiteSpace(resolution) && resolution is not ("1k" or "2k"))
            return Error("resolution must be '1k' or '2k'.");

        try
        {
            var resolved = images.Select(_imgResolver.Resolve).ToList();
            var bytesList = await _grok.ImagesAsync(prompt, model, n, aspect_ratio, resolution, response_format, resolved, ct);
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
    public async Task<CallToolResult> GrokDescribeImage(
        [Description("The question or instruction (e.g. 'Describe this image', 'Read the text', 'What style is this art in?'). Required.")] string prompt,
        [Description("One or more images to analyze. Required.")] string[] images,
        [Description("Vision-capable model. Defaults to grok-4.3.")] string? model = null,
        [Description("Detail level passed through to xAI ('low' | 'high' | 'auto'). Default 'auto'.")] string detail = "auto",
        [Description("Sampling temperature. Default 0.2 for descriptive accuracy.")] float temperature = 0.2f,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("prompt must not be empty.");
        if (images == null || images.Length == 0)
            return Error("images must contain at least one entry.");

        try
        {
            var resolved = images.Select(_imgResolver.Resolve).ToList();
            var text = await _grok.VisionAsync(prompt, resolved, model, detail, temperature, ct);
            return Text(text);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "grok_describe_image failed");
            return Error($"Describe failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "grok_generate_video"), Description(
        "Generate a video with Grok from a text prompt, optionally seeded with a starting image (image-to-video). " +
        "Video generation is asynchronous on xAI's side; this call polls until the video is ready, " +
        "which can block for up to several minutes (poll interval 5s, 10-minute timeout before failing). " +
        "Saves the result as an MP4 to output_path (must be absolute). " +
        "Returns a text summary only — video bytes are not returned inline.")]
    public async Task<CallToolResult> GrokGenerateVideo(
        [Description("The video prompt (required).")] string prompt,
        [Description("Absolute filesystem path where the MP4 is saved. Required. Parent directory will be created if missing.")] string output_path,
        [Description("Video duration in seconds. 1-15. Default 8.")] int duration = 8,
        [Description("Aspect ratio. One of 1:1, 16:9, 9:16, 4:3, 3:4, 3:2, 2:3. Default '16:9'.")] string aspect_ratio = "16:9",
        [Description("Resolution. One of '480p', '720p', '1080p'. Default '720p'.")] string resolution = "720p",
        [Description("Model override. Default is chosen per call: grok-imagine-video-1.5 when a seed image is provided (image-to-video), grok-imagine-video for text-to-video (grok-imagine-video-1.5 rejects text-to-video). Set GROK_MCP_VIDEO_MODEL in config.env to pin one model.")] string? model = null,
        [Description("Optional starting image for image-to-video. Same resolution rules as grok_edit_image: http(s) URL, absolute file path, data: URI, or raw base64.")] string? image = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("prompt must not be empty.");
        if (duration < 1 || duration > 15)
            return Error("duration must be between 1 and 15.");
        if (resolution is not ("480p" or "720p" or "1080p"))
            return Error("resolution must be one of: 480p, 720p, 1080p.");
        // Check up front — video generation can take minutes, so fail before spending that
        // time on a call whose output we couldn't save anyway.
        if (!System.IO.Path.IsPathFullyQualified(output_path))
            return Error(
                $"output_path must be an absolute path. Got: {output_path}. " +
                "Reason: the MCP server has its own working directory, separate from the calling agent.");

        try
        {
            object? imageInput = string.IsNullOrWhiteSpace(image) ? null : _imgResolver.Resolve(image);
            var result = await _grok.VideosAsync(prompt, model, duration, aspect_ratio, resolution, imageInput, ct);
            var saved = _imgWriter.WriteVideo(output_path, result.Bytes);

            var summary = result.DurationSeconds is { } d
                ? $"Saved video to: {saved.Path} (duration: {d}s)"
                : $"Saved video to: {saved.Path}";
            return Text(summary);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "grok_generate_video failed");
            return Error($"Video generation failed: {ex.Message}");
        }
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

    private static CallToolResult Text(string text)
    {
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = text } },
            IsError = false,
        };
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
