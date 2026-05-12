using System.Net;
using System.Net.Http;
using GrokMcp.Config;
using GrokMcp.Services;
using GrokMcp.Tests.Fakes;
using GrokMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace GrokMcp.Tests;

// These tests compose the real services (GrokTools → GrokClient → ImageInputResolver → ImageWriter)
// but replace the HttpMessageHandler so no real HTTP traffic happens. Validation paths and
// resolver-error paths assert that no HTTP call is made.
public class GrokToolsTests : IDisposable
{
    private const string ApiBase = "https://api.example.test/v1";
    private readonly string _tmp;

    private readonly FakeHttpMessageHandler _handler = new();
    private readonly GrokTools _tools;
    private readonly ChatSessionStore _sessions;

    private const string SuccessChatJson = """
        {
          "choices": [{"message": {"content": "answer"}}],
          "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2}
        }
        """;

    public GrokToolsTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "grok-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);

        var http = new HttpClient(_handler);
        var opts = Options.Create(new GrokOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = ApiBase,
            ChatModel = "test-chat",
            CreativeModel = "test-vision",
            ImageModel = "test-image",
            SessionTurnCap = 10,
        });
        var grok = new GrokClient(http, opts, NullLogger<GrokClient>.Instance);
        grok._retryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };

        _sessions = new ChatSessionStore(opts);
        var resolver = new ImageInputResolver();
        var writer = new ImageWriter();
        _tools = new GrokTools(grok, _sessions, resolver, writer, NullLogger<GrokTools>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    // ----- Validation paths (must short-circuit before any HTTP call) -----

    [Fact]
    public async Task GrokChat_empty_message_returns_error_and_makes_no_http_call()
    {
        var result = await _tools.GrokChat("");
        AssertError(result, "message must not be empty");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokGenerateImage_empty_prompt_returns_error()
    {
        var result = await _tools.GrokGenerateImage("", Path.Combine(_tmp, "x.jpg"));
        AssertError(result, "prompt must not be empty");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokGenerateImage_n_out_of_range_returns_error()
    {
        var result = await _tools.GrokGenerateImage("p", Path.Combine(_tmp, "x.jpg"), n: 99);
        AssertError(result, "n must be between 1 and 10");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokEditImage_empty_images_returns_error()
    {
        var result = await _tools.GrokEditImage("p", Array.Empty<string>(), Path.Combine(_tmp, "x.jpg"));
        AssertError(result, "images must contain at least one entry");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokEditImage_n_out_of_range_returns_error()
    {
        var result = await _tools.GrokEditImage(
            "p",
            new[] { "https://example.com/a.jpg" },
            Path.Combine(_tmp, "x.jpg"),
            n: 9);
        AssertError(result, "n must be between 1 and 4");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokDescribeImage_empty_prompt_returns_error()
    {
        var result = await _tools.GrokDescribeImage("", new[] { "https://example.com/a.jpg" });
        AssertError(result, "prompt must not be empty");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokDescribeImage_missing_local_file_returns_error_with_path_in_message()
    {
        var missing = Path.Combine(_tmp, "absent.jpg");
        var result = await _tools.GrokDescribeImage("describe", new[] { missing });
        AssertError(result, missing);
        Assert.Empty(_handler.Requests);
    }

    // ----- Success paths -----

    [Fact]
    public async Task GrokChat_success_returns_assistant_text()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await _tools.GrokChat("hi");

        Assert.False(result.IsError ?? false);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]);
        Assert.Equal("answer", text.Text);
    }

    [Fact]
    public async Task GrokChat_with_session_id_appends_user_and_assistant_turns()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var session = $"test-session-{Guid.NewGuid():N}";
        await _tools.GrokChat("first message", session_id: session);

        var snap = _sessions.Snapshot(session);
        Assert.Equal(2, snap.Count);
        Assert.Equal("user", snap[0].Role);
        Assert.Equal("first message", snap[0].Content);
        Assert.Equal("assistant", snap[1].Role);
        Assert.Equal("answer", snap[1].Content);
    }

    [Fact]
    public async Task GrokChat_reset_session_clears_prior_turns_before_running()
    {
        var session = $"test-session-{Guid.NewGuid():N}";
        _sessions.Append(session, new ChatTurn("user", "old"));
        _sessions.Append(session, new ChatTurn("assistant", "older"));

        _handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);
        await _tools.GrokChat("fresh", session_id: session, reset_session: true);

        var snap = _sessions.Snapshot(session);
        Assert.Equal(2, snap.Count);
        Assert.Equal("fresh", snap[0].Content);
        Assert.Equal("answer", snap[1].Content);
    }

    [Fact]
    public async Task GrokChat_grok_failure_returns_error_result_with_message()
    {
        // 4xx breaks out of retry → bubbles up as InvalidOperationException → caught and wrapped.
        _handler.EnqueueStatus(HttpStatusCode.BadRequest, "bad request body");

        var result = await _tools.GrokChat("hi");

        AssertError(result, "Chat failed");
    }

    // ----- Helpers -----

    private static void AssertError(CallToolResult result, string expectedFragment)
    {
        Assert.True(result.IsError, "Expected IsError = true on result");
        var text = Assert.IsType<TextContentBlock>(result.Content[0]);
        Assert.Contains(expectedFragment, text.Text);
    }
}
