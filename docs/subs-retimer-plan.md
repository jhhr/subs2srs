# Subs Re-Timer: research and integration plan

Status: steps 1 to 4 of section 7 are implemented (`subsretimer` Core,
tests, CLI with `--auto` and the stdout contract, auto-align, and the
subs2srs launcher). Step 5, the GTK editor window, is open.

Decision taken: the retimer lives in its own repository
(https://github.com/jhhr/subsretimer) and subs2srs integrates with it
through the command-line contract in section 4.

## 1. How SubsReTimer was included in subs2srs-mono

Findings from `fkzys/subs2srs-mono` and `nihil-admirari/subs2srs-net48-builds`:

- **It was never part of the subs2srs source.** The mono repo bundles a
  *prebuilt* `SubsReTimer.exe` (v1.0, dated 2013-02-15, .NET 3.5 / WinForms)
  plus `SourceGrid.dll` (a WinForms grid control), `startup.ass`, a `Help/`
  folder (`usage.html` + screenshots) and `Tutorial_Files/` under
  `subs2srs/Utils/SubsReTimer/`. There is no C# source for it in that repo.
- The binary comes from nihil-admirari's build script, which downloads the
  original subs2srs v27.0 source archive from SourceForge, extracts
  `subs2srs/SubsReTimer/SubsReTimer/`, applies a small csproj patch
  (SourceGrid reference + long-path manifest) and builds it with MSBuild
  against `siemens/sourcegrid`.
- **Integration was one menu item.** `FormMain` has
  `Tools → "Subs Re-Timer..."` whose handler is a single line:
  `Process.Start(ConstantSettings.PathSubsReTimerFull)`, where the path is
  `<appdir>/Utils/SubsReTimer/SubsReTimer.exe`. No data flows between the two
  programs; the retimer is a separate process with its own file dialogs.
- Packaging: the Makefile copies the whole `Utils/SubsReTimer` folder to
  `/opt/subs2srs/Utils/SubsReTimer/` and installs `dist/subsretimer.desktop`
  (`Exec=mono /opt/subs2srs/Utils/SubsReTimer/SubsReTimer.exe`).
- The GTK4 fork lists it under "Removed components" in the README, but two
  vestiges remain: `ConstantSettings.PathSubsReTimerFull = FindInPath("SubsReTimer")`
  in `subs2srs/Settings.cs` and a matching line in `subs2srs/Logger.cs`.
  Nothing uses them. They become the launch hook (section 4).

**Why the mono approach cannot simply be restored:** the exe needs Mono +
WinForms + SourceGrid. The whole point of the GTK4 port was to drop Mono and
`System.Drawing`. The tool has to be ported.

## 2. What the original tool actually does

Source: `subs2srs_v27.0_Source_Code.7z` → `subs2srs/SubsReTimer/SubsReTimer/`
(GPL-3.0, same license as this fork, so a direct port is license-compatible).
About 4,200 lines including the WinForms designer files; the logic worth
porting is roughly 1,200 lines.

| File | Lines | Role |
|---|---|---|
| `FormMain.cs` / `FormMain.Utils.cs` | 579 + 819 | UI flow and all algorithms (see below) |
| `ChartSubs.cs` | 441 | Gantt-style timeline of both files (GDI+ drawing) |
| `SubsParserASS.cs` / `SubsParserSRT.cs` | 241 + 195 | Parsers that keep the raw line and all ASS fields |
| `SubsWriter.cs` | 139 | Writes ASS by patching original Dialogue lines in place; rewrites SRT |
| `InfoLine.cs` | 206 | Line model with `RawLine`, `Layer`, `Style`, `MarginL/R/V`, `Effect` |
| `UtilsSubs.cs`, `UtilsCommon.cs`, `ObjectCloner.cs` | ~450 | overlap, shift, time formatting, deep clone for undo |

Algorithms (all timing based, **no text matching at all**):

- **Closest line**: nearest start time in the other file (`findClosestLine`).
- **Best overlap**: overlap ratio of a line against the other file, scanning
  forward and stopping after 5 consecutive non-improvements (`getBestOverlap`).
- **Row colors**: orange when the gap from the previous line's start is
  > 28,000 ms (`LARGE_TIMING_GAP`); gray when best overlap ≤ 0.
- **The only edit**: select a line on each side, press Time Shift; every
  right-hand line from the selected index onward is shifted by
  `left.start − right.start`. Undo/redo by cloning the whole list.
- **Stats**: average mismatch (all lines : non-gray lines), line counts,
  overlap % and signed diff for the selected pair.
- **Save**: ASS output preserves headers/styles by replacing each original
  `Dialogue:` line with the retimed one; SRT output is regenerated from raw
  text. Output defaults to `<name>_retimed.<ext>`.

The fork's existing parsers are lossy for round-tripping (they strip `{...}`
tags, collapse `\N`, join SRT multi-lines, drop empty-text lines, consult
`Settings.Instance`, and sort). The retimer needs its own raw-preserving
parse path, which is another reason for it to own its code.

## 3. Repository layout

Two repositories. subs2srs depends on the retimer only at run time, through
the executable found on `PATH`, exactly like ffmpeg and mkvtoolnix. No
submodule, no shared build. This was chosen over a submodule because it
avoids the AUR submodule dance, avoids locking both repos to the same
GirCore version, and the command-line contract below gives the same UX.

### `subsretimer` repository

```
subsretimer/
  SubsRetimer.Core/    net10.0 class library, no GTK dependency
    RetimerLine.cs     line model: times, text, RawLine, Layer, Style,
                       MarginL/R/V, Effect, OriginalIndex
    RetimerIO.cs       ParseAss/ParseSrt/WriteAss/WriteSrt/DefaultOutputPath,
                       keeps BOM and original line endings
    RetimerEngine.cs   Left/Right lists, ShiftFrom, ShiftToMatch,
                       ClosestIndex, BestOverlap, LargeGapIndices,
                       MismatchIndices, AverageMismatch, Undo/Redo, IsDirty
    AutoAlign.cs       timing-based piecewise-offset alignment (section 5)
  SubsRetimer.Gtk/     class library: the editor window and widgets
    RetimerWindow.cs   two ColumnViews, stats strip, Time Shift, Undo/Redo,
                       Open/Save, keyboard shortcuts, Auto Align button
  SubsRetimer/         thin executable `subsretimer`: argument parsing,
                       GUI or headless mode, stdout protocol
  SubsRetimer.Tests/   xUnit: round-trip, shift, gap/mismatch, undo,
                       auto-align on synthetic fixtures, CLI protocol
  dist/                subsretimer.desktop, launcher script
  Makefile             build / test / install (mirrors subs2srs)
  LICENSE              GPL-3.0-or-later (port of GPL code)
```

The `Gtk` library is separate from the executable so that an in-process
integration (ProjectReference from subs2srs) stays one step away if the
process-based one ever proves insufficient. Do not start there.

Third-party subtitle files from the original package (`Tutorial_Files/`)
are not committed; tests use synthetic fixtures.

### `subs2srs` repository

Only the launcher and the hand-back (section 4). No parsing or retiming
code is added to subs2srs.

## 4. Command-line contract

```
subsretimer [options] [REFERENCE] [TARGET]

  REFERENCE   subtitle file already timed to the video (left pane)
  TARGET      subtitle file to be retimed (right pane)

  --auto                 run auto-align and save without opening the GUI
  -o, --output PATH      output path (default: <TARGET>_retimed.<ext>)
  --ref-encoding NAME    encoding of REFERENCE (default utf-8)
  --target-encoding NAME encoding of TARGET (default utf-8)
  --print-output         print the path of each saved file to stdout
  --version, --help
```

Rules that subs2srs relies on:

- **stdout is reserved for saved paths.** With `--print-output`, every
  successful save (GUI or `--auto`) prints exactly one line, the absolute
  path of the file written, flushed immediately. Nothing else is ever
  printed to stdout. All diagnostics go to stderr.
- **Exit codes.** `0` = at least one file was saved. `2` = exited without
  saving (user closed the GUI, or `--auto` found nothing to do). `1` = error,
  message on stderr. Any other value is treated as an error.
- **Arguments are optional in GUI mode.** With zero or one file the GUI
  opens with the panes that were given filled in.
- **`--auto` requires both files** and never opens a window. It does not
  overwrite an existing output unless `--output` names it explicitly.
- Batch use is a shell loop over `--auto`; no batch syntax in v1.

### subs2srs side

1. `ConstantSettings`: rename the vestige to `PathSubsRetimerExe =
   FindInPath("subsretimer")`; keep the Logger line in step.
2. New `DialogSubsRetimerLaunch` opened from a "Subs Re-Timer" frame in the
   Tools tab (after Dueling Subtitles, matching the original menu order):
   - Reference: radio `Subs1` / `Subs2`. Default `Subs2`, since in the
     common case the native-language subs match the video and the
     target-language Subs1 is the one to retime. Remember the last choice
     in preferences.
   - Two path entries prefilled from the main window's Subs1/Subs2 fields
     and their encodings. Editable, with Browse buttons.
   - Checkbox "Auto-align without opening the editor" → adds `--auto`.
   - If the executable is not on `PATH`, the frame shows a hint naming the
     package instead of the button.
3. Launch with `--print-output`, capture stdout, `WaitForExitAsync` with
   the app's existing async/`IProgressReporter` pattern so the main window
   stays responsive. On exit code `0`, take the last stdout line, verify the
   file exists, and ask "Use `<name>_retimed.ass` as Subs1?" (the side that
   was retimed). On yes, set that entry. On `2`, do nothing. On `1` or
   unknown, show stderr in the usual error dialog.
4. Later: when the Subs fields hold wildcard patterns, resolve both with
   `UtilsSubs.getSubsFiles`, pair by index, run `--auto` per pair with a
   progress bar, and offer to rewrite the pattern to the `_retimed` files.
   Not in v1.

## 5. Automatic alignment (in `SubsRetimer.Core/AutoAlign.cs`)

The manual tool's single operation (shift everything from index *i* by *d*)
is the right primitive; automation only has to find the breakpoints and
offsets, and timing structure alone is enough for that:

1. **Candidate offsets.** For every target line, for every reference line
   whose start is within ±10 min, take `d = ref.start − target.start`,
   quantize to 100 ms bins and histogram over the file. The top peaks are
   the candidates. Sponsor segments, the OP and eyecatches appear as a few
   distinct peaks (0, ±15 s, ±90 s, and their sums).
2. **Per-line scores.** For each target line and candidate offset,
   score = best overlap of the shifted line against the reference (binary
   search on starts). Zero if nothing overlaps.
3. **Segmentation.** Dynamic programming over target lines with the
   candidate offset as state: minimize `−score` plus a switch penalty λ
   (a few lines' worth of overlap) whenever the offset changes. Backtrack
   to a piecewise-constant offset with few breakpoints. Lines that overlap
   nothing under any offset (CC-only sound cues, music) inherit their
   segment's offset: the shift-all behaviour that motivates the feature.
4. **Refinement.** Within each segment, replace the binned offset with the
   median of `ref.start − target.start` over the overlapping pairs.
5. **Apply as a sequence of `ShiftFrom` calls** so each breakpoint is one
   undoable step and the user reviews the result with the orange/gray
   coloring and the mismatch statistic. `--auto` applies the same sequence
   and saves.

Cost is `O(N · K · log N)`, negligible for episode-sized files. This is a
simplified version of the model used by `alass` (piecewise-constant offsets
with a split penalty).

Tests: build a synthetic reference, derive the target by inserting or
removing 15 s / 90 s blocks at a few points, adding lines with no
counterpart, and jittering by ±80 ms; assert the detected breakpoints and
offsets match the construction.

## 6. Is the LLM feature needed?

Not for the core problem. The proposed pipeline (semantically match lines,
detect timing disagreements, shift from the first one, repeat) is the
original tool's manual workflow with an LLM supplying the anchor pairs.
Section 5 supplies the anchors from timing structure instead: deterministic,
offline, free, and unit-testable. The shift-all behaviour for CC-only lines
falls out naturally.

Where an LLM would genuinely add something:

- **Ambiguous regions**: sparse dialogue, or a CC file whose sound-cue lines
  swamp the timing fingerprint, where two candidate offsets score equally.
- **Verification**: sample a few pairs per segment and ask whether they say
  the same thing, giving a confidence report.
- **Segmentation differences** (one line covering two on the other side) are
  handled reasonably by overlap; text does not obviously do better because
  translations are rarely line-for-line.

Costs of an LLM-first design: a long-context call per episode, an API key,
network and money; noisy index outputs that need the same overlap sanity
checks anyway; and lower precision than timing (tens of ms).

Recommendation: ship sections 3 to 5 and use them on real EN/JP pairs. Add
the LLM only as a targeted assist for low-confidence breakpoints if real
files show the timing method failing there, queried with a small window of
lines around the uncertain breakpoint, behind an `IAnchorFinder` interface
in `SubsRetimer.Core`.

## 7. Order of work

1. `subsretimer` repo: Core + Tests (round-trip, shift, gap/mismatch, undo).
2. `subsretimer` repo: executable with `--auto`, `--print-output`, exit
   codes; CLI tests that spawn the process and assert the stdout protocol.
3. `subs2srs`: launcher dialog and hand-back; test against step 2's
   headless mode.
4. `subsretimer` repo: `AutoAlign` and its synthetic-fixture tests; wire
   into `--auto`.
5. `subsretimer` repo: GTK window (the manual editor), Auto Align button.
6. Packaging and docs in both repos: README, CHANGELOG, desktop entry,
   AUR `optdepends` on the subs2srs side.

Steps 1 to 4 need only the .NET SDK and are fully testable headless. Step 5
needs a GTK4 desktop to verify by hand.
