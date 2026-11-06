# Rewind

<img src="src/ReplayUi/Assets/Rewind.png" alt="Rewind logo" width="64">

[English](README.md) · **Русский**

Сохраните последние минуты игры или работы за компьютером. Rewind ведёт буфер на диске и сохраняет отдельный MP4 с каждого выбранного экрана.

**Windows 11 x64 · NVIDIA NVENC · GPL-3.0-only**

[Собрать установщик Windows](#сборка-из-исходников)

## Быстрый старт

1. [Соберите установщик](#сборка-из-исходников), запустите `Rewind-Setup.exe`, выберите язык интерфейса и папку для повторов. По умолчанию это системная папка **Видео → Rewind**. При необходимости сетап установит FFmpeg и FFprobe через WinGet.
2. Откройте Rewind из меню «Пуск», выберите экраны в настройках и нажмите **Включить**.
3. Через несколько секунд нажмите **Alt + F10** и отпустите Alt. Чтобы сохранить все записываемые экраны, нажмите **Alt + Shift + F10**.
4. Откройте клип в библиотеке Rewind. В обычном режиме файлы лежат в подпапке `Clips` выбранной папки повторов.

Язык интерфейса можно выбрать в установщике и изменить в настройках Rewind: **English / Русский**. При первом запуске выбирается поддерживаемый язык системы, для остальных языков — английский. Выбор сохраняется между запусками и при переустановке с сохранением папки данных.

## Возможности

- **Несколько экранов.** У каждого экрана свои разрешение и частота кадров. Одно нажатие сохраняет отдельный MP4 для каждого записываемого экрана.
- **Длительность в момент сохранения.** Пока удерживаете Alt, можно выбрать, сколько времени оставить, или отменить сохранение.
- **Раздельный звук.** Системный звук, микрофон и Discord можно записывать на отдельные дорожки.
- **Локальная библиотека.** Просмотр клипов и управление файлами работают без аккаунта. Если у вас есть архив ShadowPlay, укажите его папку в разделе **Настройки → Хранение → Совместимость с NVIDIA ShadowPlay**. Новые повторы будут использовать структуру архива и имена `.DVR.mp4`. В обычном режиме формат имени можно настроить.

## Сравнение для локальных повторов

| Функция | ShadowPlay | OBS | Medal | Action! | **Rewind** |
| --- | :---: | :---: | :---: | :---: | :---: |
| Отдельный клип с каждого монитора одним нажатием | ❌ | ❌ | ❌ | ❌ | ✅ |
| Свои разрешение и FPS для каждого одновременно записываемого монитора | ❌ | ❌ | ❌ | ❌ | ✅ |
| Выбор длительности повтора после нажатия клавиши сохранения | ❌ | ❌ | ❌ | ❌ | ✅ |
| Открытый исходный код программы захвата | ❌ | ✅ | ❌ | ❌ | ✅ |
| Бесплатное сохранение локальных повторов без пробного срока | ✅ | ✅ | ✅ | ❌ | ✅ |
| Звук Discord на отдельной дорожке | ❌ | ✅ | ✅ | ✅ | ✅ |
| Мгновенный повтор по горячей клавише | ✅ | ✅ | ✅ | ✅ | ✅ |
| Несколько аудиодорожек в одном клипе | ✅ | ✅ | ✅ | ✅ | ✅ |

Сравниваем штатные функции одного запущенного экземпляра без плагинов. ✅ Поддерживается · ❌ Штатного режима нет.

ShadowPlay сравнивается в составе NVIDIA App. У Action! есть [30-дневный пробный период][action-trial].

[ShadowPlay][shadowplay] · [OBS][obs] ([запись][obs-output], [звук приложений][obs-app-audio], [дорожки][obs-audio], [исходники][obs-source]) · [Medal][medal] ([бесплатный режим][medal-free], [дорожки][medal-audio]) · [Action!][action] ([Time-Shift][action-replay], [звук приложений][action-audio])

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

## Горячие клавиши

| По умолчанию | Действие |
| --- | --- |
| Alt + F10 | Сохранить повтор основного экрана |
| Alt + Shift + F10 | Сохранить повторы всех записываемых экранов |
| F9 / F11, пока удерживается Alt | Изменить длительность повтора |
| C, пока удерживается Alt | Отменить сохранение |

Отпустите Alt, чтобы сохранить выбранный интервал.

## Системные требования

- **Windows 11 x64** и видеокарта **NVIDIA с NVENC**.
- Свободное место на диске для буфера и клипов. Объём зависит от длительности записи и настроек каждого экрана.
- Сетап включает .NET 10 Desktop Runtime. Если в системе ещё нет FFmpeg и FFprobe, для их установки понадобятся WinGet и интернет.
- Если захват зависает в эксклюзивном полноэкранном режиме, переключите игру в оконный режим.

## Сборка из исходников

Понадобятся Node.js 24, .NET 10 SDK, Visual Studio Build Tools с MSVC x64, Windows SDK с заголовками C++/WinRT и [NSIS 3.13](https://nsis.sourceforge.io/Download). Заголовок NVENC загружается при первой сборке.

1. Подготовьте x64-сборку [libmpv под LGPL](https://mpv.io/installation/) и поместите `libmpv-2.dll` и `LICENSE.LGPL` в `.cache/playback-lab/mpv/`. Сведения о проверенной версии есть в [уведомлениях о зависимостях](THIRD_PARTY_NOTICES.md).
2. Соберите сетап из корня проекта.

   ```powershell
   node build/build-nsis.cjs "C:\путь\к\NSIS\makensis.exe"
   ```

3. Запустите `Rewind-Setup.exe` из `.cache/nsis/<идентификатор-сборки>/`. При обновлении сначала удалите предыдущую версию через «Установленные приложения» Windows. Настройки и клипы сохранятся.

<details>
<summary>Проверки для разработки</summary>

```powershell
dotnet run --project tests/ReplayUiChecks/ReplayUiChecks.csproj --artifacts-path .cache/tests
dotnet run --project tests/LocalizationUiChecks/LocalizationUiChecks.csproj --artifacts-path .cache/l10n-ui-tests
node tests/check-nsis.cjs "C:\путь\к\NSIS\makensis.exe"
```

</details>

## Лицензия

Код Rewind распространяется под [GNU GPL v3.0 only](LICENSE). Лицензии и источники сторонних компонентов перечислены в [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
