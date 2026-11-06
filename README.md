# Rewind

<img src="src/ReplayUi/Assets/Rewind.png" alt="Rewind logo" width="64">

**English** · [Русский](README.ru.md)

Save the last few minutes of gameplay or desktop activity. Rewind keeps a rolling buffer on disk and saves a separate MP4 from each selected monitor.

**Windows 11 x64 · NVIDIA NVENC · GPL-3.0-only**

[Build the Windows installer](#build-from-source)

## Quick start

1. Build and run `Rewind-Setup.exe`. Choose English or Russian in setup and select where to store replays. The default folder is **Videos → Rewind**. Setup can install FFmpeg and FFprobe with WinGet if needed.
2. Open Rewind from Start, select your displays in Settings and enable recording.
3. Wait a few seconds. Press **Alt + F10** for the primary display or **Alt + Shift + F10** for all recorded displays. Release Alt to save.
4. Open the MP4 in the Rewind library. In standard mode, saved clips are in the `Clips` subfolder.

Change the interface language in Settings at any time. Rewind remembers the choice when you keep its data folder.

## Features

- Set the resolution and frame rate for each display independently.
- While holding Alt, use **F9 / F11** to adjust the replay length or **C** to cancel.
- Record system audio, microphone and Discord on separate tracks.
- Browse clips in the built-in player. To continue using an existing ShadowPlay archive, choose its folder under the storage settings. Rewind can keep the archive layout and `.DVR.mp4` filenames.

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
