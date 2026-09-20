# Subs Re-Timer: research and re-integration plan

Status: plan, not yet implemented.

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
  Nothing uses them.

**Why the mono approach cannot simply be restored:** the exe needs Mono +
WinForms + SourceGrid. The whole point of the GTK4 port was to drop Mono and
`System.Drawing`. Re-adding the tool therefore means porting it, not bundling
it.

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

What is reusable from the fork today: `UtilsSubs.getOverlap`,
`UtilsSubs.shiftTiming`, `UtilsSubs.formatAssTime`, `ObjectCloner`,
`SubsParserASS.getAssDialogRegex` (private; would be made internal). What is
**not** reusable as-is: the fork's parsers are lossy for round-tripping. They
strip `{...}` tags, collapse `\N`, join SRT multi-lines with spaces, drop
empty-text lines, consult `Settings.Instance` for `RemoveStyledLines`, and
sort by start time. A retimer must write back exactly what it read, so it
needs a raw-preserving parse path.

## 3. Plan for re-adding it natively

### Phase 1: core library, no UI (testable in `subs2srs.Tests`)

New folder `subs2srs/Retimer/`:

1. `RetimerLine.cs`: `InfoLine` plus `RawLine`, `Layer`, `Style`, `MarginL/R/V`,
   `Effect`, and `OriginalIndex` (position in the file, so ASS lines can be
   written back in file order even though the working list is sorted).
2. `RetimerIO.cs`: `ParseAss`, `ParseSrt`, `WriteAss`, `WriteSrt`,
   `DefaultOutputPath`. Ports of the retimer's parsers and `SubsWriter`.
   No dependency on `Settings.Instance`. Detect original line ending and BOM
   and keep them.
3. `RetimerEngine.cs`: holds `Left`, `Right`, undo/redo stacks. Methods:
   `ShiftFrom(rightIndex, ms)`, `ShiftToMatch(leftIndex, rightIndex)`,
   `ClosestIndex(line, otherList)`, `BestOverlap`, `LargeGapIndices(list)`,
   `MismatchIndices(list, otherList)`, `AverageMismatch(includeNoOverlap)`,
   `Undo`, `Redo`, `IsDirty`. Use binary search on sorted start times instead
   of the original linear scans.
4. Tests: round-trip ASS and SRT fixtures byte-for-byte after a zero shift;
   shift-from-index; gap and mismatch detection on small synthetic files;
   undo/redo. Synthetic fixtures only; the tutorial subtitle files from the
   original package are third-party content and should not be committed.
5. Remove the `PathSubsReTimerFull` vestiges in `Settings.cs` and `Logger.cs`.

### Phase 2: GTK4 window

`subs2srs/DialogSubsRetimer.cs`, following the `DialogDuelingSubtitles`
pattern (`Gtk.Window`, `SetTransientFor`, nested `GLib.MainLoop`, `Run()`).
A non-modal window would suit a standalone tool better; either is fine, but
stay consistent with the other tool dialogs unless there is a reason not to.

Layout (mirrors the original, minus the chart):

- Top bar: Average Mismatch, left count `sel/total`, right count.
- Two `Gtk.ColumnView`s (Start, Dialog) side by side with the filename above
  each. Row coloring via CSS classes (`retimer-gap`, `retimer-mismatch`)
  applied in the `OnBind` handler; use the CSS injection already in
  `GtkColumnViewHelper`. Note GTK4 colors cells, not rows, so apply the class
  to both cells of a row.
- Detail strip: selected left/right text and start time, overlap %, signed
  diff, "Time Shift" button, Undo/Redo, Save, Save As.
- Menu or header buttons: Open Left, Open Right (async `Gtk.FileDialog`,
  same as the rest of the app), Save Right, Save Right As.
- Keyboard: Enter = Time Shift, Ctrl+Z/Y, Ctrl+O / Ctrl+Shift+O, Ctrl+S,
  Ctrl+Up/Down = previous/next orange line, Left/Right = move and select
  closest line on the other side. Right-click on a row = select closest on
  the other side. Drag-and-drop of files onto a list via `Gtk.DropTarget`.
- Entry point: a new frame "Subs Re-Timer" in `MainWindow.BuildToolsTab`
  with a one-line description and a "Subs Re-Timer..." button, placed after
  Dueling Subtitles like the original menu order. Prefill Left/Right from
  the main window's Subs1/Subs2 fields when they point at a single `.ass`
  or `.srt` file.
- Phase 2b (optional): timeline chart as a `Gtk.DrawingArea` with Cairo,
  ported from `ChartSubs.cs`. The chart is a nice-to-have; the list colors
  plus overlap % carry the workflow on their own.

Docs and packaging:

- README: drop the "SubsReTimer — separate tool" line from Removed
  components; add a short usage section derived from `usage.html`
  (workflow: left = reference, right = to be retimed, work top to bottom,
  orange lines are where shifts are needed, gray lines have no counterpart).
- CHANGELOG entry and version bump per repo convention.
- No separate desktop entry needed since it lives inside the app. If a
  direct launcher is wanted later, add a `--retimer` flag in `Program.cs`
  and a `dist/subs2srs-retimer.desktop`.

### Phase 3: automatic alignment (the requested automation)

Add `RetimerAutoAlign.cs` and an "Auto Align" button. The manual tool's
single operation (shift everything from index *i* by *d*) is exactly the
right primitive; automation only has to find the breakpoints and offsets.
That can be done from timing structure alone:

1. **Candidate offsets.** For every right line, for every left line whose
   start is within a window (say ±10 min), take `d = left.start − right.start`,
   quantize to 100 ms bins and histogram over the whole file. The top peaks
   are the candidate offsets. Sponsor segments, the OP, and eyecatches show
   up as a handful of distinct peaks (0, ±15 s, ±90 s, and their sums).
2. **Per-line scores.** For each right line and each candidate offset,
   score = best overlap of the shifted line against the left file (binary
   search on starts). Zero if nothing overlaps.
3. **Segmentation.** Dynamic programming over right lines with the candidate
   offset as state: minimize `−score` plus a switch penalty λ (a few lines'
   worth of overlap) whenever the offset changes. Backtrack to get a
   piecewise-constant offset with few breakpoints. Lines that overlap
   nothing under any offset (CC-only sound cues, music) simply inherit their
   segment's offset, which is the shift-all behaviour that motivates the
   feature.
4. **Refinement.** Within each segment, replace the binned offset with the
   median of `left.start − right.start` over the pairs that overlap.
5. **Apply as a sequence of `ShiftFrom` calls** so each breakpoint is one
   undoable step and the user reviews the result with the existing
   orange/gray coloring and the mismatch statistic.

Cost is `O(N · K · log N)` for N lines and K candidates, negligible for
episode-sized files. This is a simplified version of the model used by
`alass` (piecewise-constant offsets with a split penalty), which is a
language-independent subtitle-to-subtitle aligner. `alass` could also be
offered as an optional external engine found on `PATH`, the same way ffmpeg
and mkvtoolnix are, but the native version above is small enough to own and
keeps the tool self-contained.

Tests: build a synthetic reference file, derive the other file by inserting
or removing 15 s / 90 s blocks at a few points, adding a few extra lines
with no counterpart, and jittering timings by ±80 ms; assert that the
detected breakpoints and offsets match the construction.

### Phase 4 (only if Phase 3 proves insufficient): LLM assist

See section 4. If added, keep it behind an `IAnchorFinder` interface so the
timing-based aligner remains the default and the LLM is an optional,
narrowly scoped helper.

## 4. Is the LLM feature needed?

Short answer: not for the core problem. The proposed pipeline is

1. semantically match lines across languages,
2. detect where matched pairs disagree in time,
3. shift everything from the first disagreement,
4. repeat to the end.

Steps 2 to 4 are the original tool's manual workflow and are trivially
automated once anchors exist. Only step 1 needs a matcher, and timing
structure already provides one: both files describe the same dialogue on
the same footage, so the pattern of line durations and gaps is a shared
fingerprint. Piecewise-offset alignment from timings alone is a solved
problem (see `alass`), is deterministic, runs offline in milliseconds, has
no API key, cost, or privacy surface, and is unit-testable.

Where an LLM would genuinely add something:

- **Ambiguous regions**: long stretches with sparse dialogue, or a CC file
  with many sound-cue lines that swamp the timing fingerprint, where two
  candidate offsets score nearly equally.
- **Verification**: after auto-alignment, sampling a few pairs per segment
  and asking "do these say the same thing?" gives a confidence report and
  catches a wrong segment.
- **Segmentation differences**: one English line covering two Japanese
  lines, or vice versa. Timing overlap handles this reasonably; text does
  not obviously do better because translations are rarely line-for-line.

Costs of an LLM-first design worth weighing:

- Whole-file cross-language pairing of ~500 × ~500 lines needs a
  long-context call per episode, an API key, network access, and money;
  batch processing a season multiplies this.
- Index outputs from models are noisy (off-by-one, hallucinated pairs) and
  need the same overlap sanity checks the timing method already applies.
- Translated subtitles versus Japanese CC are not literal, so "semantic
  match" is fuzzy anyway; the timing method's precision (tens of ms) is
  better than what text pairing can provide.

Recommendation: implement Phases 1 to 3. Ship the timing-based Auto Align
and use it on real EN/JP pairs. Add the LLM only as a targeted assist for
low-confidence breakpoints if real files show the timing method failing
there. If it is added, query it with a small window of lines around the
uncertain breakpoint rather than the whole file.
