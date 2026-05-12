using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GrokMcp.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrokMcp.Services;

public class GrokClient
{
    // Per-instance and internal-settable so tests can zero-out the wall-clock delays.
    // Production: every GrokClient instance starts with the same [0s, 2s, 6s] schedule.
    internal TimeSpan[] _retryDelays =
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6),
    };

    private readonly HttpClient _http;
    private readonly GrokOptions _opts;
    private readonly ILogger<GrokClient> _log;

    public GrokClient(HttpClient http, IOptions<GrokOptions> opts, ILogger<GrokClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;

        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
            throw new InvalidOperationException("XAI_API_KEY environment variable is required.");

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("grok-mcp/1.0");
    }

    public async Task<ChatResult> ChatAsync(
        IEnumerable<object> messages,
        string? model,
        float temperature,
        CancellationToken ct)
    {
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? _opts.ChatModel : model;
        var msgList = messages.ToList();
        var body = new
        {
            model = resolvedModel,
            messages = msgList,
            temperature = temperature,
        };
        var url = $"{_opts.ApiBaseUrl.TrimEnd('/')}/chat/completions";
        return await PostChatAsync(url, body, resolvedModel, ct);
    }

    public async Task<string> VisionAsync(
        string prompt,
        IEnumerable<object> imageObjects,
        string? model,
        string detail,
        float temperature,
        CancellationToken ct)
    {
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? _opts.CreativeModel : model;
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var img in imageObjects)
        {
            // img is { url = "..." } from ImageInputResolver
            var url = img.GetType().GetProperty("url")!.GetValue(img)!;
            content.Add(new { type = "image_url", image_url = new { url, detail } });
        }
        var messages = new[] { new { role = "user", content = content } };
        var body = new
        {
            model = resolvedModel,
            messages = messages,
            temperature = temperature,
        };
        var endpoint = $"{_opts.ApiBaseUrl.TrimEnd('/')}/chat/completions";
        var result = await PostChatAsync(endpoint, body, resolvedModel, ct);
        return result.Content;
    }

    public record ChatResult(string Content, int PromptTokens, int CompletionTokens, int TotalTokens);

    public async Task<List<byte[]>> ImagesAsync(
        string prompt,
        string? model,
        int n,
        string? aspectRatio,
        string responseFormat,
        IReadOnlyList<object>? inputs,
        CancellationToken ct)
    {
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? _opts.ImageModel : model;
        var hasInputs = inputs != null && inputs.Count > 0;

        var body = new Dictionary<string, object>
        {
            ["model"] = resolvedModel,
            ["prompt"] = prompt,
            ["n"] = n,
            ["response_format"] = responseFormat,
        };
        if (!string.IsNullOrWhiteSpace(aspectRatio))
            body["aspect_ratio"] = aspectRatio;

        if (hasInputs)
        {
            if (inputs!.Count == 1)
                body["image"] = inputs[0];
            else
                body["images"] = inputs;
        }

        var endpoint = hasInputs
            ? $"{_opts.ApiBaseUrl.TrimEnd('/')}/images/edits"
            : $"{_opts.ApiBaseUrl.TrimEnd('/')}/images/generations";

        var json = await PostWithRetryAsync(endpoint, body, resolvedModel, prompt, ct);

        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var results = new List<byte[]>(data.GetArrayLength());

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var b64Prop))
            {
                results.Add(Convert.FromBase64String(b64Prop.GetString() ?? ""));
            }
            else if (item.TryGetProperty("url", out var urlProp))
            {
                var bytes = await _http.GetByteArrayAsync(urlProp.GetString(), ct);
                results.Add(bytes);
            }
        }
        return results;
    }

    private async Task<ChatResult> PostChatAsync(string endpoint, object body, string model, CancellationToken ct)
    {
        var promptPreview = TryExtractFirstUserText(body);
        var json = await PostWithRetryAsync(endpoint, body, model, promptPreview, ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int promptTokens = -1, completionTokens = -1, totalTokens = -1;
        if (root.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : -1;
            completionTokens = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : -1;
            totalTokens = usage.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : -1;
            _log.LogInformation("Grok [{Model}] tokens — prompt: {Prompt}, completion: {Completion}, total: {Total}",
                model, promptTokens, completionTokens, totalTokens);
        }

        var content = root
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        return new ChatResult(content, promptTokens, completionTokens, totalTokens);
    }

    private async Task<string> PostWithRetryAsync(
        string endpoint,
        object body,
        string model,
        string promptPreview,
        CancellationToken ct)
    {
        Exception? lastEx = null;
        var requestJson = JsonSerializer.Serialize(body);
        var preview = promptPreview.Length > 100 ? promptPreview.Substring(0, 100) + "..." : promptPreview;

        for (var attempt = 1; attempt <= _retryDelays.Length; attempt++)
        {
            if (attempt > 1)
                await Task.Delay(_retryDelays[attempt - 1], ct);

            _log.LogDebug("Grok call (attempt {N}/{Max}) → {Endpoint}, model={Model}, payload={Bytes}B, prompt={Preview}",
                attempt, _retryDelays.Length, endpoint, model, requestJson.Length, preview);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

            HttpResponseMessage response;
            string responseBody;
            try
            {
                response = await _http.SendAsync(request, ct);
                responseBody = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                lastEx = ex;
                _log.LogWarning("Grok network failure (attempt {N}/{Max}, model={Model}): {Err}",
                    attempt, _retryDelays.Length, model, ex.Message);
                continue;
            }

            if (response.IsSuccessStatusCode)
                return responseBody;

            var status = (int)response.StatusCode;
            var transient = status >= 500 || status == 429;
            var bodyPreview = responseBody?.Length > 300 ? responseBody.Substring(0, 300) + "..." : responseBody;
            _log.LogWarning("Grok HTTP {Status} (attempt {N}/{Max}, model={Model}): {Body}",
                response.StatusCode, attempt, _retryDelays.Length, model, bodyPreview);
            lastEx = new HttpRequestException($"Grok HTTP {status}: {bodyPreview}");
            response.Dispose();

            if (!transient) break;
        }

        throw new InvalidOperationException(
            $"Grok call failed after {_retryDelays.Length} attempts (model={model}). Last error: {lastEx?.Message}",
            lastEx);
    }

    private static string TryExtractFirstUserText(object body)
    {
        try
        {
            var msgs = body.GetType().GetProperty("messages")?.GetValue(body) as System.Collections.IEnumerable;
            if (msgs == null) return "";
            foreach (var m in msgs)
            {
                var role = m.GetType().GetProperty("role")?.GetValue(m) as string;
                if (role != "user") continue;
                var content = m.GetType().GetProperty("content")?.GetValue(m);
                if (content is string s) return s;
                // Multimodal: list of content parts
                if (content is System.Collections.IEnumerable parts)
                {
                    foreach (var part in parts)
                    {
                        var type = part.GetType().GetProperty("type")?.GetValue(part) as string;
                        if (type == "text")
                            return part.GetType().GetProperty("text")?.GetValue(part) as string ?? "";
                    }
                }
            }
        }
        catch { /* preview is best-effort */ }
        return "";
    }
}
