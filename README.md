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

- **SubsReTimer** — now a separate project, [subsretimer](https://github.com/jhhr/subsretimer);
  the Tools tab launches it when it is installed (see below)
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
- [mkvtoolnix](https://mkvtoolnix.download/) (`mkvmerge`, `mkvextract`, `mkvinfo`) *(only for MKV track extraction and `subs2srs-cli season`)*

**Build:**
- [.NET 10+ SDK](https://dotnet.microsoft.com/)

**Optional:**
- [noto-fonts-cjk](https://github.com/notofonts/noto-cjk) — for Japanese/Chinese/Korean text
- [subsretimer](https://github.com/jhhr/subsretimer) — for the Subs Re-Timer entry in the Tools tab and `subs2srs-cli season`

## Subs Re-Timer

When two subtitle files for the same episode are timed to different cuts of
the video (a sponsor segment, opening or eyecatch present in one and not the
other), the **Subs Re-Timer** entry in the Tools tab re-times one to match
the other. It runs the separate `subsretimer` program found on `PATH` (or
in the *Tools Directory* preference), prefilled with the Subs1 and Subs2
files from the main window. Choose which file is the reference (the one
already matching your video), tick *Auto-align* to run the alignment without
opening the tool's editor, and on success subs2srs offers to put the re-timed
file into the corresponding field. Without *Auto-align* the tool's editor
opens on the pair; when you save there, subs2srs offers the saved file the
same way.

## Command line: a season in one command (`subs2srs-cli`)

`subs2srs-cli` makes the cards of a whole season without the GUI: every episode in one run, into
one import file, with the episodes it cannot do left out and the others keeping their episode
numbers. `season` starts from the videos and a Japanese subtitle file per video: it extracts each
video's English track, retimes the Japanese file to it with
[subsretimer](https://github.com/jhhr/subsretimer), then makes the cards. `go` makes the cards from
subtitles already prepared.

Set the show up once in the GUI (card fields, audio, snapshots, snippet mode, deck name, output
directory, the Subs1 and Subs2 encodings, *Episode Start #*) and save it with File → Save Project.
Both commands take every setting from that `.s2s.json`, and the files from a season folder:

```sh
subs2srs-cli season "Show S1" --project show.s2s.json --dry-run   # what it would do; changes nothing
subs2srs-cli season "Show S1" --project show.s2s.json
subs2srs-cli go --project show.s2s.json --season "Show S1"        # the cards alone, from Show S1/s2s
```

Both read the GUI's `preferences.json` (the *Tools Directory*, the AI settings and cache) and never
write it; `--prefs FILE` reads another file, `--no-prefs` none. They open no window and need no
display. The table goes to stdout; progress, warnings and errors go to stderr (`--verbose` adds the
application log).

Install: on Linux `make install` puts `/usr/bin/subs2srs-cli` next to the GUI's launcher. On
Windows, `subs2srs-cli.exe` is next to `subs2srs.exe` in the zip (and in `out/win-x64` after
`make publish-windows`); run it from PowerShell or cmd by its path, or add the folder to `PATH`.
From a source tree: `dotnet run --project subs2srs.Cli -- season ...`.

### `go`: the cards from prepared subtitles

In short: `go --project FILE [--season DIR] [--dry-run] [--grouping rules|off] [--deck NAME] [--yes]
[--verbose] [--prefs FILE | --no-prefs]`. From `subs2srs-cli --help`:

```text
  --project <file>  the project file
  --season <dir>    take the episodes from a season folder instead of the project's
                    Subs1, Subs2 and video patterns: every *.mkv in <dir>, sorted as
                    the GUI sorts files and numbered from the project's Episode Start #,
                    with s2s/<video name>.ja.<ext> as Subs1 and s2s/<video name>.en.<ext>
                    as Subs2 (<ext> ass, ssa or srt; the tag in any case). An episode
                    without exactly one of each is skipped and keeps its number; videos
                    after the project's Episode End # are left out. Audio clips come
                    from the videos.
  --dry-run         print the episode list (with whether each episode's AI grouping
                    is cached) and the checks before starting, and stop
  --grouping <mode> group the lines into snippets by this mode instead of the
                    project's: rules or off, so a project that groups by AI runs
                    without the model.
  --deck <name>     the deck name instead of the project's: it names the import file
                    (<name>.tsv), the media folder (<name>.media) and the media, and
                    starts each card's tag. The output directory stays the project's.
  --yes             answer yes when asked to confirm (the answer is no otherwise),
                    as at a warning of the checks
  --prefs <file>    read this preferences JSON file instead of the user's preferences.json
  --no-prefs        do not read preferences.json
  --verbose         echo the application log to stderr
  --help            this text
  --version         the version
```

A season folder holds the videos and, in its `s2s` subfolder, the subtitles of each video under
the video's own name:

```text
Show S1/
  Show - 01.mkv
  Show - 02.mkv
  Show - 03.mkv
  s2s/
    Show - 01.ja.srt     Subs1 of episode 1
    Show - 01.en.ass     Subs2 of episode 1
    Show - 02.en.ass     no .ja file: episode 2 is skipped
    Show - 03.ja.srt     episode 3 is still episode 3
    Show - 03.en.ass
```

- The order is the GUI's file order, by name and not by number: pad the numbers (`02`, not `2`)
  so that episode 2 sorts before episode 10.
- The names must match the video's exactly, brackets and all; they are never read as wildcards.
  The project's Subs1 encoding applies to the `.ja` files and its Subs2 encoding to the `.en` files.
- With `--season`, audio clips come from the videos; a project that takes them from audio files is
  refused.
- Tags and media file names number and pad every episode as a run over the whole season would, so
  they stay the same from one run to the next whichever episodes were skipped.
- Without `--season`, `go` uses the project's own patterns, paired by position as in the GUI, and
  refuses when they match different numbers of files.

Before starting, `go` makes the checks the GUI's Go makes and lists every error at once: the output
directory can be written, a deck name, ffmpeg (and the animated snapshot encoder when that output is
on), and `claude` when snippets are grouped by AI through it. A warning (a video without the
project's audio stream) stops `go` unless `--yes` is given.

**AI grouping.** When the project's snippet mode is AI, `go` first asks the model to group every
episode it found, as the Preview does, then makes the cards of all of them in one run. The *AI
Grouping On Go* preference plays no part here.

- A cached answer is used without asking. The cache is the GUI's, so a Preview of the episode later
  costs nothing either. `--dry-run` shows `cached` or `not cached` per episode without asking.
- An episode the model cannot group is skipped, not grouped by the rules. One where only some
  requests failed keeps the rules' grouping for those parts, with a warning; it is not cached, so
  the next run asks again.
- Once the Claude usage limit is reached, every remaining episode without a cached answer is
  skipped without asking. Run the same command again after the limit resets: the episodes already
  grouped come from the cache, only the skipped ones are asked, and the TSV is written again with
  every episode.
- `--grouping rules` (or `off`) runs an AI project without the model.

After a run:

```text
#  Episode    AI           Status                                       Cards
1  Show - 01  grouped      done                                         148
2  Show - 02  -            skipped: no .ja file                         -
3  Show - 03  cached       done                                         151
4  Show - 04  usage limit  skipped: the Claude usage limit was reached  -
season TSV: D:\Anki\Show S1\Show_S1.tsv (2 of 4 episodes); exit 3
```

| Exit | Meaning |
| --- | --- |
| `0` | Every episode done (with `--dry-run`: every episode found and no check failed) |
| `3` | Done, but some episodes were skipped; the table says why |
| `1` | An error before any work (usage, project, folder, file counts, a failed check, no to a warning), or a step of the run failed: its message is on stderr, and the TSV the run had started is deleted |
| `130` | Cancelled (Ctrl+C); a TSV the run had started is deleted too |

### `season`: extract, retime, make the cards

The season folder holds the videos (`*.mkv`) and beside each its Japanese subtitle file, named
like the video, with or without a tag: `Show - 01.srt`, `Show - 01.ja.srt`, `Show - 01.ja.cc.ass`
(`.ass`, `.ssa` or `.srt`; a tag `en` or `eng` marks an English file, which is not taken). For
each video, one after another, `season`:

1. picks the English track with `mkvmerge -J`: of the English ASS, SSA and SRT tracks that are not
   forced, the one with the most events. Image tracks (PGS, VobSub) cannot be used.
2. extracts it with `mkvextract` to `s2s/<video name>.en.<ext>`.
3. retimes the Japanese file to it with `subsretimer --auto` into `s2s/<video name>.ja.<ext>` (and
   its report, `s2s/<video name>.retime.json`), reading it in the project's Subs1 encoding, which
   the retimed file keeps.

Then it makes the cards of every episode with a retime, as `go --season` makes them from `s2s`:
the same checks, the AI grouping first when the project groups by AI, one import file. The other
episodes are skipped and keep their numbers.

```text
Show S1/
  Show - 01.mkv
  Show - 01.srt              JP file of episode 1
  Show - 02.mkv
  Show - 02.ja.srt
  Show - 03.mkv              no JP file: episode 3 gets no cards
  s2s/                       written by season
    Show - 01.en.ass         the English track
    Show - 01.ja.srt         the JP file retimed to it: Subs1 of episode 1
    Show - 01.retime.json
    Show - 02.en.ass
    ...
```

In short: `season DIR --project FILE [--track ID] [--min-match F] [--force] [--only
extract|retime|go] [--dry-run]`, and `--deck`, `--grouping`, `--yes`, `--verbose`, `--prefs`,
`--no-prefs` as for `go`. From `subs2srs-cli --help`:

```text
  --project <file>  the project file: Subs1 encoding, Episode Start # and End #,
                    and everything go takes from it
  --track <id>      extract this track (an id mkvmerge -i shows) in every episode
                    instead of the pick
  --min-match <f>   passed to subsretimer: save a retime only when it covers at
                    least this share (0 to 1) of the EN lines. There is no default:
                    without it, none is passed and every retime is saved.
  --force           extract and retime every episode again, a fix saved from the
                    editor included. After changing --track this is needed: an
                    earlier extraction of another track in the same format has the
                    same name and would be kept.
  --only <stage>    run one stage: extract; retime (the EN files already in s2s;
                    MKVToolNix is not needed); or go (the cards of what s2s holds,
                    as go --season makes them)
  --dry-run         extract, retime, delete and make nothing: print the EN track
                    each episode would use, its JP file, whether its EN file and its
                    retime are there and would be kept, and whether the AI grouping
                    of each episode with a retime kept is cached; then go's checks
  --grouping, --deck, --yes, --prefs, --no-prefs, --verbose as for go
```

After `subs2srs-cli season 'D:\Anime\Show S1' --project 'D:\Anki\show.s2s.json' --deck Show_S1
--min-match 0.8` in PowerShell:

```text
Episode    EN track           Retime                           AI  Status                                    Cards
Show - 01  3 "English" 47 ev  2 segments, 97% of EN covered    -   done                                      46
Show - 02  3 "English" 45 ev  below --min-match (66%)          -   skipped: below --min-match (66%)          -
Show - 03  3 "English" 44 ev  no JP file named like the video  -   skipped: no JP file named like the video  -
Show - 04  3 "English" 44 ev  1 segment, 100% of EN covered    -   done                                      44
season TSV: D:\Anki\Show S1\Show_S1.tsv (2 of 4 episodes); exit 3
align by hand, then run again:
  subsretimer --target-encoding utf-8 --output "D:\Anime\Show S1\s2s\Show - 02.ja.srt" "D:\Anime\Show S1\s2s\Show - 02.en.ass" "D:\Anime\Show S1\Show - 02.ja.srt"
```

- *EN track* is the track's id, name and event count. A line under the table says when the picked
  track differs between episodes; `--track` then takes the same one from all.
- *Retime* gives subsretimer's segments (runs of lines moved by one offset) and the share of the EN
  lines the retimed lines cover; *AI*, *Status* and *Cards* are `go`'s columns (`-` in *AI* for a
  project that does not group by AI).
- For each pair subsretimer did not save (below `--min-match`, or no timed lines), the command
  under the table opens its editor on that pair. The files are given by full path (subsretimer too,
  unless it is the one on `PATH`), quoted for PowerShell on Windows and for a POSIX shell elsewhere.
  Its Save writes the `s2s` file the next run keeps and makes cards of. A JP file subsretimer
  cannot read (often a wrong Subs1 encoding) fails with its message and gets no command.

Run the same command again and it does only what is missing: an extracted file is kept, and so is
a retime newer than both its EN and JP files (an earlier run's, or a fix saved from the editor);
any other is done again. Keeping goes by the files' times, so a JP file replaced by one with an
older time (a copy that kept its time) needs `--force`. `--min-match` has no default yet: the
first real episodes are to show where it goes.

- In the season folder, `season` writes and deletes only in `s2s`: it deletes an episode's `.en`
  file of another track, its `.ja` files other than a kept retime, and before a retime its report.
  A retime that saves nothing leaves no `.ja` file. When an episode's JP or EN file cannot be told
  (none, or two), its `.ja` file is left alone, so an editor fix survives a renamed file; the
  episode is skipped.
- The EN files are read as UTF-8, as `mkvextract` writes them, whatever the project's Subs2
  encoding; a warning says when that differs.
- MKVToolNix (`mkvmerge`, `mkvextract`) is found in the *Tools Directory*, on `PATH`, and on
  Windows in `C:\Program Files\MKVToolNix` (or `C:\Program Files (x86)\MKVToolNix`), where its
  installer puts it without adding it to `PATH`. Without it `season` stops before any work
  (`--only retime` and `--only go` do not need it). `subsretimer` is found in the *Tools Directory*
  or on `PATH`; without it, the episodes that need a retime fail.
- `go`'s checks run after the extraction and the retime, so a missing deck name or ffmpeg is
  reported only then; the work done is kept for the next run.

Exit codes are `go`'s. An error before any work also covers a folder without `.mkv` files and
MKVToolNix not found; a failed check prints the table too. With `--only extract` or `--only
retime`, `0` means every episode got its EN file or retime, `3` that some did not.

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
   found the same way, MKVToolNix also in `C:\Program Files\MKVToolNix`, where its installer
   puts it.
3. Preferences live in `%APPDATA%\subs2srs\preferences.json`, logs in `%LOCALAPPDATA%\subs2srs\Logs`.

Troubleshooting: if windows render blank or the app crashes at startup on an old GPU or over
Remote Desktop, set the environment variable `GSK_RENDERER=cairo` (the bundled build already
defaults to it). The log directory above contains GTK warnings and the full ffmpeg command lines.

### Building on Windows

Install [MSYS2](https://www.msys2.org/), then in its shell
`pacman -S mingw-w64-ucrt-x86_64-gtk4 mingw-w64-ucrt-x86_64-ntldd`. That gives you the GTK 4
runtime in `C:\msys64\ucrt64\bin`. There are two ways to get a running app:

**A bundled build** (what the release zip contains): .NET and the GTK runtime are copied next to
the exe, so it starts from anywhere with no `PATH` setup.

```powershell
make publish-windows                  # with PowerShell 7 (pwsh)
make publish-windows PWSH=powershell  # with only Windows PowerShell 5.1
out/win-x64/subs2srs.exe
```

The commands in `.github/workflows/release.yml` do the same and zip the result.

**A development build** (`make build`, `dotnet build`, `dotnet run --project subs2srs`): the
output under `subs2srs/bin/` contains no GTK, so the GTK directory has to be on `PATH` when the
app starts:

```powershell
$env:PATH = "C:\msys64\ucrt64\bin;$env:PATH"
subs2srs/bin/Release/net10.0/subs2srs.exe
```

Note that it is `ucrt64\bin`, not `C:\msys64\usr\bin` (the MSYS2 shell tools), which is the one
people usually already have on `PATH`. To make it permanent, add `C:\msys64\ucrt64\bin` to your
user `PATH`, preferably near the end: it holds many DLLs that can shadow those of other programs.

`make install` and `make uninstall` are the Linux install targets and do nothing useful on
Windows.

If the app exits at startup with
`System.DllNotFoundException: Unable to load DLL 'libgtk-4-1.dll' or one of its dependencies`
(often preceded by `could not install GLib log writer: Unable to load DLL 'GLib'`), you are
running a development build without `C:\msys64\ucrt64\bin` on `PATH`. Use one of the two options
above.

## Install

### AUR

```sh
yay -S subs2srs-gui
```

### Manual

Linux only; on Windows see [Building on Windows](#building-on-windows).

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
