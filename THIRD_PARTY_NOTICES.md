# Сторонние компоненты Rewind

Собственный код Rewind лицензирован под [GPL‑3.0-only](LICENSE). Сторонние компоненты сохраняют свои лицензии. NSIS кладёт их тексты и уведомления в подпапку `licenses`. Текст лицензии libmpv находится рядом с DLL в `mpv/LICENSE.LGPL`.

| Компонент | Версия и исходники | Лицензия и примечания |
| --- | --- | --- |
| NAudio.Core и NAudio.Wasapi | [3.3.0, коммит 4f8889e](https://github.com/naudio/NAudio/tree/4f8889e2bd34c3978a25652c6c172fa1502ccba3) | MIT |
| WPF UI и WPF UI.Abstractions | [4.3.0, коммит 153baad](https://github.com/lepoco/wpfui/tree/153baadeaf475875a81a4605881ca5c2dbe53972) | MIT. В пакет входят уведомления из обоих NuGet-пакетов. |
| LibVLCSharp и LibVLCSharp.WPF | [3.10.2, коммит 3082c09](https://github.com/videolan/libvlcsharp/tree/3082c09ca955131c705fc4767fde03e9c4a14999) | LGPL-2.1-or-later |
| VideoLAN.LibVLC.Windows | [VLC 3.0.24](https://download.videolan.org/vlc/3.0.24/vlc-3.0.24.tar.xz), [исходники зависимостей VLC](https://www.videolan.org/vlc/download-sources.html) | Ядро LibVLC под LGPL. У плагинов могут действовать другие условия. В пакет входят тексты GPL и LGPL и список авторов из дистрибутива VideoLAN. |
| System.Numerics.Tensors | [9.0.0](https://github.com/dotnet/runtime/tree/v9.0.0/src/libraries/System.Numerics.Tensors) | MIT. В пакет входят уведомления NuGet. |
| .NET Runtime и Windows Desktop Runtime | [dotnet/runtime](https://github.com/dotnet/runtime), [dotnet/wpf](https://github.com/dotnet/wpf), [dotnet/winforms](https://github.com/dotnet/winforms). Точные версии сборки в `Rewind.runtimeconfig.json`. | Тексты лицензий и уведомлений берутся из пакетов runtime, использованных при сборке. |
| NSIS | [NSIS](https://nsis.sourceforge.io/Download) | Файл `COPYING` берётся из использованного комплекта компилятора. |
| NVIDIA nvEncodeAPI | [nv-codec-headers n13.0.19.0](https://github.com/FFmpeg/nv-codec-headers/tree/n13.0.19.0) | MIT для заголовка. Сам кодировщик предоставляет драйвер NVIDIA. |

## FFmpeg и FFprobe

Rewind запускает [FFmpeg](https://ffmpeg.org/) и FFprobe как отдельные процессы. Публичный пакет NSIS не содержит их бинарники. Если готовой пары нет, окно установки загружает [Gyan.FFmpeg](https://github.com/GyanD/codexffmpeg) через WinGet. [Условия FFmpeg](https://www.ffmpeg.org/legal.html)

## libmpv

Пробный пакет использует `libmpv-2.dll` из [LGPL-артефакта mpv для Windows x64](https://github.com/mpv-player/mpv/releases/download/git-release/libmpv-v0.41.0-dev-g0fae46954-39894836680-x86_64-w64-mingw32-lgpl.zip). [Исходники mpv](https://github.com/mpv-player/mpv/tree/0fae4695470bf2c0c06762503292ee5379f62f62) и [скрипт сборки](https://github.com/mpv-player/mpv/blob/0fae4695470bf2c0c06762503292ee5379f62f62/ci/build-mingw64-full.sh) соответствуют коммиту задания [CI 39894836680](https://github.com/mpv-player/mpv/actions/runs/39894836680/job/120222737258).

- SHA‑256 ZIP `5e9ce24e62910fc7ad07e7d15c934cfcaf9236ce46cd3c29964e91b00794cd38`
- SHA‑256 DLL `a4d24c2077840200a7a318fdc059dac21bb57e565fbeb51dcb4ffc6835e7dab7`

В журнале задания CI указаны [FFmpeg `git-2026-10-23-1a24bd6`](https://github.com/FFmpeg/FFmpeg/tree/1a24bd6c7f329613cf64bef24ce7d029de391acd), libplacebo `v7.374.0` и образ сборки `ghcr.io/btbn/ffmpeg-builds/win64-lgpl@sha256:59662fba1b2d870e1aa1d074d3620b71dc98c62df01e2682a0edec224658ad7a`.

Перед публикацией бинарников нужен комплект исходников для точных ревизий встроенных библиотек libmpv. Скрипт CI использует статические зависимости из контейнера BtbN и загружает FFmpeg из незакреплённой ветки. Одного архива исходников mpv для этой сборки недостаточно. Для LibVLC следует приложить исходники соответствующих плагинов и библиотек из сборки VideoLAN.
