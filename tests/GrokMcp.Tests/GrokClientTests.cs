using System.Net;
using System.Net.Http;
using GrokMcp.Config;
using GrokMcp.Services;
using GrokMcp.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrokMcp.Tests;

public class GrokClientTests
{
    private const string ApiBase = "https://api.example.test/v1";

    private static (GrokClient Client, FakeHttpMessageHandler Handler) Build()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler);
        var opts = Options.Create(new GrokOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = ApiBase,
            ChatModel = "test-chat",
            CreativeModel = "test-vision",
            ImageModel = "test-image",
        });
        var client = new GrokClient(http, opts, NullLogger<GrokClient>.Instance);
        // Zero out retry delays so transient-error tests don't wait on the wall clock.
        client._retryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };
        return (client, handler);
    }

    private const string SuccessChatJson = """
        {
          "choices": [{"message": {"content": "hello back"}}],
          "usage": {"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15}
        }
        """;

    [Fact]
    public async Task ChatAsync_success_returns_content_and_token_usage()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            model: null,
            temperature: 0.7f,
            reasoningEffort: null,
            conversationId: null,
            CancellationToken.None);

        Assert.Equal("hello back", result.Content);
        Assert.Equal(10, result.PromptTokens);
        Assert.Equal(5, result.CompletionTokens);
        Assert.Equal(15, result.TotalTokens);
        Assert.Single(handler.Requests);
        Assert.Equal($"{ApiBase}/chat/completions", handler.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task ChatAsync_4xx_breaks_out_of_retry_loop_immediately()
    {
        var (client, handler) = Build();
        handler.EnqueueStatus(HttpStatusCode.BadRequest, "bad payload");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, null, CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ChatAsync_5xx_then_success_retries_and_returns()
    {
        var (client, handler) = Build();
        handler.EnqueueStatus(HttpStatusCode.InternalServerError, "boom");
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, null, CancellationToken.None);

        Assert.Equal("hello back", result.Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ChatAsync_429_is_treated_as_transient_and_retries()
    {
        var (client, handler) = Build();
        handler.EnqueueStatus(HttpStatusCode.TooManyRequests, "slow down");
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, null, CancellationToken.None);

        Assert.Equal("hello back", result.Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ChatAsync_all_attempts_fail_throws_with_last_error_message()
    {
        var (client, handler) = Build();
        for (var i = 0; i < 3; i++)
            handler.EnqueueStatus(HttpStatusCode.InternalServerError, $"boom-{i}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, null, CancellationToken.None));

        Assert.Contains("failed after 3 attempts", ex.Message);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ChatAsync_network_failure_is_treated_as_transient_and_retries()
    {
        var (client, handler) = Build();
        handler.EnqueueException(new HttpRequestException("connection refused"));
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, null, CancellationToken.None);

        Assert.Equal("hello back", result.Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ChatAsync_with_reasoning_effort_serializes_into_body()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, reasoningEffort: "high", conversationId: null, CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.Contains("\"reasoning_effort\":\"high\"", req.Body);
    }

    [Fact]
    public async Task ChatAsync_without_reasoning_effort_omits_field()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, reasoningEffort: null, conversationId: null, CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.DoesNotContain("reasoning_effort", req.Body);
    }

    [Fact]
    public async Task ChatAsync_with_conversation_id_sets_x_grok_conv_id_header()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, conversationId: "sess-abc-123", CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.True(req.Headers.TryGetValues("x-grok-conv-id", out var values));
        Assert.Equal("sess-abc-123", values!.Single());
    }

    [Fact]
    public async Task ChatAsync_without_conversation_id_does_not_set_header()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, conversationId: null, CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.False(req.Headers.Contains("x-grok-conv-id"));
    }

    [Fact]
    public async Task ImagesAsync_no_inputs_posts_to_generations_endpoint()
    {
        var (client, handler) = Build();
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var b64 = Convert.ToBase64String(pngBytes);
        handler.EnqueueJson(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{b64}}"}]}""");

        var bytesList = await client.ImagesAsync(
            prompt: "a circle",
            model: null,
            n: 1,
            aspectRatio: "1:1",
            responseFormat: "b64_json",
            inputs: null,
            CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal($"{ApiBase}/images/generations", handler.Requests[0].Uri.ToString());
        Assert.Single(bytesList);
        Assert.Equal(pngBytes, bytesList[0]);
    }

    [Fact]
    public async Task ImagesAsync_with_inputs_posts_to_edits_endpoint()
    {
        var (client, handler) = Build();
        var b64 = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF });
        handler.EnqueueJson(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{b64}}"}]}""");

        var inputs = new object[] { new { url = "data:image/png;base64,AAAA" } };
        var bytesList = await client.ImagesAsync(
            prompt: "make it gold",
            model: null,
            n: 1,
            aspectRatio: "1:1",
            responseFormat: "b64_json",
            inputs: inputs,
            CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal($"{ApiBase}/images/edits", handler.Requests[0].Uri.ToString());
        Assert.Single(bytesList);
    }

    [Fact]
    public async Task ImagesAsync_decodes_multiple_b64_json_entries()
    {
        var (client, handler) = Build();
        var b1 = Convert.ToBase64String(new byte[] { 0x01, 0x02 });
        var b2 = Convert.ToBase64String(new byte[] { 0x03, 0x04 });
        handler.EnqueueJson(HttpStatusCode.OK,
            $$"""{"data":[{"b64_json":"{{b1}}"},{"b64_json":"{{b2}}"}]}""");

        var bytesList = await client.ImagesAsync(
            "p", null, n: 2, "1:1", "b64_json", inputs: null, CancellationToken.None);

        Assert.Equal(2, bytesList.Count);
        Assert.Equal(new byte[] { 0x01, 0x02 }, bytesList[0]);
        Assert.Equal(new byte[] { 0x03, 0x04 }, bytesList[1]);
    }

    [Fact]
    public async Task VisionAsync_posts_to_chat_completions_with_image_url_content()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var images = new object[] { new { url = "https://example.com/x.jpg" } };
        var text = await client.VisionAsync(
            prompt: "what is this",
            imageObjects: images,
            model: null,
            detail: "auto",
            temperature: 0.2f,
            CancellationToken.None);

        Assert.Equal("hello back", text);
        var req = Assert.Single(handler.Requests);
        Assert.Equal($"{ApiBase}/chat/completions", req.Uri.ToString());
        Assert.Contains("image_url", req.Body);
        Assert.Contains("https://example.com/x.jpg", req.Body);
    }

    [Fact]
    public void Constructor_with_empty_api_key_throws()
    {
        var opts = Options.Create(new GrokOptions { ApiKey = "" });
        var http = new HttpClient(new FakeHttpMessageHandler());
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new GrokClient(http, opts, NullLogger<GrokClient>.Instance));
        Assert.Contains("XAI_API_KEY", ex.Message);
    }
}
