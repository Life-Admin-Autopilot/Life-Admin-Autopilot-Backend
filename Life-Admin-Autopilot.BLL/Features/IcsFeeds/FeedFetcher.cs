using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Life_Admin_Autopilot.BLL.Features.IcsFeeds;

/// <summary>Publishers' cache validators, stored per feed and replayed on the next poll.</summary>
public readonly record struct FeedCacheState(string? Etag, string? LastModified);

public enum FeedFetchStatus
{
    /// <summary>Publisher answered 304. No body, no parse, no writes.</summary>
    Unchanged,

    Ok,

    /// <summary>404/410. TERMINAL — a retired URL does not start working again.</summary>
    Gone,

    Error,
}

/// <param name="Body">Only set for <see cref="FeedFetchStatus.Ok"/>.</param>
/// <param name="Reason">Only set for <c>Gone</c> and <c>Error</c>. Surfaced to the user verbatim.</param>
public readonly record struct FeedFetchResult(
    FeedFetchStatus Status,
    string? Body = null,
    string? Etag = null,
    string? LastModified = null,
    string? Reason = null);

/// <summary>
/// Port of <c>server/src/modules/integrations/ics/fetchFeed.ts</c> — fetch a
/// calendar feed, cheaply and safely.
///
/// <para>
/// ICS has no push mechanism — there is no watch channel and no webhook anywhere in
/// RFC 5545, so polling is the only option. What makes that affordable is the
/// conditional GET: the publisher's ETag / Last-Modified is replayed, and a feed
/// that has not changed answers 304 with an empty body. A school-term feed changes
/// perhaps three times a year, so almost every poll costs one round trip and no
/// parsing and no writes.
/// </para>
///
/// <para>
/// <b>Redirects are followed MANUALLY and every hop is re-vetted.</b> A publisher
/// URL that passes the SSRF check can still redirect into private space, and an
/// auto-following HTTP client would chase it with no opportunity to look — which
/// defeats the entire guard. <see cref="FeedFetcherOptions.HttpClientName"/>'s
/// handler must therefore have redirect following DISABLED; see
/// <c>IcsFeedsFeature</c>.
/// </para>
/// </summary>
public sealed class FeedFetcher
{
    /// <summary>A term of school events is tens of KB. A megabyte is already anomalous.</summary>
    public const int MaxFeedBytes = 5 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public const int MaxRedirects = 3;

    /// <summary>
    /// Some council endpoints 403 an absent UA. Identifying honestly also gives a
    /// publisher someone to contact if the polling ever bothers them.
    /// </summary>
    public const string UserAgent = "Kitto/1.0 (calendar feed subscriber)";

    public const string Accept = "text/calendar, text/plain;q=0.9, */*;q=0.5";

    private static readonly Regex VCalendarSentinel =
        new("BEGIN:VCALENDAR", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _clients;
    private readonly FeedUrlGuard _guard;
    private readonly TimeProvider _time;

    public FeedFetcher(IHttpClientFactory clients, FeedUrlGuard guard, TimeProvider? time = null)
    {
        _clients = clients;
        _guard = guard;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// A 200 that is not iCalendar is almost always a login or error page — an
    /// expired or auth-gated feed URL commonly answers 200 with a sign-in form.
    /// Parsed as ICS that yields zero events, indistinguishable from "term has no
    /// dates", so every reminder silently disappears.
    ///
    /// <para>
    /// Checks the BODY rather than the Content-Type header on purpose: plenty of
    /// councils serve valid .ics as <c>text/plain</c> or
    /// <c>application/octet-stream</c>, so the header is too unreliable to gate on,
    /// while the <c>BEGIN:VCALENDAR</c> sentinel is mandatory.
    /// </para>
    /// </summary>
    public static bool LooksLikeICalendar(string body) =>
        VCalendarSentinel.IsMatch(body.Length > 2048 ? body[..2048] : body);

    public async Task<FeedFetchResult> FetchAsync(
        string rawUrl,
        FeedCacheState cache = default,
        CancellationToken cancellationToken = default)
    {
        Uri current;
        try
        {
            current = await _guard
                .AssertPublicFeedUrlAsync(FeedUrlGuard.NormalizeFeedUrl(rawUrl), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnsafeFeedUrlException ex)
        {
            return Error(ex.Message);
        }
        catch (Exception)
        {
            return Error("That address is unusable.");
        }

        var client = _clients.CreateClient(FeedFetcherOptions.HttpClientName);

        for (var hop = 0; hop <= MaxRedirects; hop += 1)
        {
            using var request = BuildRequest(current, cache);

            // A per-hop deadline, matching `AbortSignal.timeout(15_000)` inside the
            // reference's loop — the budget is per request, not per fetchFeed call.
            using var deadline = new CancellationTokenSource(Timeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

            HttpResponseMessage response;
            try
            {
                response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return Error("That feed took too long to respond.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Error("That feed could not be reached.");
            }

            using (response)
            {
                var status = (int)response.StatusCode;

                if (status == 304)
                {
                    return new FeedFetchResult(FeedFetchStatus.Unchanged);
                }

                if (status is >= 300 and < 400)
                {
                    var location = FirstHeader(response, "Location");
                    if (string.IsNullOrEmpty(location))
                    {
                        return Error("That feed sent an invalid redirect.");
                    }

                    try
                    {
                        // Resolve relative Locations against the current hop, then
                        // re-run the FULL guard — scheme, credentials and DNS.
                        var next = new Uri(current, location);
                        current = await _guard
                            .AssertPublicFeedUrlAsync(
                                FeedUrlGuard.NormalizeFeedUrl(next.AbsoluteUri),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (UnsafeFeedUrlException ex)
                    {
                        return Error(ex.Message);
                    }
                    catch (Exception)
                    {
                        return Error("That redirect is unusable.");
                    }

                    continue;
                }

                if (status is 404 or 410)
                {
                    return new FeedFetchResult(FeedFetchStatus.Gone, Reason: "That feed no longer exists.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Error($"That feed returned {status}.");
                }

                string? body;
                try
                {
                    body = await ReadCappedAsync(response, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    return Error("That feed took too long to respond.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    return Error("That feed could not be reached.");
                }

                if (body is null)
                {
                    return Error("That feed is too large.");
                }

                if (!LooksLikeICalendar(body))
                {
                    return Error("That address did not return a calendar.");
                }

                return new FeedFetchResult(
                    FeedFetchStatus.Ok,
                    body,
                    FirstHeader(response, "ETag"),
                    FirstHeader(response, "Last-Modified"));
            }
        }

        return Error("That feed redirected too many times.");
    }

    private static HttpRequestMessage BuildRequest(Uri url, FeedCacheState cache)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("accept", Accept);
        request.Headers.TryAddWithoutValidation("user-agent", UserAgent);

        // Conditional GET. Sent on every hop, as in the reference — a redirect chain
        // that ends at an unchanged resource should still be able to answer 304.
        if (!string.IsNullOrEmpty(cache.Etag))
        {
            request.Headers.TryAddWithoutValidation("if-none-match", cache.Etag);
        }

        if (!string.IsNullOrEmpty(cache.LastModified))
        {
            request.Headers.TryAddWithoutValidation("if-modified-since", cache.LastModified);
        }

        return request;
    }

    /// <summary>
    /// Read a response body with a hard byte ceiling, so a huge feed cannot exhaust
    /// memory. Returns null when the ceiling is breached.
    /// </summary>
    private static async Task<string?> ReadCappedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var declared = response.Content.Headers.ContentLength ?? 0;
        if (declared > MaxFeedBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new ArrayBufferWriter<byte>(Math.Min(MaxFeedBytes, 64 * 1024));
        var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(rented, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                // Content-Length is a hint, not a promise — enforce while reading too.
                if (buffer.WrittenCount + read > MaxFeedBytes)
                {
                    return null;
                }

                buffer.Write(rented.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string? FirstHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return values.FirstOrDefault();
        }

        return response.Content.Headers.TryGetValues(name, out var contentValues)
            ? contentValues.FirstOrDefault()
            : null;
    }

    private static FeedFetchResult Error(string reason) =>
        new(FeedFetchStatus.Error, Reason: reason);
}

/// <summary>Names the HttpClient the fetcher resolves. See <c>IcsFeedsFeature</c>.</summary>
public static class FeedFetcherOptions
{
    public const string HttpClientName = "ics-feed";
}
