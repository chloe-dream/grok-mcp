using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace GrokMcp.Tests.Fakes;

// Test double for HttpClient that lets each test enqueue a sequence of responses
// and inspect what requests were made.
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    public List<Recorded> Requests { get; } = new();

    public sealed record Recorded(HttpMethod Method, Uri Uri, string Body, HttpRequestHeaders Headers);

    public void EnqueueJson(HttpStatusCode status, string body)
    {
        _responders.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
    }

    public void EnqueueStatus(HttpStatusCode status, string body = "")
    {
        _responders.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        });
    }

    public void EnqueueException(Exception ex)
    {
        _responders.Enqueue(_ => throw ex);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new Recorded(request.Method, request.RequestUri!, body, request.Headers));

        if (_responders.Count == 0)
            throw new InvalidOperationException(
                $"FakeHttpMessageHandler: no response queued for {request.Method} {request.RequestUri}");

        return _responders.Dequeue().Invoke(request);
    }
}
