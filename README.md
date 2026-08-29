# Spotify to MP3

A small Windows desktop app that turns **Spotify** links into **MP3** files — no browser, no Premium, no login. Paste a track, album or playlist and it saves the songs locally as real MP3s.

## Features

- **Track, album or playlist** — paste any `open.spotify.com` link (or `spotify:` URI); albums and playlists download in one go with numbered filenames
- **No Spotify account needed** — metadata (titles, artists, cover art, track lists) is read from Spotify's own public embed pages; the audio itself is sourced by matching each song on YouTube
- **MP3 encoding @ 192 / 256 / 320 kbps** using Windows Media Foundation (pick in the app)
- **Preview card** — title, artist and cover art shown before you download
- **Paste or drag & drop** the link straight into the window; `Enter` starts the download
- **Live progress bar** with real-time percent across the whole batch ("Track 3/10 - ...")
- **Skip-and-continue** — if one track can't be found, it's noted and the rest keeps going; a summary lists what was skipped
- **Cancel anytime**, then a one-click "show in folder" when done
- **Friendly errors** — private videos, bot-checks, region locks and rate limits all get plain-English messages

## Requirements

- Windows 10 or 11 (WPF app)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run a built copy, or the .NET 10 SDK to build from source

## Building

```powershell
git clone <your-repo-url>
cd Spotify-to-mp3
dotnet publish -c Release -r win-x64 --self-contained false
```

The output lands in `bin/Release/net10.0-windows/win-x64/publish/` — the app and its bundled `Tools/yt-dlp.exe` go there together, so you can copy that folder anywhere and run it.

## Usage

1. Copy a Spotify link (a single song, an album or a whole playlist)
2. Open the app, paste (`Ctrl+V` or the paste button) and hit **Enter**
3. Pick where to save when prompted — one file for tracks, a folder for albums/playlists
4. Watch the progress bar — done! Use *Show in folder* to find your files

## How it works

| Step | Tooling |
| --- | --- |
| Track/album/playlist info (titles, artists, covers) | Spotify's public embed page (`open.spotify.com/embed/...`) - no auth token required |
| Finding each song's audio | `yt-dlp "ytsearch1:<artist> - <title>"` (first YouTube match for the exact song name) |
| Audio download (best quality stream) | yt-dlp `-f bestaudio/best`, bundled in `Tools/` |
| MP3 encoding @ chosen bitrate | NAudio + Windows Media Foundation |

Because Spotify's full streams sit behind a DRM/player wall, the app resolves each track by name from YouTube instead — which is also why results can occasionally be a cover or live version rather than the exact studio recording. The closest match for "artist - title" wins; anything that fails is skipped and reported at the end.

## Project structure

```
SpotifyToMp3.csproj      .NET 10 WPF project (only package: NAudio)
AssemblyInfo.cs          Application metadata
App.xaml(.cs)             Application entry point
MainWindow.xaml(.cs)      Entire UI + download/encoding logic
Tools\yt-dlp.exe          Bundled downloader (same build as the one in my YouTube to MP3 app)
```

## Tech stack

- C# / .NET 10, WPF (custom purple/black UI, no third-party UI libraries)
- [NAudio](https://github.com/naudio/NAudio) — Media Foundation MP3 encoding
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) — audio download
