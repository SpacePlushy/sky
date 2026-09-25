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

    /// <summary>Runs when a request arrives, before it is answered.</summary>
    public Action<Uri>? OnRequest { get; set; }

    /// <summary>How long each response is held back, so concurrent callers overlap.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>The next response has this status, but its body fails partway through reading.</summary>
    public ScriptedHandler RespondWithBrokenBody(HttpStatusCode status)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StreamContent(new BrokenStream()) });
        return this;
    }

    public ScriptedHandler Respond(HttpStatusCode status, string body)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body) });
        return this;
    }

    /// <summary>The next response has this status, but its body never arrives until the request is cancelled.</summary>
    public ScriptedHandler RespondWithHangingBody(HttpStatusCode status)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StreamContent(new HangingStream()) });
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
        OnRequest?.Invoke(request.RequestUri!);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

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

    /// <summary>A stream whose reads wait until they are cancelled, like a body that stalls mid-transfer.</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream that fails as soon as it is read, like a connection reset mid-body.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Simulated connection reset.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
