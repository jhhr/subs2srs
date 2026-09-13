# subs2srs (GTK4 port)

[![AUR](https://img.shields.io/badge/AUR-install-blue.svg)](https://aur.archlinux.org/packages/subs2srs-gui)
[![CI](https://github.com/ajatt-tools/subs2srs/actions/workflows/ci.yml/badge.svg)](https://github.com/ajatt-tools/subs2srs/actions/workflows/ci.yml)
[![Chat](https://img.shields.io/badge/chat-join-green)](https://tatsumoto-ren.github.io/blog/join-our-community.html)
![License](https://img.shields.io/badge/License-GPLv3-blue.svg)
[![Spec](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/fkzys/specs/refs/heads/main/version.json&maxAge=300)](https://github.com/fkzys/specs)

![screenshot](assets/screenshot.png)

A tool that creates [Anki](https://apps.ankiweb.net/) flashcards from movies
and TV shows with subtitles, for language learning.

This is a **GTK4 / .NET 10** rewrite of the UI layer.
The processing core (subtitle parsing, ffmpeg calls, SRS generation)
is carried over from the original with minimal changes.

## Credits

- [Christopher Brochtrup](https://sourceforge.net/projects/subs2srs/) — original author
- [erjiang](https://github.com/erjiang/subs2srs) — Linux/Mono port
- [nihil-admirari](https://github.com/nihil-admirari/subs2srs-net48-builds) — updated dependencies

## What changed from the Mono/WinForms version

| Area | Old (erjiang fork) | This port |
|---|---|---|
| UI toolkit | WinForms on Mono | **GTK4** via GirCore |
| Runtime | Mono | **.NET 10+** |
| System.Drawing | Required everywhere | **Removed** — `SrsColor`, `FontInfo` used instead |
| Serialization | `BinaryFormatter` | **System.Text.Json** (`ObjectCloner`) |
| Preferences format | Custom `key = value` text with regex updates | **JSON** (`preferences.json`) |
| Progress dialogs | `BackgroundWorker` + modal `DialogProgress` | **`async/await`** + `IProgressReporter` |
| PropertyGrid (Preferences) | WinForms `PropertyGrid` | **`ColumnView`** with editable cells |
| Preview dialog | `BackgroundWorker` (deadlocked on Wayland) | **`Task.Run` + `async`** |
| Font/Color pickers | WinForms dialogs | **`FontDialogButton` / `ColorDialogButton`** (native GTK4) |
| VobSub support | Built-in | **Optional** (compile with `EnableVobSub=true`) |
| MS fonts | Required fontconfig workaround | **Not needed** |
| Build system | mcs / xbuild | **`dotnet publish`** via Makefile |

### Removed components

- **SubsReTimer** — separate tool, not part of this port
- **DialogAbout** — removed (was WinForms bitmap-based)
- **DialogPreviewSnapshot** — merged into `DialogPreview`
- **DialogVideoDimensionsChooser** — removed (size set directly in settings)
- **GroupBoxCheck** — WinForms custom control, not needed in GTK

## Dependencies

**Runtime:**
- [.NET 10+](https://dotnet.microsoft.com/) runtime
- [GTK 4](https://gtk.org/)
- [ffmpeg](https://ffmpeg.org/)
- [mp3gain](https://mp3gain.sourceforge.net/) *(only if using audio normalization)*
- [mkvtoolnix](https://mkvtoolnix.download/) (`mkvextract`, `mkvinfo`) *(only for MKV track extraction)*

**Build:**
- [.NET 10+ SDK](https://dotnet.microsoft.com/)

**Optional:**
- [noto-fonts-cjk](https://github.com/notofonts/noto-cjk) — for Japanese/Chinese/Korean text

## Build

```sh
make build
```

## Test
```sh
make test      # unit tests + card-generation e2e (needs ffmpeg on PATH)
make test-ui   # GTK UI tests; needs a display (xvfb-run -a make test-ui on a headless box)
```

The UI tests open every window, drive the main window through a full deck generation and
exercise the Preferences dialog. Set `SUBS2SRS_UITEST_ARTIFACTS=<dir>` to get a PNG screenshot
of each window.

## Windows

A portable zip (`subs2srs-<version>-win-x64.zip`) is attached to every GitHub release. It is
self-contained: .NET and the GTK 4 runtime are included, nothing needs to be installed.

1. Unzip anywhere and run `subs2srs.exe`.
2. Install ffmpeg (not bundled): `winget install Gyan.FFmpeg`, or download a build from
   <https://ffmpeg.org/download.html>. Either put its `bin` folder on `PATH` or point
   **Preferences → Misc → Tools Directory** at it. `mp3gain` and `mkvtoolnix` are optional and
   found the same way.
3. Preferences live in `%APPDATA%\subs2srs\preferences.json`, logs in `%LOCALAPPDATA%\subs2srs\Logs`.

Troubleshooting: if windows render blank or the app crashes at startup on an old GPU or over
Remote Desktop, set the environment variable `GSK_RENDERER=cairo` (the bundled build already
defaults to it). The log directory above contains GTK warnings and the full ffmpeg command lines.

Building on Windows: install [MSYS2](https://www.msys2.org/), then
`pacman -S mingw-w64-ucrt-x86_64-gtk4 mingw-w64-ucrt-x86_64-ntldd`, add `C:\msys64\ucrt64\bin`
to `PATH` for development runs (`dotnet run --project subs2srs`), and use `make publish-windows`
(or the commands in `.github/workflows/release.yml`) to produce the bundled zip.

## Install

### AUR

```sh
yay -S subs2srs-gui
```

### Manual

```sh
git clone https://github.com/ajatt-tools/subs2srs.git
cd subs2srs
sudo make install
```

Installs to `/usr/lib/subs2srs/`, launcher to `/usr/bin/subs2srs`.

### Uninstall

```sh
sudo make uninstall
```

## Configuration

On first run, `preferences.json` is created in
`~/.config/subs2srs/` (Windows: `%APPDATA%\subs2srs\`).

If a `preferences.txt` from a previous version exists in the same directory,
it is automatically migrated to JSON on first launch. The old file is left
intact.

Projects are saved as `.s2s.json` files (File → Save/Load Project).

Edit preferences via **Preferences** dialog or by editing `preferences.json`
directly.

### Adding a new preference

1. Add default constant to `PrefDefaults`
2. Add property to `PreferencesData.cs` with default from `PrefDefaults`
3. Add delegating property to `ConstantSettings`
4. Add to `DialogPref.BuildPropTable()` + `DialogPref.SavePreferences()`
5. Add to `Logger.writeSettingsToLog()`
6. If the preference maps to `Settings.Instance`, add to `Settings.Reset()`

### Parallelism

Set `max_parallel_tasks` in Preferences → Misc (or in `preferences.json`):
- `0` — auto (number of CPU cores, default)
- `1` — sequential (no parallelism)
- `N` — use up to N threads for media generation

## Building with VobSub support

VobSub (`.sub`/`.idx`) parsing requires `System.Drawing.Common` and is
disabled by default. To enable:

```sh
dotnet publish subs2srs/subs2srs.csproj -c Release -p:EnableVobSub=true
```

## License

[GPL-3.0-or-later](https://www.gnu.org/licenses/gpl-3.0.html)
