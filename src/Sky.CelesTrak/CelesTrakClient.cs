using System.Net;
using System.Net.Http.Headers;

namespace Sky.CelesTrak;

/// <summary>Downloads GP data from CelesTrak. It makes exactly one request per call and never retries.</summary>
/// <remarks>
/// Rate limits and error handling live in <see cref="GpCache"/>, which decides whether a request
/// may be made at all. See https://celestrak.org/usage-policy.php.
/// </remarks>
public sealed class CelesTrakClient(HttpClient httpClient)
{
    /// <summary>Creates the HTTP handler Sky uses for CelesTrak.</summary>
    /// <remarks>
    /// Redirects are not followed. CelesTrak counts a 301 toward its firewall limit and the .com
    /// domain redirects, so a redirect must surface as an error instead of being hidden.
    /// </remarks>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
    };

    /// <summary>Creates an HTTP client with <see cref="CreateHandler"/>, a 30 s timeout, and a descriptive User-Agent.</summary>
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(CreateHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sky", "0.1"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/SpacePlushy/sky)"));
        return client;
    }

    /// <summary>The request URL for a group, in JSON (OMM) format.</summary>
    internal static Uri GroupUri(string group) =>
        new($"https://celestrak.org/NORAD/elements/gp.php?GROUP={group}&FORMAT=JSON");

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Requests one group once.</summary>
    /// <remarks>
    /// The status is read before the body. Once CelesTrak has answered with a status, that answer
    /// stands even if the body is lost: a non-200 still blocks the group (with the read error in
    /// place of the body), and a 200 whose body is lost counts as a network failure.
    /// </remarks>
    internal async Task<FetchOutcome> FetchGroupAsync(string group, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(GroupUri(group), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new FetchOutcome.Unreachable(ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new FetchOutcome.Unreachable($"Timed out: {ex.Message}");
        }

        using (response)
        {
            int status = (int)response.StatusCode;
            using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bodyTimeout.CancelAfter(BodyTimeout);
            try
            {
                string body = await response.Content.ReadAsStringAsync(bodyTimeout.Token).ConfigureAwait(false);
                return new FetchOutcome.Answered(status, body);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                // A non-200 status has arrived, so CelesTrak has answered: that answer must be
                // recorded (and block the group) even if the caller cancelled while its body was
                // read. Only a 200 whose body is lost is treated as unreachable, or as cancelled.
                if (status != 200)
                {
                    return new FetchOutcome.Answered(status, $"(the response body could not be read: {ex.Message})");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new FetchOutcome.Unreachable($"The download was interrupted: {ex.Message}");
            }
        }
    }
}

/// <summary>What happened when Sky asked CelesTrak for a group.</summary>
internal abstract record FetchOutcome
{
    /// <summary>CelesTrak answered with an HTTP status and body.</summary>
    internal sealed record Answered(int Status, string Body) : FetchOutcome;

    /// <summary>No HTTP answer: network failure or timeout.</summary>
    internal sealed record Unreachable(string Reason) : FetchOutcome;
}
