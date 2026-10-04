# Architecture: pipeline, snippets, media cutting

The classes carry XML comments that explain each piece. This page explains what spans files: the order
things happen in, the invariants the pieces rely on, and why some choices look odd.

## The pipeline

`SubsProcessor.DoWork` runs these steps; the names are the progress-bar labels, so they are greppable.

| Step | Code | Notes |
| --- | --- | --- |
| Combine subs | `WorkerSubs.combineAllSubs` | Parse subs1, drop badly timed lines, sentence-join (below), time shift, parse subs2, pair subs2 to subs1 |
| Inactivate lines | `WorkerSubs.inactivateLines` | Include/exclude text, duplicates, length and duration filters. Sets `InfoCombined.Active = false`; **never removes** |
| AI grouping | `WorkerSubs.runAiGrouping` | Only counted and run when it will really run: mode AI **and** no joins from the preview **and** the *AI Grouping On Go* preference |
| Group into snippets | `WorkerSubs.groupIntoSnippets` | Always runs. Takes joins from the preview, else from the AI step, else computes them from the rules (or none in mode Off), runs `Repair`, then materialises snippet-level `InfoCombined`s |
| Find context lines / Remove inactive lines | `WorkerSubs` | From here on the list is cards, not subtitle lines |
| Generate import file | `WorkerSrs` | TSV. The animated snapshot column sits right after Snapshot and only exists when the feature is on |
| Audio / snapshots / animated snapshots / video | `WorkerAudio`, `WorkerSnapshot`, `WorkerAnimatedSnapshot`, `WorkerVideo` | `Parallel.ForEach` bounded by `ConstantSettings.EffectiveParallelism` |

`SubsProcessor.determineNumSteps` must agree with the steps that actually run, or the progress bar lies.

### Preview ↔ Go

`DialogPreview` runs the first two steps itself, then shows **lines, not snippets**: one row per
subtitle line with the grouping drawn on top. It never materialises snippets. When the user presses Go
from the preview, `MainWindow.GoAsync(WorkerVars)` hands the preview's `CombinedAll` and its join vector
(`WorkerVars.Joins`) to the pipeline, which then skips the first steps and the AI step. That is how
manual edits survive Go. `WorkerVars.ProposedJoins` / `ProposalProducer` / `ProposalModel` /
`ProposalPromptVersion` exist only for the validation export (what the producer proposed vs. what the
user saved).

Consequence: anything that must be visible or editable before media generation belongs **before**
"Group into snippets" and must work on the flat line list.

Go with mode AI but without preview joins and without *AI Grouping On Go* silently uses the rules and
writes a log line. That is deliberate (no dialog in the middle of Go).

## The command line (`subs2srs-cli`)

`subs2srs.Cli` (assembly `subs2srs-cli`) is the pipeline's second caller: a console exe on every
platform, because the GUI is a `WinExe` on Windows and has no console. `Program.Main` sets up the
standard streams and Ctrl+C and calls `CliRunner.RunAsync(args, stdout, stderr, token)`, which the
tests call in-process. It references the app (`InternalsVisibleTo("subs2srs-cli")`); what the GUI and
the command line share is GTK-free code in `subs2srs/` (`ProjectFiles`, `GoChecks`, `PipelineResult`,
`SubsRetimerLauncher`; also `MkvTracks`, `MkvExtract` and `RetimeReport`, which only `season` uses so
far). `CliRunner.RunAsync` dispatches on the command: `go` (`CliRunner.GoAsync`) or `season`
(`SeasonCommand.RunAsync`, below).

- Preferences are read with `PrefIO.ReadFile`, never `PrefIO.read`, which writes a missing file and
  migrates an old one: the command line never writes the GUI's preferences.
- `UtilsMsg` already writes `ERROR:`/`INFO:`/`CONFIRM:` lines to `Console.Error` before calling its
  hooks, so only `OnShowConfirm` is set (it answers `--yes`). File logging is off; `--verbose` sets
  `Logger.Instance.Echo`.
- stdout carries the table only; progress (`ConsoleProgress`), the checks and `UtilsMsg` go to
  stderr. A redirected stream gets a UTF-8 writer without a BOM; a console gets
  `Console.OutputEncoding` UTF-8, put back on exit because the parent shares that console.

### Setting up a run

`go` starts from `ProjectIO.Load`, which restores every saved setting but leaves the `Files` arrays
empty and `EpisodeNumbers`/`EpisodeCountForNames` null (`RestoreFrom`). Everything that describes the
run is therefore set after the load:

- **Pattern mode** (no `--season`): `ProjectFiles.Resolve()`, which `MainWindow.SaveSettings` calls
  too: patterns into `Files`, cut to *Episode End #*, `UpdateAudioFilenameFormats()`. Then
  `EpisodeList.Pair` refuses unequal counts, which the pipeline would hit part-way as an index out of
  range.
- **Season mode**: `EpisodeList.FromSeason` (a thin wrapper over the pure `ForSeason`), then
  `CliRunner.SetUpSeasonRun`:
  - `Subs[0/1].Files` and `VideoClips.Files`: the ready episodes, in order.
  - `Subs[0/1].FilePattern` too, set to the first ready episode's own file. The pipeline reads the
    patterns, not only `Files`: `Subs[1].FilePattern != ""` is how `WorkerSrs` and `WorkerSubs` know
    there is a Subs2, and both patterns are checked for VobSub. A name, not a wildcard: season file
    names hold `[`.
  - `AudioClips.FilePattern` and `Files` cleared: `WorkerAudio` indexes `AudioClips.Files` by episode
    whenever it is not empty, even with audio from the video, so a project's leftover audio pattern
    would crash the run. (`FromSeason` refuses a project with audio from audio files.)
  - `EpisodeNumbers`: each ready episode's number, its position among the videos plus the start
    number, so a skipped episode does not renumber the others. `Settings.EpisodeNumber(index)` is the
    one place tags, sequence markers, media names, log lines, the parser and the per-episode
    time-shift rules read it from; the list must be as long as the `Files` arrays.
  - `EpisodeCountForNames`: the season's video count within *Episode End #*, skipped episodes
    included. Otherwise `${0:episode_num}` is padded to the digits of the run's own episode count, so
    a run of 9 episodes would name episode 3 `3` and a run of 10 `03`, and tags and media names would
    change from one run to the next.
  - `ConstantSettings.UpdateAudioFilenameFormats()`, which pattern mode gets from `Resolve`.

The GUI sets neither: `EpisodeNumber(index)` falls back to `index + EpisodeStartNumber` and the padding
to the run's count, as before.

### Checks shared with Go

`GoChecks.Run(settings, audioStreamIndex, aiGroupingRuns)` returns every problem at once: errors (the
output dir, created if missing, written and cleaned up again; deck name; ffmpeg; the animated snapshot
encoder; `claude` for a `terminal-` model) and the audio-stream warning. Each guards a failure that
would otherwise come part-way through a run. `MainWindow.GoAsync` calls it after `SaveSettings`, off
the GTK thread (the audio check runs ffprobe on every video), shows all errors in one dialog and asks
each warning. `go` lists them on stderr and refuses on an error, and puts each warning to
`UtilsMsg.showConfirm`. The callers differ in `aiGroupingRuns`: the GUI passes
`WorkerSubs.aiGroupingOnGoApplies` (Go asks the model only when its own AI step runs), `go` passes
"mode is AI" (its pre-pass always asks). `GoChecks.AudioStreamIndex` turns a loaded project's
`VideoClips.AudioStream` into the GUI list's position.

### AI first, then one run

With snippet mode AI, `AiPrePass` runs before the pipeline: *Combine subs* and *Inactivate lines* (the
`WorkerSubs` calls `DoWork` makes, on a `WorkerVars` like `StartAsync`'s), then `AiGrouper.Group` per
episode. Episodes left without an AI grouping are taken out of the line lists, the `Files` arrays and
`EpisodeNumbers` (`DropSkipped`, `KeepEpisodes`; `EpisodeCountForNames` stays). The rest go to
`StartAsync(progress, combinedAll, joins)`, the Preview → Go hand-off: the pipeline skips its first
steps and, since every episode has joins, its own AI step, so *AI Grouping On Go* plays no part.

- Why not the pipeline's AI step: there a failed episode falls back to the rules, and the season batch
  wants it skipped and grouped by the model on a later run.
- One run over every episode keeps *Remove duplicate lines* spanning the season, as in the GUI. Its
  table spans every ready episode, the ones the AI step then drops included: the lines left active are
  the ones the model grouped and the cache key hashes, so re-inactivating after the drop would change
  them.
- `ClaudeCliProvider.UsageLimit` stays set for the rest of the process. From then on an episode whose
  answer is not cached (`AiGrouper.Estimate(...).Cached`) is skipped without a request; a cached one
  still runs. An episode during which the limit was hit is skipped even when only some chunks failed:
  such a result is not cached, so the next run redoes it whole. Chunks that failed for another reason
  keep their episode, grouped by the rules there, with a warning.

### The run's result

`SubsProcessor.StartAsync` returns a `PipelineResult`. `Status` is `Cancelled` only when the reporter's
`Cancel` or token is set; any other stop is `Failed`, a step that returned null or false included (it
throws a private `StepStoppedException`). `Message` is one line, "<step label> failed: <detail>" for a
failure. `CardsPerEpisode` is by index into the `Files` arrays (map it through `EpisodeNumber`;
context-only lines are not cards), and `ImportFile` is set once the TSV writer is open. The GUI ignores
the result and still learns the outcome from its dialogs ([open-items.md](open-items.md)); `go` turns it
into the table and the exit code, and deletes the `ImportFile` of a `Failed` or `Cancelled` run, whose
cards lack media.

`go` is split so that `season` can run it: `ApplyCardOptions` (`--grouping`, `--deck`), `PlanGo` (the
dry run's set-up, cache column and checks, as a `GoPlan`) and `RunGoAsync(list, yes, stderr, token)`
(set-up, checks, pre-pass, pipeline, TSV clean-up), which returns a `GoRun` instead of printing:
`Cells()` gives each episode's AI, Status and Cards, `TsvLine(exit)` the last line. `go` prints the
`GoRun` as its own table; `season` puts the cells into its own.

### `season`: extract, retime, then `go`

`SeasonCommand.RunAsync` takes the folder's videos (`EpisodeList.SeasonVideos`, cut at *Episode End #*
as `go --season` cuts them) and makes one `SeasonEpisode` row per video. Each stage fills its part of
the row (`Pick`, `Extraction`, `En`, `Jp`, `Retime`; a dry run's `EnPlan`/`RetimePlan`; go's `Cards`),
one episode after another, and the table is a list of `SeasonColumn(Header, Cell)` that `Columns`
chooses from the options, so a full run, `--only`, `--dry-run` and a cancel print from the same rows.

- **Extract** (`MkvTracks`, `MkvExtract`): `mkvmerge -J`, then `Pick` (`--track` takes any text track,
  Japanese or forced too: that is how a mistagged track is used). A container that is not Matroska is
  refused at the pick: mkvmerge lists an MP4's tracks, mkvextract then fails. Before extracting, the
  episode's other `s2s/<name>.en.*` files are deleted (extractions of another track; `go` skips an
  episode with two). A non-empty output is kept without running mkvextract, so a changed `--track`
  whose track has the same format needs `--force`, which deletes the episode's own `.en` file too.
  Without a pick nothing is deleted. A failed extraction reaches the retime as `FoundFile.Missing`.
- **Retime** (`RetimeStage`): `FindJpFiles` is given every video of the folder, so a name that is also
  a longer video's JP file is that video's (`Movie.Extended.srt` is not `Movie`'s). `RetimeAsync` keeps
  an output written after both its EN and JP files (`WouldKeep`, unless `--force`) without subsretimer,
  deleting any other `.ja` file of the episode; otherwise it deletes the episode's `.ja` files and
  report, runs the launcher, and deletes a `.ja` file left by a run that did not save or was
  cancelled. So after the stage an episode has a `.ja` file exactly when its outcome is `Ready`, with
  one exception: no JP or no EN file (none, or two) deletes nothing, so an editor fix survives a
  lookup problem; such an episode is not `Ready`. subsretimer is resolved once
  (`RetimeOptions.FromSettings`); a missing one fails only the episodes that need a retime.
- **Cards**: `ForGo` builds the `EpisodeList` from the retime outcomes, not from what `s2s` holds: a
  `Ready` episode gets its retime as Subs1 and its EN file as Subs2, any other is a skipped `Episode`
  whose reason is the Retime cell, and the numbers are `go --season`'s (`EpisodeList.OfSeason`).
  Reading `s2s` again would hand `go` a `.ja` file the table calls skipped. Then `RunGoAsync`. `--only
  go` takes `EpisodeList.FromSeason`, exactly as `go --season`.
- `SetUpCards` (before any work) refuses audio from audio files, applies `--grouping`/`--deck`, and
  sets Subs2 to UTF-8, the encoding mkvextract writes every text track in, with one warning when the
  project says otherwise.
- `--dry-run` must not call `RetimeAsync`, which deletes. `PlanExtractAsync` and `PlanRetime` only read
  (`WouldKeep` is shared with the run); the AI column asks the cache only where the retime would be
  kept, since the other episodes' Subs1 does not exist yet.
- MKVToolNix is required before any work when the run extracts. `go`'s checks run only after the
  extraction and the retime ([open-items.md](open-items.md)); a check that fails there prints the
  table (`not made` where `go` would have made cards) and exits 1. A cancel prints the table with the
  rows not done `cancelled` and exits 130.

## Snippets

A snippet is a card built from several consecutive subtitle lines. There is no snippet type:
`InfoCombined` was extended in place (`Parts`, `GroupNote`, `Segments()`), because a wrapper type would
have had to be threaded through ~20 call sites, the preview, `WorkerVars` and the project format. When
`Parts` is set, `Subs1`/`Subs2` hold the *merged view* (text joined with `<br>`, start of the first kept
part, end of the last), so every older consumer keeps working unchanged.

### The join vector is the grouping

Every producer (rules, AI, manual editor) and every consumer (materialisation, validation files, eval
scoring) speaks one representation: a vector of booleans, "this line is attached to the next one".
Two forms exist and mixing them up is the classic bug here:

- **Stored form** (`WorkerVars.Joins`, the editor): one slot per line of the episode, *including
  inactive lines*. `joins[i]` means line *i* is attached to the next **kept** line; slots of inactive
  lines are ignored. It is stored this way so that activating or omitting a line in the preview never
  invalidates the vector.
- **Kept projection**: *n − 1* booleans over the *n* kept (active) lines only. This is what the
  validation file stores, what the AI prompt's line index `i` refers to, and what the scorer compares.
  `SnippetGrouping` converts between the two.

"Neighbour" always means adjacent among **kept** lines. Two kept lines with an omitted line between
them are neighbours and can be joined.

### Limits and `Repair`

Trimmed duration = sum of the kept parts' durations + `min(gap, GapKeepMs)` for each internal gap + the
grouped-card pad. It must not exceed `MaxSnippetSeconds` (15). `SnippetGrouping.Repair` enforces this
for every producer by splitting a violator at its largest internal gap until it fits. The editor uses
the same arithmetic to refuse an attach. Do not add a second implementation of this formula; call
`SnippetGrouping` / `SnippetLimits.FromSettings`.

### Omitted lines inside a snippet

User decision, and the most test-protected behaviour in the feature: if kept lines A and C are joined
and omitted line B lies between them, **the card is built as if B did not exist**. B contributes no
text (in either subtitle field), no duration, and its dialogue is cut out of the audio clip, the video
clip and the animated snapshot.

How that is represented:

- `InfoCombined.Parts` still contains B, solely so `SnippetGrouping.Flatten` can restore the full line
  list (the validation export round-trips). Everything else reads `KeptParts`. If you iterate `Parts`
  to build text, times or media, you have reintroduced the bug.
- B's range is **subtracted** from the cut list rather than left to gap shortening, so it disappears
  for any `GapKeepMs` and also with gap removal off.
- Where B overlaps a kept line in time, the kept dialogue wins; only the part of B outside every kept
  line is cut.
- With gap removal **off**, the card is the whole A..C span minus B's own range; the silences around B
  stay.
- An omitted line that is not inside any snippet passes through as an inactive single, as it always did.

### Two kinds of multi-range cards

1. **Snippets** (`Parts` set), from the grouping step.
2. **Sentence-joined lines**: the older feature that merges subs1 lines ending in `,`, `、`, `→` into
   one `InfoLine` during "Combine subs". It was *not* moved onto `Parts`; instead it records the
   original ranges in `InfoLine.Segments` (shifted together with the line). So it is a plain single
   line everywhere except in the media workers, which still apply gap removal to it.

This means **gap removal also affects snippet mode Off**: it is on by default, and a sentence-joined
line with a pause longer than `GapKeepMs` gets that pause shortened. `Mode = Off` is the identity only
with respect to grouping.

## Media cutting

### One range list per grouped card

`SnippetMedia.RangesFor(card)` returns the single list of time ranges a grouped card is cut from, and
audio, video, animated snapshot and the preview's play button all use it, so they cover the same
dialogue. It returns `null` for a plain single line, which each worker then cuts its own old way.

Padding is the subtle part:

- A **grouped** card is padded with the **audio-clip pad** for all three media types (when audio clips
  are enabled with padding, else no pad). That is the pad the duration budget is measured with, and
  one list needs one pad.
- A **single-line** card keeps each worker's own pad: audio pad, video pad, none for animated snapshots.
- `WorkerVideo` first transcodes the whole episode range to a temp file, so it pads that range with
  `max(video pad, grouped pad)` to make sure every card's ranges lie inside it.
- A list that ends up as one range takes the worker's plain (non-gap) path.

### How each worker cuts a multi-range card

| Media | Method | Accuracy |
| --- | --- | --- |
| Audio | One ffmpeg call: input seek, then `aselect='between(t,…)+…',asetpts=N/SR/TB` (`UtilsGapRemoval`, both the demux-to-WAV path and the direct path) | Sample-accurate |
| Video | Stream-copy each range from the temp transcode, join with the `concat` demuxer (`UtilsVideo.cutVideoSegments`) | Keyframe-snapped; length only approximates the audio |
| Animated snapshot | `select=…,setpts=N/FRAME_RATE/TB` in front of `fps`/`crop`/`scale` | Exact (it re-encodes anyway) |
| Snapshot | Midpoint of the **first kept part** for snippets; whole-span midpoint for plain and sentence-joined lines | The whole-span midpoint of a snippet could land in a removed gap |

The video inaccuracy is a user decision: speed over frame accuracy, no re-encode per card. Tests check
video length only loosely on purpose; do not "fix" it with a re-encode.

All range/filter strings are built by pure functions (`UtilsGapRemoval`, `UtilsAnimatedSnapshot`) so
they can be unit-tested without ffmpeg; keep new ffmpeg argument logic in that style, culture-invariant.

### Animated snapshots

Own output type with own settings (`Settings.AnimatedSnapshots`), not a video-clip format. One 0–100
quality knob on libwebp's scale; for avif it maps to `crf = 63 − q·63/100` with fast presets. Encoder
choice comes from a cached `ffmpeg -encoders` probe (`libwebp_anim` over `libwebp`: both animate on
ffmpeg 5.1 but the former was 3.5× smaller; `libaom-av1` over `libsvtav1`). With no encoder the
main-window checkbox is disabled with a hint by `MainWindow.CheckExternalTools`. Seek is `-ss`/`-t`
**before** `-i` on the source video (fast seek). Crop uses ffmpeg expressions so no resolution probe is
needed.

## Settings, preferences, files

| Thing | Class | Stored in | Scope |
| --- | --- | --- | --- |
| Project settings | `Settings.Instance` (+ `SnippetSettings`, `AnimatedSnapshots`, …) | `.s2s.json` via `ProjectIO` | Per project; `Settings.RestoreFrom`/`Reset` |
| Preferences | `PreferencesData` behind `ConstantSettings` | `preferences.json` via `PrefIO` (`~/.config/subs2srs`, `%APPDATA%\subs2srs`) | Global; includes API keys, AI knobs, key bindings, `ToolsDir`, `ValidationDir` |
| AI answer cache | `AiGroupingCache` | `%LOCALAPPDATA%/subs2srs/ai-cache` or the *AI Cache Directory* preference | No expiry or pruning |
| Validation files | `GroupingValidationFile` | `*.grouping.json` in `ValidationDir` | Labelled groupings for the eval |
| Logs | `Logger` | `ConstantSettings.LogDir`: `~/.local/share/subs2srs/Logs`, `%LOCALAPPDATA%\subs2srs\Logs` | Full ffmpeg command lines and GLib warnings land here |

.NET resolves `%APPDATA%`/`%LOCALAPPDATA%` through the shell API, **not** the environment variables, so
they cannot be redirected by setting env vars in a test or script. That is why `ConstantSettings.LogDir`
has an internal setter and the test scopes write their own preferences file.

External tools (ffmpeg, ffprobe, ffplay, mkvtoolnix, mp3gain, subsretimer) are resolved on every use
by `ConstantSettings.ResolveTool` (the *Tools Directory* preference, then a PATHEXT-aware PATH search;
for mkvmerge, mkvextract and mkvinfo then, on Windows, `%ProgramFiles%\MKVToolNix` and
`%ProgramFiles(x86)%\MKVToolNix`, where the installer puts them without adding them to PATH) and
started through `UtilsCommon.makeToolStartInfo` (UTF-8 pipes, no window, `-nostdin` for ffmpeg). Start
new tool processes the same way; `UseShellExecute` and bare tool names broke on Windows.

The async runs of mkvmerge, mkvextract and subsretimer share `UtilsCommon.RunToolAsync`: both pipes
read concurrently (a chatty stderr cannot deadlock the child), and a cancel kills the tool with its
children, waits up to 5 s (so a partial file can be deleted on Windows) and throws
`OperationCanceledException`. Each class has a `RunnerOverride` for tests. MKVToolNix's tools are
started through `MkvTracks.StartInfo`, which off Windows sets `LC_ALL=C.UTF-8` when the inherited
locale is not UTF-8: under the C locale mkvmerge 82 drops a non-ASCII file argument and cuts its `-J`
output at the first non-ASCII character. Both get `--output-charset UTF-8` and full paths (a name
starting with `@` is read as an option file). mkvextract writes its messages to stdout, and exit code 1
from either means warnings, not failure.

### The `subsretimer` launcher

Subtitle re-timing is not part of the pipeline. It lives in a separate repository,
[jhhr/subsretimer](https://github.com/jhhr/subsretimer) (a GTK4/.NET port of the Subs Re-Timer that
shipped with the original subs2srs), and subs2srs only *launches* it: `SubsRetimerLauncher` (arguments,
process, result) and `DialogSubsRetimer` (the Tools-tab window). There is no build dependency and no
shared code; the two repositories are coupled only by the command-line contract below. Keep it that
way: the decision against a submodule was taken to avoid locking both repositories to one GirCore
version and to keep AUR packaging simple.

The contract subs2srs relies on (documented on the tool's side in its README; a change there is a
breaking change here):

| Rule | subs2srs side |
| --- | --- |
| `subsretimer [options] [REFERENCE] [TARGET]`; REFERENCE is the file already timed to the video, TARGET the one to re-time | The dialog's *Reference* radio decides which of Subs1/Subs2 is which; the other side is the one that gets replaced afterwards |
| Paths are passed after `--` | So a file name starting with `-` cannot be read as an option. `EditorCommand` (below) prints no `--`, so it is given full paths |
| `--ref-encoding` / `--target-encoding` take subs2srs **short** encoding names (`utf-8`, `shift_jis`, …) | `InfoEncoding.longToShort` on the main window's dropdown values |
| `--auto` runs the alignment and saves without a window; without it the tool opens its editor | The *Auto-align* checkbox. Without it the editor opens and the run ends when it closes: exit 0 when it saved (each path printed as it is written), 2 when it did not |
| `--auto` never overwrites `<TARGET>_retimed.<ext>` unless `--output` names it | A second run on the same pair therefore fails; the error text says so |
| With `--print-output`, **stdout carries only saved paths**, one per line, flushed on each save; everything else goes to stderr | `ParseResult` takes the last non-empty stdout line as the saved file |
| Exit `0` = at least one file saved, `2` = nothing saved (editor closed, below `--min-match`, or a file had no timed lines), `1` = error on stderr; anything else is treated as an error | `Result.Saved` / `NothingSaved` / `Failed`. Exit 0 with an empty stdout counts as nothing saved |
| `--min-match F` (with `--auto`): save only when the retimed target covers at least that share (0 to 1) of the reference's lines, else exit 2 | `Request.MinMatch`, passed only when set, in the invariant culture (the tool refuses `0,85`) |
| `--report PATH` (with `--auto`): a JSON report, `version` 1, of a run that exits 0 or 2: `exitCode`, `saved`, `segments`, `referenceCoverage.share`, `reason` (null, `below min-match`, `no timed lines`) | `Request.ReportPath`; `RetimeReport.Read` takes only those fields and gives null for a missing file, not JSON, another version or a field of the wrong type |
| Redirected stdout and stderr are UTF-8 without a byte-order mark | The start info comes from `makeToolStartInfo`, so both pipes are read as UTF-8 and a Japanese saved path comes back intact on Windows |

The tool's README ("Contract for other programs") has the details.

`SubsRetimerLauncher.EditorCommand(exe, request)` is the command line a user types to open the editor
on a pair (the season batch prints one for each pair subsretimer did not save): no `--auto`,
`--min-match`, `--report` or `--print-output`; `--ref-encoding` unless UTF-8, `--target-encoding`,
`--output` (where the editor's Save writes), then the two paths without `--`. It is quoted for
PowerShell on Windows (double quotes; a backtick before `` ` ``, `$` and the double-quote characters;
`& ` before a quoted exe) and for a POSIX shell elsewhere (single quotes); plain ASCII words stay bare.

The executable is found like every other tool, `ConstantSettings.ResolveToolOrName("subsretimer")`
(*Tools Directory*, then PATH, `.exe` on Windows), and `SubsRetimerLauncher.IsAvailable` gates the
button. `RunAsync` goes through `UtilsCommon.RunToolAsync` (above) and never throws for a tool failure,
only `OperationCanceledException` for a cancel, after killing the tool, so a cancel cannot be taken for
exit 2. The dialog `await`s it on the GTK thread; its nested main loop keeps pumping meanwhile. Closing
the dialog cancels an auto-align; an open editor is not killed (that would lose unsaved work there),
the dialog only stops waiting for it (`WaitAsync`). On success the dialog asks, through
`UtilsMsg.showConfirm`, whether to put the saved path into the re-timed side's field. The tool's stderr
is what the user sees on failure, so the tool must keep its messages user-readable.

Two preferences, `SubsRetimerReferenceIsSubs2` and `SubsRetimerAuto`, remember the dialog's last
choices. They are written by the dialog itself, not by `DialogPref` (see
[open-items.md](open-items.md)).
