# Rewind

<img src="src/ReplayUi/Assets/Rewind.png" alt="Rewind logo" width="64">

**English** · [Русский](README.ru.md)

Save the last few minutes of gameplay or desktop activity. Rewind keeps a rolling buffer on disk and saves a separate MP4 from each selected monitor.

**Windows 11 x64 · NVIDIA NVENC · GPL-3.0-only**

[Build the Windows installer](#build-from-source)

## Quick start

1. Run the installer, choose English or Russian and select a replay folder. The default is **Videos → Rewind**.
2. In Rewind Settings, select your displays and enable recording.
3. Press **Alt + F10** for the primary display or **Alt + Shift + F10** for all recorded displays. Release Alt to save, then open the clip in the library.

You can change the interface language in Settings.

## Features

- Set the resolution and frame rate independently for each display.
- Hold Alt and press **F9 / F11** to adjust the replay length or **C** to cancel.
- Record system audio, microphone and Discord on separate tracks.
- Play and organize clips in the built-in library. Select your existing ShadowPlay archive in storage settings to keep its layout and `.DVR.mp4` filenames.

## Local replay comparison

| Feature | ShadowPlay | OBS | Medal | Action! | **Rewind** |
| --- | :---: | :---: | :---: | :---: | :---: |
| One hotkey saves a separate clip from each monitor | ❌ | ❌ | ❌ | ❌ | ✅ |
| Independent resolution and frame rate for each recorded monitor | ❌ | ❌ | ❌ | ❌ | ✅ |
| Choose replay length after pressing the save hotkey | ❌ | ❌ | ❌ | ❌ | ✅ |
| Open-source capture application | ❌ | ✅ | ❌ | ❌ | ✅ |
| Free local replays without a trial period | ✅ | ✅ | ✅ | ❌ | ✅ |
| Discord audio on a separate track | ❌ | ✅ | ✅ | ✅ | ✅ |
| Instant replay hotkey | ✅ | ✅ | ✅ | ✅ | ✅ |
| Multiple audio tracks in one clip | ✅ | ✅ | ✅ | ✅ | ✅ |

This compares built-in features in a single running instance without plugins. ✅ Supported · ❌ Unavailable as a built-in feature.

ShadowPlay is evaluated as part of the NVIDIA App. Action! offers a [30-day trial][action-trial].

[ShadowPlay][shadowplay] · [OBS][obs] ([recording][obs-output], [application audio][obs-app-audio], [audio tracks][obs-audio], [source code][obs-source]) · [Medal][medal] ([free mode][medal-free], [audio tracks][medal-audio]) · [Action!][action] ([Time-Shift][action-replay], [application audio][action-audio])

[shadowplay]: https://www.nvidia.com/en-us/geforce/news/nvidia-app-download-and-features/
[obs]: https://obsproject.com/kb/obs-studio-overview
[obs-output]: https://obsproject.com/kb/standard-recording-output-guide
[obs-app-audio]: https://obsproject.com/kb/application-audio-capture-guide
[obs-audio]: https://obsproject.com/kb/multiple-audio-track-recording-guide/
[obs-source]: https://github.com/obsproject/obs-studio
[medal]: https://support.medal.tv/support/solutions/articles/48001157616-how-to-choose-your-recording-method
[medal-free]: https://medal.tv/download
[medal-audio]: https://support.medal.tv/support/solutions/articles/48001220541-multi-track-recording
[action]: https://mirillis.com/action-features
[action-replay]: https://mirillis.com/action-time-shift
[action-audio]: https://mirillis.com/action-history
[action-trial]: https://mirillis.com/activation-action-tutorial

## Requirements

- Windows 11 x64, an NVIDIA GPU with NVENC, and enough disk space for the buffer and clips.
- Setup includes the .NET 10 Desktop Runtime. Installing missing copies of FFmpeg and FFprobe requires WinGet and internet access.
- If capture stalls in exclusive fullscreen, try windowed mode.

## Build from source

Install Node.js 24, .NET 10 SDK, Visual Studio Build Tools with MSVC x64, a Windows SDK with C++/WinRT headers, and [NSIS 3.13](https://nsis.sourceforge.io/Download). The first build downloads the NVENC header.

1. Put an x64 [libmpv](https://mpv.io/installation/) build under the LGPL in `.cache/playback-lab/mpv/`, with `libmpv-2.dll` and `LICENSE.LGPL`. See [third-party notices](THIRD_PARTY_NOTICES.md) for the tested version.
2. From the repository root, run:

   ```powershell
   node build/build-nsis.cjs "C:/path/to/NSIS/makensis.exe"
   ```
3. Run `Rewind-Setup.exe` from `.cache/nsis/<build-id>/`. To upgrade, first uninstall the previous version through Windows Installed apps. Keep the data and replay folders to preserve your settings and clips.

## License

[GNU GPL v3.0 only](LICENSE). Dependency licenses and sources are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
