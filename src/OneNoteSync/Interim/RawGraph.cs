// Interim: raw byte requests to Graph with a retry policy per call, on M365Graph's token. M365Graph's GraphClient
// only sends and returns JSON (StringContent adds "; charset=utf-8", and every call is retried alike), so this
// belongs in M365Graph as a "SendRawAsync(method, url, bytes, contentType, policy)"; delete this once it's there.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using M365Graph;

namespace OneNoteSync.Interim;

/// <summary>How a call may be retried. A GET can always be sent again. A PATCH or POST is sent again only when
/// it certainly wasn't applied: a 429 (refused before it was looked at), or a connection that never opened.</summary>
public sealed record RetryPolicy(int Tries, bool SafeToRepeat)
{
    public static readonly RetryPolicy Get = new(5, true);
    public static readonly RetryPolicy Write = new(5, false);
}

public sealed class RawGraph
{
    private static readonly HashSet<int> Retryable = [429, 500, 502, 503, 504];

    private readonly Func<CancellationToken, Task<string>> token;
    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<DateTimeOffset> now;

    public RawGraph(Func<CancellationToken, Task<string>> token, HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? now = null)
    {
        this.token = token;
        this.http = http;
        this.delay = delay ?? Task.Delay;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>An HttpClient as M365Graph's (IPv4 first, 60-second timeout), but asking for no compression, as
    /// Python's urllib didn't: the page's bytes arrive exactly as served.</summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = Net.CreateHandler();
        handler.AutomaticDecompression = DecompressionMethods.None;
        return Net.CreateHttpClient(handler);
    }

    /// <summary>Send, and return the reply's bytes. url is relative to Graph's v1.0 root ("/me/onenote/..."), as
    /// error messages quote it. Throws OneNoteException for an error answer, DeliveryUnknownException when a write
    /// may have been applied but got no answer, or the network error itself for a GET that kept failing.</summary>
    public async Task<byte[]> SendAsync(HttpMethod method, string url, byte[]? body, string? contentType, RetryPolicy policy,
        CancellationToken cancel = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, Graph.BaseUrl + url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(cancel).ConfigureAwait(false));
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                if (contentType is not null)
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            TimeSpan wait;
            try
            {
                using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
                var raw = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return raw;
                var status = (int)response.StatusCode;
                var detail = Encoding.UTF8.GetString(raw);
                detail = detail.Length > 300 ? detail[..300] : detail;
                var message = $"OneNote said {status} to {method.Method} {(url.Length > 80 ? url[..80] : url)}: {detail}";
                var retryable = policy.SafeToRepeat ? Retryable.Contains(status) : status == 429;
                if (!retryable || attempt == policy.Tries)
                {
                    if (!policy.SafeToRepeat && status >= 500)
                        throw new DeliveryUnknownException($"{message} (not sent again: it may have been applied)");
                    throw new OneNoteException(status, message);
                }
                wait = RetryAfter(response) ?? TimeSpan.FromSeconds(5 * attempt);
            }
            catch (Exception error) when (error is not OneNoteException and not DeliveryUnknownException && Net.IsNetworkError(error, cancel))
            {
                if (!policy.SafeToRepeat && !NeverSent(error))
                    throw new DeliveryUnknownException(
                        $"No answer to {method.Method} {(url.Length > 80 ? url[..80] : url)} ({error.GetType().Name}: {error.Message}); "
                        + "not sent again, because it may have been applied.", error);
                if (attempt == policy.Tries)
                    throw;
                wait = TimeSpan.FromSeconds(5 * attempt);
            }
            await delay(wait, cancel).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the request certainly never reached Graph: the name didn't resolve or no connection opened.
    /// (M365Graph's IPv4-first connect throws its own HttpRequestException, "Couldn't connect to ...", which .NET
    /// passes through without an HttpRequestError; the library should set ConnectionError on it.)</summary>
    internal static bool NeverSent(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError })
                return true;
            if (e is HttpRequestException && e.Message.StartsWith("Couldn't connect to ", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Retry-After as seconds or as an HTTP date (the Python's int() would have crashed on a date).</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
        {
            var until = date - now();
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }
        return null;
    }
}
