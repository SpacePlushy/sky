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

    /// <summary>Requests one group once.</summary>
    internal async Task<FetchOutcome> FetchGroupAsync(string group, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(GroupUri(group), cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new FetchOutcome.Answered((int)response.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            return new FetchOutcome.Unreachable(ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new FetchOutcome.Unreachable($"Timed out: {ex.Message}");
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
