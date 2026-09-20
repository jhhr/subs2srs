# GTK / GirCore, Windows, packaging, CI

## GirCore 0.7.0

The UI is GTK 4 through the managed GirCore bindings, pinned at **0.7.0** (the Windows bundle relies on
the DLL names that version loads, e.g. `libgtk-4-1.dll`, which match MSYS2's). All former P/Invokes into
GTK were replaced by managed calls (`GtkColumnViewHelper`); do not add new ones, they are what broke
Windows.

GirCore 0.7 ships **no XML docs** and hides nested signal-args types, and its names differ from the C
API in ways you cannot guess. When you need an API, reflect over the assemblies instead of guessing: a
scratch console project (outside the tracked tree, e.g. under `.chains/scratch/`) that references the
same GirCore packages, loads the assemblies and prints method signatures, tolerating
`ReflectionTypeLoadException`. Facts already established this way, by compiling:

| Need | GirCore 0.7 spelling |
| --- | --- |
| All top-level windows | `Gtk.Window.GetToplevels()` (there is no `ListToplevels`) |
| Per-frame callback | `Widget.AddTickCallback((Widget, Gdk.FrameClock) => bool)` |
| Key name → keyval | `Gdk.Functions.KeyvalFromName` (there is **no** `AcceleratorParse`; `KeyBinding.cs` parses accelerator strings itself) |
| Key events | `EventControllerKey.OnKeyPressed` |
| Drag and drop | `DragSource` / `DropTarget` |
| Render a widget to PNG | `Gtk.WidgetPaintable` → `Gtk.Snapshot` → `.ToNode()` → `((Gtk.Native)win).GetRenderer()` (fallback: a `CairoRenderer` with `RealizeForDisplay`) → `RenderTexture` → `SaveToPng`; rects via `Graphene.Rect.Alloc().Init(...)` |
| GLib log hook | `GLib.Functions.LogSetWriterFunc` with `GLib.LogWriterFunc(LogLevelFlags, LogField[]) → LogWriterOutput` (`GLibLogForwarder` sends warnings/criticals to `Logger`, then calls the default writer) |

## GTK behaviours that bit us

- **`ColumnView` / list widgets bind Up/Down with every modifier.** A key controller in the default
  (bubble) phase never sees `<Control>Up`/`<Control>Down`. The preview's controller is in the **capture**
  phase for that reason. Tests call the handler directly, so they cannot catch a regression here.
- **Rebuilding a list model scrolls to the top and destroys the focused row.** The preview re-binds rows
  in place after an edit and restores focus to the row after a button press or a drop. Do not "simplify"
  this to a model rebuild; `PreviewGroupingScrollTests` guards it.
- **`Widget.OnDestroy` does not fire for a window closed with `Close()`.** Ask
  `Gtk.Window.GetToplevels()` whether a window is still alive.
- **A `close-request` handler that always returns true makes the window undestroyable.**
  `DialogPreview` hides on close to keep its state, and returns false once it is really being destroyed
  (`_destroyed`). Keep that distinction when changing close handling.
- Long work runs in `Task.Run` with `async/await` and reports through `IProgressReporter`; never block
  the GTK thread (the old `BackgroundWorker` design deadlocked on Wayland). `GtkSynchronizationContext`
  brings continuations back to the GTK thread.
- While the preview is busy (AI call) it disables the list, the action rows and Go but keeps **Cancel**
  enabled; it does not grey out the whole window, because Cancel has to stay clickable.
- Tool dialogs (`DialogMkvExtract`, `DialogDuelingSubtitles`, `DialogSubsRetimer`) are modal
  `Gtk.Window`s whose `Run()` spins a **nested `GLib.MainLoop`** until `close-request`. An
  `async void` click handler that `await`s a process inside such a dialog resumes on the GTK thread
  through `GtkSynchronizationContext` and the nested loop; `Close()` from inside the continuation quits
  the loop and `Run()` returns. `DialogSubsRetimer.OnRunClicked` is the reference for that pattern,
  including cancelling the child process on close.
- `GSK_RENDERER=cairo` is the safe renderer: old GPUs, Remote Desktop, Xvfb and CI all render blank or
  crash on the GL renderers. The Windows build defaults to it.

## Windows specifics in the app

- `WindowsRuntimeSetup.Apply()` runs first in `Main`. When the bundled layout is present next to the exe
  it sets `XDG_DATA_DIRS`, `GSETTINGS_SCHEMA_DIR`, `GDK_PIXBUF_MODULE_FILE`/`MODULEDIR` and
  `GSK_RENDERER=cairo`, each **only if unset**, so a developer's MSYS2 environment wins.
- `OutputType` is `WinExe` only for `win-x64` (no console window), so nothing is visible on stdout/stderr
  there: diagnostics must go through `Logger`. `app.manifest` sets PerMonitorV2 DPI, long paths and the
  UTF-8 code page.
- `UtilsCommon.RegisterEncodings()` registers `CodePagesEncodingProvider` (Shift-JIS etc.) and must run
  before any subtitle parsing: it is called from `Program.Main` **and** `SubsProcessor.StartAsync`
  (tests enter through the latter). The package is part of the .NET 10 shared framework; adding a
  `PackageReference` for it produces warning NU1510.
- External tools: see the end of [architecture.md](architecture.md). ffmpeg is never bundled (size,
  licensing); `MainWindow.CheckExternalTools()` disables Go with an install hint when it is missing.
  `subsretimer` is optional and resolved the same way; the Tools-tab button is disabled with a hint
  when it is absent. Nothing on Windows has run it yet (the tool has no Windows build or `.exe` name
  convention beyond what `ResolveTool` assumes).
- Build paths with `Path.Combine`; several Windows bugs were hard-coded `/` temp paths.
- The app writes **no log at startup** unless something fails; "a log file exists" is not a liveness
  signal.

## Packaging (`make publish-windows`)

`dotnet publish -r win-x64 --self-contained` → `dist/windows/bundle-gtk.ps1` → `dist/windows/smoke.ps1`
→ zip (in `release.yml`, on `v*` tags, attached to the GitHub release). About 144 MB unzipped.

- `bundle-gtk.ps1` copies seed DLLs plus their `ntldd -R` closure from MSYS2 UCRT64 flat next to the exe,
  compiled GSettings schemas, a pruned Adwaita icon set (`-FullIconTheme` for all), the png/jpeg/svg
  pixbuf loaders with a **relative** `loaders.cache`, and licences gathered via `pacman -Qqo`.
  Requires `mingw-w64-ucrt-x86_64-gtk4` and `-ntldd`.
- `smoke.ps1` strips MSYS2 from PATH, starts the exe and passes when the process is alive, a top-level
  window titled "subs2srs" exists and `preferences.json` was created. It waits up to `-TimeoutSeconds`
  (60) because a cold start right after publish takes ~7 s while Defender scans the new DLLs. It cannot
  redirect `%APPDATA%` (shell API, not env vars), so it removes a `preferences.json` it created itself.
- The scripts must run on **Windows PowerShell 5.1** as well as 7. In 5.1, a native command's redirected
  stderr becomes a terminating error under `$ErrorActionPreference='Stop'`; that is why `bundle-gtk.ps1`
  wraps native calls in `Invoke-Native`. No `&&`, `||`, `?:`, `??` in these scripts.

## CI (`.github/workflows/ci.yml`)

Two jobs, Linux (apt GTK + Xvfb + ffmpeg) and Windows (MSYS2 GTK + Chocolatey ffmpeg); both build
Release and run both test projects, uploading `.trx` results and UI screenshots.

- There is no solution file: each project is restored explicitly with `--locked-mode`. A new project or
  package that is not in a `packages.lock.json` / not listed in the workflow fails CI at restore.
- The workflow only triggers on changes under the project folders, `dist/`, `Makefile` and the workflow
  itself; a docs-only push runs nothing.
- `msys2/setup-msys2` treats `location` as a *parent* directory; the job uses `release: false` (the
  runner's preinstalled MSYS2) and derives paths from `MSYS2_LOCATION`. CI sets `XDG_DATA_DIRS` and
  `GSETTINGS_SCHEMA_DIR` for the UI tests; a local MSYS2 development run has not needed them.

## Setting up a Windows dev machine

- .NET 10 SDK. If `dotnet restore` finds no packages, the machine has no NuGet source:
  `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`.
- MSYS2, then `pacman -S mingw-w64-ucrt-x86_64-gtk4 mingw-w64-ucrt-x86_64-ntldd`. Run pacman from
  PowerShell or cmd (`C:\msys64\usr\bin\pacman.exe`); from Git Bash its arguments get dropped. A stale
  package database shows up as mirror 404s: `pacman -Syu` (needs `y` on stdin), and remove a leftover
  `var/lib/pacman/db.lck` if it refuses to start.
- ffmpeg on PATH (e.g. `winget install Gyan.FFmpeg` or Chocolatey). mkvtoolnix and mp3gain are optional;
  nothing in the test suites needs them.
- Run the app: GTK on PATH, then `dotnet run --project subs2srs`.

Not verified anywhere yet: column drag-resize on Linux after the managed `ColumnViewColumn` rewrite, and
the zip on a clean Windows machine (Windows Sandbox: unzip, open every dialog, generate a deck).
