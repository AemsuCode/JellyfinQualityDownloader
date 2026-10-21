# Jellyfin Series Quality Downloader Plugin

A Jellyfin 10.10 (`.NET 8.0`) plugin that lets you download entire **TV Series**, **Seasons**, or **Individual Episodes** in **any quality, resolution, bitrate, CRF, or codec** you choose—packaged automatically into a `.zip` archive (or a single video file for individual episodes).

---

## Features

- **Injected "Download Quality" Button in Jellyfin Web**:
  - Automatically injects a native **Download Quality** button onto Series, Season, Episode, and Movie detail pages.
  - Also includes a **Quick Series Downloader** inside **Dashboard → Plugins → Series Quality Downloader** (works even on read-only Docker containers where `index.html` cannot be modified).
- **Any Quality & Codec Configuration**:
  - **Quality Presets**:
    - `Original Quality` (Direct stream / lossless copy, with an **Instant Stream .ZIP** option that streams the archive on-the-fly without temporary files)
    - `4K UHD — 2160p (20 Mbps)`
    - `1080p Full HD High (8 Mbps)`
    - `1080p Full HD Balanced (4 Mbps)`
    - `720p HD Standard (2.5 Mbps)`
    - `720p HD Compact (1.5 Mbps)`
    - `480p SD (1 Mbps)`
    - `360p Mobile Saver (500 kbps)`
    - `Custom Quality`
  - **Full Custom Controls**:
    - Custom **Max Width / Max Height** (automatically preserves aspect ratio and never upscales above source resolution)
    - Custom **Video Bitrate (kbps)** or **Constant Quality (CRF 0–51)**
    - **Video Encoders**: `libx264`, `libx265`, `h264_nvenc`, `hevc_nvenc`, `h264_qsv`, `h264_vaapi`, or `copy`
    - **Audio Encoders**: `aac`, `libmp3lame` (MP3), `libopus`, `ac3`, or `copy` + custom Audio Bitrate & Language Track Selection
    - **Subtitles**: Soft-embed subtitles (`mov_text` in `.mp4` or stream copy in `.mkv`) filtered by language
    - **Containers**: `.mp4` (with `+faststart`) or `.mkv`
- **Interactive Season & Episode Tree Picker**:
  - Expand/collapse seasons, check/uncheck specific episodes or full seasons, and view real-time estimated output file size.
- **Background Transcode Queue & Floating Progress Indicator**:
  - Uses Jellyfin's configured FFmpeg (`IMediaEncoder`) with real-time progress tracking per episode and automatic browser download trigger on completion.

---

## Installation

### Option 1: Install via Jellyfin Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and click **`+`** to add a new repository:
   - **Repository Name**: `Series Quality Downloader`
   - **Repository URL**:
     ```text
     https://raw.githubusercontent.com/AemsuCode/JellyfinTranscodeDownload/main/manifest.json
     ```
2. Go to the **Catalog** tab, find **Series Quality Downloader** under **General**, and click **Install**.
3. Restart your Jellyfin Server.

---

### Option 2: Manual Installation

1. Build the plugin (or use the prebuilt archive in `dist/`):
   ```powershell
   dotnet build -c Release
   ```
2. Copy the contents of:
   ```text
   Jellyfin.Plugin.SeriesDownloader/bin/Release/net8.0/
   ```
   (`Jellyfin.Plugin.SeriesDownloader.dll` and `meta.json`) into a new folder named `SeriesDownloader` inside your Jellyfin server's `plugins` directory:
   - **Windows**: `C:\ProgramData\Jellyfin\Server\plugins\SeriesDownloader\`
   - **Linux**: `/var/lib/jellyfin/plugins/SeriesDownloader/`
   - **Docker**: `/config/plugins/SeriesDownloader/`
3. Restart your Jellyfin Server.
4. Open any Series, Season, or Episode in Jellyfin Web and click **Download Quality**, or visit **Dashboard → Plugins → Series Quality Downloader**.
