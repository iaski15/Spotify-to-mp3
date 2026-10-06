# Spotify to MP3

A small Windows desktop app that turns **Spotify** links into **MP3** files — no browser, no Premium, no login. Paste a track, album or playlist and it saves the songs locally as real MP3s.

## Features

- **Track, album or playlist** — paste any `open.spotify.com` link (or `spotify:` URI); albums and playlists download in one go with numbered filenames
- **No Spotify account needed** — metadata (titles, artists, cover art, track lists) is read from Spotify's own public embed pages; the audio itself is sourced by matching each song on YouTube
- **Original M4A (default)** — saves YouTube's AAC audio untouched: no re-encode, so no extra quality loss
- **Or MP3 @ 192 / 256 / 320 kbps** using Windows Media Foundation (pick in the app)
- **Preview first** — paste/drop a link and you get the cover, title and full track list before anything downloads
- **Per-track status** — each row shows searching / downloading % / saved / why it was skipped, live
- **Fixed save folder** (default `Music\Spotify to MP3`, change it in the sidebar; remembered) — albums and playlists get their own subfolder, no save dialogs
- **Re-run safe** — songs already in the folder show "Already saved" and are skipped, so re-downloading a playlist only fetches what's new or failed
- **Fallback matching** — if the best YouTube match is blocked, the next-best one is tried
- **Cancel anytime**; *Open download folder* jumps to the last saved song
- **Friendly errors** — private videos, bot-checks, region locks and rate limits all get plain-English messages

## Requirements

- Windows 10 or 11 (WPF app)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run a built copy, or the .NET 10 SDK to build from source

## Building

```powershell
git clone <your-repo-url>
cd Spotify-to-mp3
dotnet publish -c Release
```

The only file you need is **`release\Spotify to MP3.exe`**: one exe, nothing to carry alongside it. (`bin\` and `obj\` are build scratch.)

## Usage

1. Copy a Spotify link (a single song, an album or a whole playlist)
2. Click **Paste** (or drop the link on the window / type it and press **Enter**) to load the preview
3. Pick a format in the sidebar and hit **Download**
4. Watch each track's status; *Open download folder* when done

## How it works

| Step | Tooling |
| --- | --- |
| Track/album/playlist info (titles, artists, covers) | Spotify's public embed page (`open.spotify.com/embed/...`) - no auth token required |
| Finding each song's audio | Own YouTube search (InnerTube API): of the top 5 hits for "artist - title", tried closest-length-to-Spotify first, falling through blocked videos |
| Audio download (best quality stream) | Own InnerTube player client (`YouTube.cs`), ranged chunk download, no yt-dlp |
| Output | Original M4A: raw stream moved into place. MP3: NAudio + Windows Media Foundation at the chosen bitrate |

Because Spotify's full streams sit behind a DRM/player wall, the app resolves each track by name from YouTube instead — which is also why results can occasionally be a cover or live version rather than the exact studio recording. The closest-length match for "artist - title" wins; anything that fails is skipped and reported at the end.

## Project structure

```
SpotifyToMp3.csproj      .NET 10 WPF project (only package: NAudio)
AssemblyInfo.cs          Application metadata
App.xaml(.cs)             Application entry point
MainWindow.xaml(.cs)      Entire UI + download/encoding logic
YouTube.cs                YouTube search + stream download (shared with my YouTube to MP3 app)
```

## Tech stack

- C# / .NET 10, WPF (custom purple/black UI, no third-party UI libraries)
- [NAudio](https://github.com/naudio/NAudio) — Media Foundation MP3 encoding