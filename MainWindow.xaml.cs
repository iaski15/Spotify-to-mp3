using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NAudio.Wave;

namespace SpotifyToMp3;

public partial class MainWindow : Window
{
    private static readonly string YtDlpPath = Path.Combine(AppContext.BaseDirectory, "Tools", "yt-dlp.exe");

    private static readonly Regex WebUrlRegex = new(
        @"open\.spotify\.com/(?:embed/)?(track|album|playlist)/([A-Za-z0-9]{22})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UriSchemeRegex = new(
        @"^spotify:(track|album|playlist):([A-Za-z0-9]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private CancellationTokenSource? _cts;
    private bool _busy;
    private string? _lastOutputFile;
    private string? _lastFolder;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public MainWindow()
    {
        InitializeComponent();
    }

    // ---- window chrome ----

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- input helpers ----

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.GetText().Trim();
            if (text.Length > 0) UrlBox.Text = text;
        }
        catch { /* clipboard can be locked, ignore */ }

        UrlBox.Focus();
        UrlBox.CaretIndex = UrlBox.Text.Length;
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_busy)
            Convert_Click(sender, new RoutedEventArgs());
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_busy) return;

        if (e.Data.GetData(DataFormats.StringFormat) is string text && text.Trim().Length > 0)
            UrlBox.Text = text.Trim();
    }

    // ---- models ----

    private sealed record TrackEntry(string Id, string Title, string Artist, int DurationMs);

    private sealed record SpotifyEntity(
        string Kind, string Id, string Title, string Artist,
        string? CoverUrl, int DurationMs, IReadOnlyList<TrackEntry> Tracks)
    {
        public bool IsCollection => Tracks.Count > 0;
    }

    private sealed class YtDlpException(string message) : Exception(message);

    private sealed class SpotifyLinkException(string message) : Exception(message);

    // ---- main flow ----

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var raw = UrlBox.Text.Trim().Trim('"', '\'');
        if (raw.Length == 0)
        {
            SetStatus("Paste a Spotify link first.", StatusKind.Info);
            UrlBox.Focus();
            return;
        }

        _busy = true;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        SkippedText.Visibility = Visibility.Collapsed;
        SetBusy(true);

        try
        {
            (var kind, var id) = ParseSpotifyLink(raw);

            SetPhase("Reading Spotify...");
            var entity = await FetchEntityAsync(kind, id);

            if (entity.IsCollection)
                await DownloadCollectionAsync(entity);
            else
                await DownloadSingleTrackAsync(entity);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", StatusKind.Info);
        }
        catch (YtDlpException ex)
        {
            SetStatus(ex.Message, StatusKind.Error);
        }
        catch (SpotifyLinkException ex)
        {
            SetStatus(ex.Message, StatusKind.Error);
        }
        catch (Exception ex)
        {
            SetStatus(FriendlyError(ex), StatusKind.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _busy = false;
            SetBusy(false);
            SetPhase("");
        }
    }

    private static (string Kind, string Id) ParseSpotifyLink(string raw)
    {
        var url = raw.Contains("://") ? raw : "https://" + raw;

        var m = WebUrlRegex.Match(url);
        if (!m.Success) m = UriSchemeRegex.Match(raw);
        if (!m.Success)
            throw new SpotifyLinkException("That doesn't look like a Spotify track, album or playlist link.");

        return (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);
    }

    // ---- spotify metadata (no login needed: the embed page is public) ----

    private async Task<SpotifyEntity> FetchEntityAsync(string kind, string id)
    {
        var html = await _http.GetStringAsync($"https://open.spotify.com/embed/{kind}/{id}");

        var m = Regex.Match(html, "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.+?)</script>", RegexOptions.Singleline);
        if (!m.Success)
            throw new YtDlpException("Spotify didn't return data for this link - it may be invalid or unavailable in your region.");

        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        if (FindEntity(doc.RootElement) is not { } entityEl)
            throw new YtDlpException("Couldn't read that Spotify page. Make sure the link points to a track, album or playlist.");

        var title = Str(entityEl, "title") ?? Str(entityEl, "name") ?? "Unknown";
        var artist = FirstArtistName(entityEl) ?? Str(entityEl, "subtitle") ?? "";
        string? cover = FindCoverUrl(entityEl);
        int durationMs = Int(entityEl, "duration");

        var tracks = new List<TrackEntry>();
        if (entityEl.TryGetProperty("trackList", out var tl) && tl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tl.EnumerateArray())
            {
                var tid = TrackIdFromUri(Str(t, "uri")) ?? Str(t, "id");
                if (tid is null) continue;
                tracks.Add(new TrackEntry(
                    tid,
                    Str(t, "title") ?? Str(t, "name") ?? "",
                    FirstArtistName(t) ?? Str(t, "subtitle") ?? artist,
                    Int(t, "duration")));
            }
        }

        return new SpotifyEntity(kind, id, title, artist, cover, durationMs, tracks);
    }

    private static JsonElement? FindEntity(JsonElement root)
    {
        if (root.TryGetProperty("props", out var props) &&
            props.TryGetProperty("pageProps", out var pp) &&
            pp.TryGetProperty("state", out var state) &&
            state.TryGetProperty("data", out var data) &&
            data.TryGetProperty("entity", out var entity))
            return entity;

        if (root.TryGetProperty("entity", out var direct))
            return direct;

        return null;
    }

    private static string? FirstArtistName(JsonElement el)
    {
        if (el.TryGetProperty("artists", out var artists) &&
            artists.ValueKind == JsonValueKind.Array &&
            artists.GetArrayLength() > 0 &&
            Str(artists[0], "name") is { } name)
            return name;

        return null;
    }

    private static string? FindCoverUrl(JsonElement el)
    {
        if (el.TryGetProperty("coverArt", out var ca) &&
            ca.ValueKind == JsonValueKind.Object &&
            ca.TryGetProperty("sources", out var sources) &&
            sources.ValueKind == JsonValueKind.Array &&
            sources.GetArrayLength() > 0 &&
            Str(sources[0], "url") is { } u1)
            return u1;

        if (el.TryGetProperty("image", out var img))
        {
            if (img.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(img.GetString()))
                return img.GetString();
            if (Str(img, "url") is { } u2) return u2;
        }

        if (el.TryGetProperty("images", out var imgs) &&
            imgs.ValueKind == JsonValueKind.Array &&
            imgs.GetArrayLength() > 0 &&
            Str(imgs[0], "url") is { } u3)
            return u3;

        return null;
    }

    private static string? TrackIdFromUri(string? uri)
    {
        if (uri is null) return null;
        var m = Regex.Match(uri, @"spotify:track:([A-Za-z0-9]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;

    // ---- downloads ----

    private async Task DownloadSingleTrackAsync(SpotifyEntity entity)
    {
        ShowPreviewCard(entity);

        SetPhase("Finding the song...");
        SetStatus($"Looking up '{entity.Title}' by {entity.Artist}...", StatusKind.Info);

        var dialog = new SaveFileDialog
        {
            Title = "Save MP3",
            Filter = "MP3 audio|*.mp3",
            FileName = SanitizeFileName($"{entity.Artist} - {entity.Title}"),
            OverwritePrompt = true,
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            SetStatus("Cancelled.", StatusKind.Info);
            return;
        }

        _cts = new CancellationTokenSource();
        var track = new TrackEntry(entity.Id, entity.Title, entity.Artist, entity.DurationMs);

        DownloadBar.IsIndeterminate = false;
        await EncodeTrackToMp3(track, dialog.FileName, 0, 1, _cts.Token);

        _lastOutputFile = dialog.FileName;
        SetStatus($"Done! Saved to {dialog.FileName}", StatusKind.Success);
        OpenFolderButton.Visibility = Visibility.Visible;
    }

    private async Task DownloadCollectionAsync(SpotifyEntity entity)
    {
        ShowPreviewCard(entity);

        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder for the MP3s"
        };
        if (dialog.ShowDialog(this) != true)
        {
            SetStatus("Cancelled.", StatusKind.Info);
            return;
        }

        var dir = dialog.FolderName;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var total = entity.Tracks.Count;
        var saved = 0;
        var skipped = new List<string>();

        try
        {
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var track = entity.Tracks[i];

                SetPhase($"Track {i + 1}/{total} - {track.Title}");
                SetStatus($"Finding '{track.Title}' by {track.Artist} on YouTube...", StatusKind.Info);

                try
                {
                    var fileName = $"{(i + 1):D2} - " + SanitizeFileName($"{track.Artist} - {track.Title}") + ".mp3";
                    await EncodeTrackToMp3(track, Path.Combine(dir, fileName), i, total, ct);
                    saved++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (YtDlpException ex)
                {
                    skipped.Add($"{i + 1}. {track.Title} ({ShortReason(ex.Message)})");
                }

                SetOverallProgress(i, total);
            }
        }
        finally
        {
            DownloadBar.IsIndeterminate = false;
        }

        _lastFolder = dir;
        OpenFolderButton.Visibility = Visibility.Visible;

        if (saved == 0)
        {
            SetStatus("No tracks could be saved.", StatusKind.Error);
        }
        else if (skipped.Count > 0)
        {
            SetStatus($"Done - saved {saved} of {total} tracks to that folder", StatusKind.Success);
            SkippedText.Text = "Skipped: " + string.Join("; ", skipped.Take(6)) + (skipped.Count > 6 ? $" (+{skipped.Count - 6} more)" : "");
            SkippedText.Visibility = Visibility.Visible;
        }
        else
        {
            SetStatus($"Done! Saved all {total} tracks to that folder", StatusKind.Success);
        }
    }

    private async Task EncodeTrackToMp3(TrackEntry track, string outputPath, int index, int total, CancellationToken ct)
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"spmp3_{Guid.NewGuid():N}");

        SetPhase($"Track {index + 1}/{total} - downloading '{track.Title}'");
        DownloadBar.IsIndeterminate = false;
        DownloadBar.Value = (index / (double)total) * 100;
        PercentText.Text = $"{(index / (double)total) * 100:0}%";

        var sourceFile = await DownloadBestAudioAsync(SearchQuery(track), basePath, ct, OnTrackDownloadLine(index, total));

        SetPhase($"Track {index + 1}/{total} - encoding MP3");
        DownloadBar.IsIndeterminate = true;

        var kbps = SelectedKbps * 1000;
        try
        {
            await Task.Run(() =>
            {
                using var reader = new MediaFoundationReader(sourceFile);
                MediaFoundationEncoder.EncodeToMp3(reader, outputPath, kbps);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(outputPath);
            throw;
        }
        finally
        {
            DownloadBar.IsIndeterminate = false;
            DeleteQuietly(sourceFile);
        }
    }

    private static string SearchQuery(TrackEntry track)
    {
        var q = $"{track.Artist} - {track.Title}".Trim();
        return q.Length == 0 ? track.Id : q;
    }

    private int SelectedKbps => Kbps192Radio.IsChecked == true ? 192 : Kbps320Radio.IsChecked == true ? 320 : 256;

    // ---- yt-dlp integration ----

    private async Task<string> DownloadBestAudioAsync(string query, string basePath, CancellationToken ct, Action<string>? onLine)
    {
        var args =
            "-f bestaudio/best -N 4 --no-playlist --newline " +
            $"-o \"{basePath}.%(ext)s\" -- \"ytsearch1:{query}\"";

        var (_, _, stderr) = await RunYtDlpAsync(args, ct, onLine);

        var dir = Path.GetDirectoryName(basePath)!;
        var pattern = Path.GetFileName(basePath) + ".*";
        var produced = Directory.GetFiles(dir, pattern)
            .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (produced is null)
            throw MapYtDlpError(stderr.Length > 0 ? stderr : "No YouTube match was found for this track.");

        return produced;
    }

    private Action<string> OnTrackDownloadLine(int index, int total)
    {
        return line =>
        {
            if (!line.StartsWith("[download]", StringComparison.Ordinal)) return;
            var m = PercentRegex.Match(line);
            if (!m.Success) return;
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                var clamped = Math.Clamp(pct, 0, 100);
                var overall = ((index + clamped / 100.0) / total) * 100;
                Dispatcher.Invoke(() =>
                {
                    DownloadBar.Value = overall;
                    PercentText.Text = $"{overall:0}%";
                });
            }
        };
    }

    private void SetOverallProgress(int index, int total)
    {
        var overall = Math.Min(((index + 1) / (double)total) * 100, 100);
        DownloadBar.Value = overall;
        PercentText.Text = $"{overall:0}%";
    }

    private static readonly Regex PercentRegex = new(@"(\d+(?:\.\d+)?)%", RegexOptions.Compiled);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunYtDlpAsync(
        string arguments, CancellationToken ct, Action<string>? onStdoutLine = null)
    {
        if (!File.Exists(YtDlpPath))
            throw new YtDlpException("yt-dlp.exe is missing from the Tools folder next to the app.");

        var psi = new ProcessStartInfo(YtDlpPath)
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.ErrorDataReceived += (_, a) =>
        {
            if (a.Data is null) return;
            lock (stderr) stderr.AppendLine(a.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        try
        {
            using (ct.Register(() =>
                   {
                       try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                   }))
            {
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    lock (stdout) stdout.AppendLine(line);
                    onStdoutLine?.Invoke(line);
                }

                await process.WaitForExitAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        ct.ThrowIfCancellationRequested();

        string errText;
        lock (stderr) errText = stderr.ToString();

        string outText;
        lock (stdout) outText = stdout.ToString();

        if (process.ExitCode != 0)
            throw MapYtDlpError(errText);

        return (process.ExitCode, outText, errText);
    }

    private static YtDlpException MapYtDlpError(string stderr)
    {
        var s = stderr.ToLowerInvariant();

        if (s.Contains("no video") || s.Contains("not find"))
            return new YtDlpException("No matching song was found on YouTube.");
        if (s.Contains("private video"))
            return new YtDlpException("That video is private.");
        if (s.Contains("members-only") || s.Contains("join this channel"))
            return new YtDlpException("That video is members-only.");
        if (s.Contains("age") && (s.Contains("restrict") || s.Contains("confirm")))
            return new YtDlpException("That video is age-restricted and needs sign-in.");
        if (s.Contains("sign in") || s.Contains("bot"))
            return new YtDlpException("YouTube is asking for a bot-check. Updating Tools\\yt-dlp.exe usually fixes it.");
        if (s.Contains("unavailable"))
            return new YtDlpException("That video is unavailable (deleted or region-locked).");
        if (s.Contains("unsupported url"))
            return new YtDlpException("That doesn't look like a supported link.");
        if (s.Contains("429") || s.Contains("too many requests"))
            return new YtDlpException("YouTube is rate-limiting this connection. Wait a bit and try again.");

        var errorLine = stderr.Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));

        var detail = errorLine is null
            ? "yt-dlp failed."
            : errorLine.Length > 180 ? errorLine[..180] : errorLine;

        return new YtDlpException(detail);
    }

    // ---- ui state ----

    private void ShowPreviewCard(SpotifyEntity entity)
    {
        PreviewTitle.Text = entity.Title;
        PreviewArtist.Text = entity.Artist;
        PreviewMeta.Text = entity.IsCollection
            ? $"{entity.Tracks.Count} tracks" + TotalDurationLabel(entity)
            : DurationLabel(entity.DurationMs);

        CoverBrush.ImageSource = null;
        PreviewCard.Visibility = Visibility.Visible;

        if (entity.CoverUrl is not null)
            _ = LoadCoverAsync(entity.CoverUrl);
    }

    private static string TotalDurationLabel(SpotifyEntity entity)
    {
        var ms = entity.Tracks.Sum(t => t.DurationMs);
        return ms > 0 ? " - " + DurationLabel(ms) : "";
    }

    private static string DurationLabel(int ms)
    {
        if (ms <= 0) return "";
        var s = ms / 1000;
        return $"{s / 60}:{s % 60:D2}";
    }

    private async Task LoadCoverAsync(string url)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            CoverBrush.ImageSource = bmp;
        }
        catch { /* cover is optional */ }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SetStatus("Cancelling...", StatusKind.Info);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputFile is not null && File.Exists(_lastOutputFile))
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{_lastOutputFile}\""
            });
        }
        else if (_lastFolder is not null && Directory.Exists(_lastFolder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{_lastFolder}\"" });
        }
    }

    private void SetBusy(bool busy)
    {
        ConvertButton.IsEnabled = !busy;
        PasteButton.IsEnabled = !busy;
        UrlBox.IsEnabled = !busy;
        ProgressSection.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy)
        {
            DownloadBar.IsIndeterminate = false;
            DownloadBar.Value = 0;
            PercentText.Text = "";
        }
    }

    private void SetPhase(string text) => PhaseText.Text = text;

    private enum StatusKind { Info, Success, Error }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        StatusText.Foreground = kind switch
        {
            StatusKind.Success => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80)),
            StatusKind.Error => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x90, 0xB0))
        };
    }

    private static string FriendlyError(Exception ex)
    {
        if (ex is HttpRequestException or TaskCanceledException)
            return "Couldn't reach Spotify. Check your internet connection and try again.";
        var msg = ex.Message;
        return msg.Length > 200 ? "Something went wrong while downloading." : msg;
    }

    private static string ShortReason(string message) =>
        message.Length <= 60 ? message : message[..57] + "...";

    // ---- misc ----

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 80) clean = clean[..80].Trim();
        return clean.Length == 0 ? "spotify-track" : clean;
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
