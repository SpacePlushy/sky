using System.Net;

namespace Sky.CelesTrak.Tests;

/// <summary>
/// A fake HTTP server for tests. Each request takes the next scripted response and is recorded,
/// so tests can assert exactly which requests were made and when.
/// </summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    private bool _hang;

    public List<Uri> Requests { get; } = [];

    public ScriptedHandler Respond(HttpStatusCode status, string body)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body) });
        return this;
    }

    /// <summary>The next request hangs until it is cancelled, like a run interrupted mid-download.</summary>
    public ScriptedHandler HangUntilCancelled()
    {
        _hang = true;
        return this;
    }

    public ScriptedHandler FailWithNetworkError()
    {
        _responses.Enqueue(() => throw new HttpRequestException("Simulated network failure."));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        if (_hang)
        {
            _hang = false;
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"Unexpected request to {request.RequestUri}: no response was scripted.");
        }

        return _responses.Dequeue()();
    }
}
