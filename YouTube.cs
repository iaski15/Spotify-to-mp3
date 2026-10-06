using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpotifyToMp3;

public sealed class YouTubeException(string message) : Exception(message);

public sealed record MediaFormat(string Url, string Mime, long Size, int Height, long Bitrate);

public sealed record VideoInfo(string Id, string Title, string Uploader, int LengthSeconds, IReadOnlyList<MediaFormat> Formats)
{
    public string ThumbnailUrl => $"https://i.ytimg.com/vi/{Id}/hqdefault.jpg";

    public MediaFormat BestAudio() =>
        Pick(Formats.Where(f => f.Mime.StartsWith("audio/mp4")))
        ?? Pick(Formats.Where(f => f.Mime.StartsWith("audio/")))
        ?? throw new YouTubeException("No downloadable audio stream for this video.");

    // h264 first: plays everywhere without the AV1/VP9 store extensions.
    public MediaFormat BestMp4Video() =>
        Pick(Formats.Where(f => f.Mime.StartsWith("video/mp4") && f.Mime.Contains("avc1")))
        ?? Pick(Formats.Where(f => f.Mime.StartsWith("video/mp4")))
        ?? throw new YouTubeException("No downloadable MP4 video stream for this video.");

    private static MediaFormat? Pick(IEnumerable<MediaFormat> fs) =>
        fs.OrderByDescending(f => f.Height).ThenByDescending(f => f.Bitrate).FirstOrDefault();
}

/// <summary>
/// Talks to YouTube's InnerTube player API directly, posing as the visionOS app.
/// That client gets plain stream URLs back - no signature cipher, no JS player, no PO token.
/// ponytail: lives as long as YouTube leaves the VISIONOS client alone; when it breaks, swap the
/// client block below for whatever yt-dlp's _base.py INNERTUBE_CLIENTS currently marks REQUIRE_JS_PLAYER=False.
/// </summary>
public static class YouTube
{
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
    private const string ClientName = "VISIONOS";
    private const string ClientVersion = "1.02";
    private const string ClientId = "101";

    // YouTube throttles long single requests; yt-dlp uses the same 10 MB range chunks.
    private const long ChunkSize = 10 * 1024 * 1024;

    private static readonly HttpClient Http = new();

    // Requests without a visitor ID get "confirm you're not a bot" on many videos; the homepage hands one out.
    private static string? _visitorData;

    private static readonly Regex IdRegex = new(
        @"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/live/|/v/)([A-Za-z0-9_-]{11})", RegexOptions.Compiled);

    public static string? ParseVideoId(string url)
    {
        var m = IdRegex.Match(url);
        if (m.Success) return m.Groups[1].Value;
        url = url.Trim();
        return Regex.IsMatch(url, "^[A-Za-z0-9_-]{11}$") ? url : null;
    }

    public static async Task<VideoInfo> GetVideoAsync(string url, CancellationToken ct)
    {
        var id = ParseVideoId(url) ?? throw new YouTubeException("That doesn't look like a valid YouTube link.");

        _visitorData ??= await FetchVisitorDataAsync(ct);

        var body = JsonSerializer.Serialize(new
        {
            context = new
            {
                client = new
                {
                    clientName = ClientName,
                    clientVersion = ClientVersion,
                    deviceMake = "Apple",
                    deviceModel = "RealityDevice17,1",
                    osName = "visionOS",
                    osVersion = "26.5.23O471",
                    userAgent = UserAgent,
                    visitorData = _visitorData,
                    hl = "en",
                    gl = "US"
                }
            },
            videoId = id,
            contentCheckOk = true,
            racyCheckOk = true
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Add("X-YouTube-Client-Name", ClientId);
        req.Headers.Add("X-YouTube-Client-Version", ClientVersion);
        req.Headers.Add("Origin", "https://www.youtube.com");
        if (_visitorData is not null) req.Headers.Add("X-Goog-Visitor-Id", _visitorData);

        using var resp = await Http.SendAsync(req, ct);
        if ((int)resp.StatusCode == 429)
            throw new YouTubeException("YouTube is rate-limiting this connection. Wait a bit and try again.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;

        var status = root.TryGetProperty("playabilityStatus", out var ps) ? ps : default;
        var state = status.ValueKind == JsonValueKind.Object ? Str(status, "status") : null;
        if (state != "OK")
            throw MapPlayability(state, status.ValueKind == JsonValueKind.Object ? Str(status, "reason") : null);

        var details = root.GetProperty("videoDetails");
        if (details.TryGetProperty("isLive", out var live) && live.GetBoolean())
            throw new YouTubeException("Live streams aren't supported.");

        var formats = new List<MediaFormat>();
        if (root.TryGetProperty("streamingData", out var sd) && sd.TryGetProperty("adaptiveFormats", out var af))
        {
            foreach (var f in af.EnumerateArray())
            {
                // Ciphered formats need YouTube's JS player to unscramble - skip them.
                if (Str(f, "url") is not { } u) continue;
                formats.Add(new MediaFormat(
                    u,
                    Str(f, "mimeType") ?? "",
                    long.TryParse(Str(f, "contentLength"), out var len) ? len : 0,
                    f.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                    f.TryGetProperty("bitrate", out var br) ? br.GetInt64() : 0));
            }
        }
        if (formats.Count == 0)
            throw new YouTubeException("YouTube returned no downloadable streams for this video.");

        return new VideoInfo(
            id,
            Str(details, "title") ?? "video",
            Str(details, "author") ?? "",
            int.TryParse(Str(details, "lengthSeconds"), out var secs) ? secs : 0,
            formats);
    }

    /// <summary>
    /// InnerTube search as the WEB client (videos only). Returns the top hits, closest length to
    /// <paramref name="targetSeconds"/> first - weeds out music videos with long intros, live cuts, 1-hour loops.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SearchVideoIdsAsync(string query, int targetSeconds, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB", clientVersion = "2.20250925.01.00", hl = "en", gl = "US" } },
            query,
            @params = "EgIQAQ%3D%3D" // "type: video" filter, same value yt-dlp sends
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/search?prettyPrint=false")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.UserAgent.ParseAdd(UserAgent);
        using var resp = await Http.SendAsync(req, ct);
        if ((int)resp.StatusCode == 429)
            throw new YouTubeException("YouTube is rate-limiting this connection. Wait a bit and try again.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
        var hits = new List<(string Id, int Seconds)>();
        CollectVideos(doc.RootElement, hits);
        if (hits.Count == 0) throw new YouTubeException("No matching song was found on YouTube.");

        var top = hits.Take(5);
        return (targetSeconds > 0 ? top.OrderBy(h => Math.Abs(h.Seconds - targetSeconds)) : top).Select(h => h.Id).ToList();
    }

    private static void CollectVideos(JsonElement e, List<(string, int)> hits)
    {
        if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) CollectVideos(x, hits);
        if (e.ValueKind != JsonValueKind.Object) return;

        if (e.TryGetProperty("videoRenderer", out var vr) && Str(vr, "videoId") is { } id)
        {
            var len = vr.TryGetProperty("lengthText", out var lt) ? Str(lt, "simpleText") : null;
            hits.Add((id, len is null ? 0 : len.Split(':').Aggregate(0, (acc, p) => acc * 60 + (int.TryParse(p, out var n) ? n : 0))));
            return;
        }
        foreach (var p in e.EnumerateObject()) CollectVideos(p.Value, hits);
    }

    private static async Task<string?> FetchVisitorDataAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/");
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var resp = await Http.SendAsync(req, ct);
            var m = Regex.Match(await resp.Content.ReadAsStringAsync(ct), "\"VISITOR_DATA\":\"([^\"]+)\"");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }
        catch (HttpRequestException)
        {
            return null; // the player request still works for many videos without it
        }
    }

    /// <summary>Downloads a stream in ranged chunks; <paramref name="onBytes"/> gets each written byte count.</summary>
    public static async Task DownloadAsync(MediaFormat format, string path, Action<long> onBytes, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buf = new byte[1 << 16];
        long pos = 0;
        var failures = 0;

        do
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, format.Url);
                req.Headers.UserAgent.ParseAdd(UserAgent);
                if (format.Size > 0)
                    req.Headers.Range = new RangeHeaderValue(pos, Math.Min(pos + ChunkSize, format.Size) - 1);

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode == 403)
                    throw new YouTubeException("YouTube refused the download (403). The stream link may have expired - try again.");
                resp.EnsureSuccessStatusCode();

                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                int n;
                while ((n = await s.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    pos += n;
                    onBytes(n);
                }
                if (format.Size <= 0) break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested && ++failures <= 5)
            {
                // Dropped connection mid-chunk: the next request resumes from pos.
                await Task.Delay(1000 * failures, ct);
            }
        } while (pos < format.Size);
    }

    private static YouTubeException MapPlayability(string? status, string? reason)
    {
        var r = (reason ?? "").ToLowerInvariant();
        if (r.Contains("private")) return new("That video is private.");
        if (r.Contains("members") || r.Contains("join this channel")) return new("That video is members-only.");
        if (r.Contains("age") || r.Contains("inappropriate")) return new("That video is age-restricted and needs sign-in.");
        if (r.Contains("bot")) return new("YouTube is asking for a bot-check. Try again later or from another network.");
        if (status == "ERROR" || r.Contains("unavailable")) return new("That video is unavailable (deleted or region-locked).");
        return new(reason is { Length: > 0 } ? reason : $"YouTube won't play this video ({status ?? "no status"}).");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

