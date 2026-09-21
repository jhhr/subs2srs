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

External tools (ffmpeg, ffprobe, ffplay, mkvtoolnix, mp3gain) are resolved on every use by
`ConstantSettings.ResolveTool` (the *Tools Directory* preference, then a PATHEXT-aware PATH search) and
started through `UtilsCommon.makeToolStartInfo` (UTF-8 pipes, no window, `-nostdin` for ffmpeg). Start
new tool processes the same way; `UseShellExecute` and bare tool names broke on Windows.

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
| Paths are passed after `--` | So a file name starting with `-` cannot be read as an option |
| `--ref-encoding` / `--target-encoding` take subs2srs **short** encoding names (`utf-8`, `shift_jis`, …) | `InfoEncoding.longToShort` on the main window's dropdown values |
| `--auto` runs the alignment and saves without a window; without it the tool opens its editor (exit 1 with a message when it cannot open a display) | The *Auto-align* checkbox |
| `--auto` never overwrites `<TARGET>_retimed.<ext>` unless `--output` names it | A second run on the same pair therefore fails; the error text says so |
| With `--print-output`, **stdout carries only saved paths**, one per line, flushed on each save; everything else goes to stderr | `ParseResult` takes the last non-empty stdout line as the saved file |
| Exit `0` = at least one file saved, `2` = nothing saved (editor closed, or a file had no timed lines), `1` = error on stderr; anything else is treated as an error | `Result.Saved` / `NothingSaved` / `Failed`. Exit 0 with an empty stdout counts as nothing saved |

The executable is found like every other tool, `ConstantSettings.ResolveToolOrName("subsretimer")`
(*Tools Directory*, then PATH, `.exe` on Windows), and `SubsRetimerLauncher.IsAvailable` gates the
button. The run is `await`ed on the GTK thread (`Process.WaitForExitAsync` plus `ReadToEndAsync` on both
pipes, so a chatty stderr cannot deadlock the child); the dialog's nested main loop keeps pumping
meanwhile. On success the dialog asks, through `UtilsMsg.showConfirm`, whether to put the saved path
into the re-timed side's field. The tool's stderr is what the user sees on failure, so the tool must keep
its messages user-readable.

Two preferences, `SubsRetimerReferenceIsSubs2` and `SubsRetimerAuto`, remember the dialog's last
choices. They are written by the dialog itself, not by `DialogPref` (see
[open-items.md](open-items.md)).
