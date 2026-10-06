using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NAudio.Wave;

namespace SpotifyToMp3;

public partial class MainWindow : Window
{
    private static readonly Regex WebUrlRegex = new(
        @"open\.spotify\.com/(?:intl-[a-z-]+/)?(?:embed/)?(track|album|playlist)/([A-Za-z0-9]{22})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UriSchemeRegex = new(
        @"^spotify:(track|album|playlist):([A-Za-z0-9]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string FolderSettingPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyToMp3", "folder.txt");

    private const string Waiting = "#6E6488", Working = "#C99BFF", Ok = "#4ADE80", Bad = "#FF6B6B";

    private CancellationTokenSource? _cts;
    private bool _busy;
    private SpotifyEntity? _entity;
    private List<TrackRow> _rows = [];
    private string _folder;
    private string? _lastSaved;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public MainWindow()
    {
        InitializeComponent();

        try { _folder = File.ReadAllText(FolderSettingPath).Trim(); } catch { _folder = ""; }
        if (_folder.Length == 0)
            _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Spotify to MP3");
        FolderText.Text = _folder;
        FolderText.ToolTip = _folder;
    }

    // ---- window chrome ----

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.TextBox) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- input: paste / enter / drop all load the link ----

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.GetText().Trim();
            if (text.Length > 0) UrlBox.Text = text;
        }
        catch { /* clipboard can be locked, ignore */ }

        _ = LoadAsync();
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = LoadAsync();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.StringFormat) is string text && text.Trim().Length > 0)
        {
            UrlBox.Text = text.Trim();
            _ = LoadAsync();
        }
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Where should songs be saved?", InitialDirectory = _folder };
        if (dialog.ShowDialog(this) != true) return;

        _folder = dialog.FolderName;
        FolderText.Text = _folder;
        FolderText.ToolTip = _folder;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FolderSettingPath)!);
            File.WriteAllText(FolderSettingPath, _folder);
        }
        catch { /* not remembering it is fine */ }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSaved is not null && File.Exists(_lastSaved))
            Process.Start("explorer.exe", $"/select,\"{_lastSaved}\"");
        else
        {
            Directory.CreateDirectory(_folder);
            Process.Start("explorer.exe", $"\"{_folder}\"");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SetStatus("Cancelling...", Waiting);
    }

    // ---- models ----

    private sealed record TrackEntry(string Id, string Title, string Artist, int DurationMs);

    private sealed record SpotifyEntity(
        string Kind, string Id, string Title, string Artist,
        string? CoverUrl, int DurationMs, IReadOnlyList<TrackEntry> Tracks)
    {
        public bool IsCollection => Tracks.Count > 0;
    }

    private sealed class SpotifyLinkException(string message) : Exception(message);

    /// <summary>One row of the track list; Status/StatusColor update live.</summary>
    public sealed class TrackRow(int number, string title, string artist, string duration) : INotifyPropertyChanged
    {
        public string Number { get; } = number.ToString();
        public string Title { get; } = title;
        public string Artist { get; } = artist;
        public string Duration { get; } = duration;

        public string Status { get; private set; } = "";
        public string StatusColor { get; private set; } = Waiting;

        public void Set(string status, string color)
        {
            Status = status;
            StatusColor = color;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
            PropertyChanged?.Invoke(this, new(nameof(StatusColor)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ---- step 1: load the link ----

    private async Task LoadAsync()
    {
        if (_busy) return;

        var raw = UrlBox.Text.Trim().Trim('"', '\'');
        if (raw.Length == 0)
        {
            SetStatus("Paste a Spotify link first.", Waiting);
            UrlBox.Focus();
            return;
        }

        SetBusy(true, loading: true);
        try
        {
            var (kind, id) = ParseSpotifyLink(raw);
            SetStatus("Reading Spotify...", Working);
            ShowEntity(await FetchEntityAsync(kind, id));
            SetStatus($"Ready to download to {_folder}", Waiting);
        }
        catch (Exception ex)
        {
            SetStatus(ex is SpotifyLinkException ? ex.Message : FriendlyError(ex), Bad);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static (string Kind, string Id) ParseSpotifyLink(string raw)
    {
        var url = raw.Contains("://") ? raw : "https://" + raw;

        var m = WebUrlRegex.Match(url);
        if (!m.Success) m = UriSchemeRegex.Match(raw);
        if (!m.Success)
            throw new SpotifyLinkException("That doesn't look like a Spotify song, album or playlist link.");

        return (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);
    }

    private void ShowEntity(SpotifyEntity entity)
    {
        _entity = entity;
        var tracks = Tracks(entity);
        _rows = tracks.Select((t, i) => new TrackRow(i + 1, t.Title, t.Artist, DurationLabel(t.DurationMs))).ToList();
        TrackList.ItemsSource = _rows;

        KindText.Text = entity.Kind switch { "album" => "ALBUM", "playlist" => "PLAYLIST", _ => "SONG" };
        TitleText.Text = entity.Title;
        TitleText.ToolTip = entity.Title;
        var ms = tracks.Sum(t => t.DurationMs);
        MetaText.Text = string.Join("  ·  ", new[]
        {
            entity.Artist,
            entity.IsCollection ? $"{tracks.Count} songs" : "",
            DurationLabel(ms)
        }.Where(s => s.Length > 0));
        DownloadLabel.Text = entity.IsCollection ? $"Download all {tracks.Count}" : "Download";

        CoverBrush.ImageSource = null;
        GlowBrush.ImageSource = null;
        if (entity.CoverUrl is not null) _ = LoadCoverAsync(entity.CoverUrl);

        EmptyState.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Visible;
    }

    private static IReadOnlyList<TrackEntry> Tracks(SpotifyEntity e) =>
        e.IsCollection ? e.Tracks : [new TrackEntry(e.Id, e.Title, e.Artist, e.DurationMs)];

    private async Task LoadCoverAsync(string url)
    {
        try
        {
            using var ms = new MemoryStream(await _http.GetByteArrayAsync(url));
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            CoverBrush.ImageSource = bmp;
            GlowBrush.ImageSource = bmp;
        }
        catch { /* cover is optional */ }
    }

    // ---- spotify metadata (no login needed: the embed page is public) ----

    private async Task<SpotifyEntity> FetchEntityAsync(string kind, string id)
    {
        var html = await _http.GetStringAsync($"https://open.spotify.com/embed/{kind}/{id}");

        var m = Regex.Match(html, "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.+?)</script>", RegexOptions.Singleline);
        if (!m.Success)
            throw new SpotifyLinkException("Spotify didn't return data for this link - it may be invalid or unavailable in your region.");

        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        if (FindEntity(doc.RootElement) is not { } entityEl)
            throw new SpotifyLinkException("Couldn't read that Spotify page. Make sure the link points to a track, album or playlist.");

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
        // Current embed layout: visualIdentity.image = [{url, maxWidth}, ...] in no particular order.
        if (el.TryGetProperty("visualIdentity", out var vi) &&
            vi.ValueKind == JsonValueKind.Object &&
            vi.TryGetProperty("image", out var vimg) &&
            vimg.ValueKind == JsonValueKind.Array &&
            vimg.EnumerateArray().OrderByDescending(i => Int(i, "maxWidth")).Select(i => Str(i, "url")).FirstOrDefault(u => u is not null) is { } u0)
            return u0;

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


    // ---- step 2: download ----

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _entity is null) return;

        var entity = _entity;
        var tracks = Tracks(entity);
        var ext = KeepOriginal ? ".m4a" : ".mp3";
        // Albums/playlists get their own subfolder; single songs go straight into the save folder.
        var dir = entity.IsCollection ? Path.Combine(_folder, SanitizeFileName(entity.Title)) : _folder;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        foreach (var r in _rows) r.Set("Waiting", Waiting);

        var saved = 0;
        var i = 0;
        try
        {
            Directory.CreateDirectory(dir);
            for (; i < tracks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var track = tracks[i];
                var row = _rows[i];
                TrackList.ScrollIntoView(row);

                var name = SanitizeFileName($"{track.Artist} - {track.Title}") + ext;
                if (entity.IsCollection) name = $"{i + 1:D2} - {name}";
                var path = Path.Combine(dir, name);

                if (File.Exists(path))
                {
                    row.Set("Already saved", Ok);
                    _lastSaved = path;
                    saved++;
                    continue;
                }

                SetStatus($"{i + 1}/{tracks.Count}  ·  {track.Artist} - {track.Title}", Working);
                try
                {
                    await SaveTrackAsync(track, path, row, i, tracks.Count, ct);
                    row.Set("Saved", Ok);
                    _lastSaved = path;
                    saved++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad track (no match, region lock, codec) shouldn't stop the rest.
                    row.Set(FriendlyError(ex), Bad);
                }
                SetProgress((i + 1) / (double)tracks.Count * 100);
            }

            SetStatus(saved == tracks.Count
                    ? $"Done! Saved {Plural(saved)} to {dir}"
                    : $"Saved {saved} of {Plural(tracks.Count)} to {dir} (hover a red status for why)",
                saved == 0 ? Bad : Ok);
        }
        catch (OperationCanceledException)
        {
            foreach (var r in _rows.Skip(i)) r.Set("Cancelled", Waiting);
            SetStatus($"Cancelled. Saved {Plural(saved)}.", Waiting);
        }
        catch (Exception ex)
        {
            SetStatus(FriendlyError(ex), Bad);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private static string Plural(int n) => n == 1 ? "1 song" : $"{n} songs";

    private async Task SaveTrackAsync(TrackEntry track, string outputPath, TrackRow row, int index, int total, CancellationToken ct)
    {
        row.Set("Searching...", Working);
        SetProgress(index / (double)total * 100);

        var query = $"{track.Artist} - {track.Title}".Trim(' ', '-');
        var ids = await YouTube.SearchVideoIdsAsync(query.Length > 0 ? query : track.Id, track.DurationMs / 1000, ct);

        // Label "official audio" uploads are often blocked for this client - fall through to the next-best match.
        MediaFormat? audio = null;
        YouTubeException? lastError = null;
        foreach (var id in ids)
        {
            try { audio = (await YouTube.GetVideoAsync(id, ct)).BestAudio(); break; }
            catch (YouTubeException ex) { lastError = ex; }
        }
        if (audio is null) throw lastError!;

        var sourceFile = Path.Combine(Path.GetTempPath(), $"spmp3_{Guid.NewGuid():N}" + (audio.Mime.StartsWith("audio/mp4") ? ".m4a" : ".webm"));
        long done = 0;
        var lastPct = -1;
        try
        {
            await YouTube.DownloadAsync(audio, sourceFile, n =>
            {
                done += n;
                if (audio.Size <= 0) return;
                var frac = Math.Min(done / (double)audio.Size, 1);
                var pct = (int)(frac * 100);
                if (pct == lastPct) return;
                lastPct = pct;
                Dispatcher.BeginInvoke(() =>
                {
                    row.Set($"Downloading {pct}%", Working);
                    SetProgress((index + frac) / total * 100);
                });
            }, ct);

            if (KeepOriginal && sourceFile.EndsWith(".m4a"))
            {
                // Bit-for-bit YouTube AAC, no generation loss.
                // ponytail: it's fragmented MP4 (DASH); every modern player reads it, very old taggers may not.
                File.Move(sourceFile, outputPath, overwrite: true);
                return;
            }

            row.Set("Encoding...", Working);
            var kbps = KeepOriginal ? 256_000 : SelectedKbps * 1000;
            var keep = KeepOriginal;
            await Task.Run(() =>
            {
                using var reader = new MediaFoundationReader(sourceFile);
                if (keep) MediaFoundationEncoder.EncodeToAac(reader, outputPath, kbps); // rare: source was opus/webm
                else MediaFoundationEncoder.EncodeToMp3(reader, outputPath, kbps);
            }, ct);
        }
        catch
        {
            DeleteQuietly(outputPath); // never leave a half-written song behind
            throw;
        }
        finally
        {
            DeleteQuietly(sourceFile);
        }
    }

    private bool KeepOriginal => OriginalRadio.IsChecked == true;

    private int SelectedKbps => Mp3192Radio.IsChecked == true ? 192 : Mp3256Radio.IsChecked == true ? 256 : 320;

    // ---- ui state ----

    private void SetBusy(bool busy, bool loading = false)
    {
        _busy = busy;
        UrlBox.IsEnabled = PasteButton.IsEnabled = !busy;
        FormatPanel.IsEnabled = ChangeFolderButton.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy;
        CancelButton.Visibility = busy && !loading ? Visibility.Visible : Visibility.Collapsed;
        DownloadBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        DownloadBar.IsIndeterminate = loading;
        if (!busy) SetProgress(0, show: false);
    }

    private void SetProgress(double pct, bool show = true)
    {
        DownloadBar.Value = pct;
        PercentText.Text = show ? $"{pct:0}%" : "";
    }

    private void SetStatus(string message, string color)
    {
        StatusText.Text = message;
        StatusText.ToolTip = message;
        StatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(color == Waiting ? "#9A90B0" : color)!;
    }

    private static string DurationLabel(int ms)
    {
        if (ms <= 0) return "";
        var s = ms / 1000;
        return s >= 3600 ? $"{s / 3600} hr {s % 3600 / 60} min" : $"{s / 60}:{s % 60:D2}";
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        YouTubeException or SpotifyLinkException => ex.Message,
        HttpRequestException or TaskCanceledException => "Network error. Check your connection.",
        _ => ex.Message.Length > 120 ? "Something went wrong." : ex.Message
    };

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
