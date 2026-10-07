# Open items

What is unmeasured, unverified or deferred. None of this is visible in the code, which looks equally
confident everywhere. Remove an item when it is settled and, if a decision came out of it, record the
decision in the topic doc.

## Never exercised against the real thing

- **The Anthropic, OpenAI and Gemini adapters have never made a real call.** Request shapes and error
  classification come from the official docs (2026-09-14) and from fixtures written by hand. Run
  `AiLiveTests` once per provider when a key is available, and replace hand-written fixtures with
  recorded responses. Known doubts: whether Gemini 3.x still accepts `responseMimeType` +
  `responseJsonSchema` (its guide shows a `responseFormat` field the adapter does not send); OpenAI uses
  Chat Completions although OpenAI recommends the Responses API for new work.
- **Response-driven rate limiting** has only met scripted responses. Header names and formats are from
  docs and from the user's Python implementation.
- **Anthropic streaming** fixed cut-off chunks in theory; it has not been confirmed on a real episode.
  OpenAI and Gemini still cap output at 16,384 tokens without streaming and may cut off long chunks.
- **`claude` CLI**: one real request succeeded. Unknown: how an upstream 429/5xx surfaces in print mode,
  and the real wording of a usage-limit message (detection is a regex). Not yet done: one whole episode
  through `terminal-claude-sonnet-5` compared, for grouping and wall clock, with `claude-sonnet-5`.
- **Animated webp/avif in Anki**: nobody has imported a generated deck to check that the animation plays
  and the file sizes are sane.
- Linux column drag-resize and a clean-machine Windows run: see
  [gtk-and-windows.md](gtk-and-windows.md).
- **`subsretimer` auto-align has only seen synthetic fixtures** (irregular generated dialogue with
  15 s / 90 s cuts, jitter and sound cues). No real EN/JP closed-caption pair has been through it; the
  switch penalty (2.5 lines of overlap) and the 28 s gap threshold inherited from the original tool are
  untested defaults. Run `subsretimer --auto EN.srt JP.ass` on a real episode and read the segment
  summary on stderr. The launcher has never been run on Windows.
- **`subs2srs-cli go` and `season` have not made a real season on Windows.** Their tests run on the
  Windows CI job in-process, on generated media (mkv files muxed there by the real MKVToolNix), a
  scripted subsretimer and a fake model, and the bundled `subs2srs-cli.exe` has passed the smoke
  test; `season` has run by hand on generated seasons on Linux only. No real season (real mkv files,
  real JP files, the real subsretimer on Windows, `claude` grouping) has been through either. The
  Windows-only paths of `season` (the PowerShell quoting of the editor command, names compared
  ignoring case, `%ProgramFiles%\MKVToolNix`) are checked by CI's tests, never by hand. The user's
  first step is one real episode by hand (phase 0 of the season-batch plan in the subsretimer
  repository), which is also to set `--min-match`: it has no default.

## Small known faults

- **The GUI says "Action cancelled." for a failed step** that stopped by returning false (the audio
  worker's own failures): `SubsProcessor.StartAsync`'s catch shows that dialog for a
  `StepStoppedException` as for a cancel, and `MainWindow.GoAsync` ignores the `PipelineResult`, which
  says `Failed` with the step and the reason. A one-line change in that catch if wanted.
- **"Processing completed in 0,02 minutes."**: `StartAsync` formats the time with the current culture,
  so the INFO line and `PipelineResult.Message` show a decimal comma on such systems.
- **`UtilsVideo.validateAudioStreamConsistency` numbers episodes by position** (`i + 1`), not by
  `Settings.EpisodeNumber`: in a season run with episode 2 skipped, the audio-stream warning calls
  episode 3 "2". With no video holding the stream it names episode -1 as the reference.
- **`Environment.GetFolderPath` returns "" when `~/.config` or `~/.local/share` does not exist**
  (Linux), so the preferences file, the log folder, the AI cache and the `claude` working folder become
  relative paths under the current directory (seen: `subs2srs/Logs/log-*.txt` in a project folder).
  The GUI's launcher and the `subs2srs-cli` wrapper create both folders first; running
  `/usr/lib/subs2srs/subs2srs-cli` or a development build directly does not. The fix in the app would
  be `SpecialFolderOption.DoNotVerify`.
- **Every `subs2srs-cli` run leaves an empty `log-*.txt`** in the log folder: `CliRunner.RunAsync`
  touches `Logger.Instance`, whose constructor creates the file, before it turns file logging off.
  The folder keeps only the newest 10 logs, so ten command-line runs push out every log of the GUI.
- **`season` runs `go`'s checks only after extracting and retiming**: a missing deck name, ffmpeg or
  `claude` is reported after that work (which is kept for the next run). Checking up front what does
  not depend on the episodes (all but the audio streams) would be friendlier.
- **`season` keeps by name and file time.** A retime is kept when it was written after both its EN
  and JP files, so a JP file replaced by a copy that kept an older time (Explorer keeps it) leaves the
  old retime in place. An extraction is kept when its file exists, so after a changed `--track` whose
  track has the same format the old track stays. `--force` redoes both; the help text says so.
- **CI's `push` trigger names the branch `default`** (`branches: [default]` in `ci.yml`), which matches
  only a branch of that name, not the default branch `main`; pushes run nothing and only pull
  requests and manual runs are checked. `branches: [main]` would do what the commit says.
- **`subs2srs-cli.exe` has no `app.manifest`**, unlike `subs2srs.exe` (whose manifest sets long paths
  and the UTF-8 code page). Whether a long path breaks it on Windows is untested. It has no icon
  either; when `assets/subs2srs.ico` lands (deferred below), only the app's csproj picks it up.

## Flaky tests seen once

- The Windows CI UI job hung once in `PreviewGroupingTests.Preview_ProposesEditsAndExportsGrouping`
  (b912688: the GTK thread wedged, 11 timeouts after it) and passed on the next commit; it has not
  recurred. One local Release run failed `PreviewGroupingScrollTests` with "leaked MainWindow" (which
  hides the test body's own failure) and did not recur in 14 runs. Cause unknown for both; if either
  comes back, keep the log.

## Defaults that are guesses

No labelled data existed when these were chosen. The eval console exists to settle them
([ai-grouping.md](ai-grouping.md)); it needs ≥ 6 labelled episodes over 2+ shows, some in `holdout/`.

| Default | Value | Concern |
| --- | --- | --- |
| Rules grouper | join gap ≤ 1500 ms **and** a cue (line ends in one of `?？…→、,`, or the actor changes) | The original design had cues optional and no commas; with commas the rules join many ordinary sentence continuations |
| Gap removal | on by default, `GapKeepMs` 500, also affects sentence-joined lines in snippet mode Off | Changes output for users who never enabled snippets |
| `MaxSnippetSeconds` / chunk target | 15 s / 200 lines | From the plan, unmeasured |
| System prompt wording | `PromptVersion` 3 | v3 (exchanges kept together, no thin cards) was written from one episode's preview; never scored, and it may over-merge long conversations |
| Default model | `claude-sonnet-5` | Chosen for cost, not measured quality |
| Effort | Anthropic `medium`, OpenAI `low` | Asymmetric for no recorded reason |
| Token estimate | chars ÷ 2.5 in, 20 + 4 × lines out | Japanese is denser than English; never compared with an invoice |
| Animated snapshot quality | 20 (libwebp scale); avif `crf = 63 − q·63/100`, `-cpu-used 8` / `-preset 10` | The avif mapping and speed presets are unmeasured |
| Regression gate template | `claude-sonnet-5`, `minBoundaryF1` 0.9 | Placeholders until real fixtures exist |
| Concurrency | 32 requests (auto), 4 CLI processes | The CLI number comes from one 4-core measurement in another project |

## Decisions the user may still want to reverse

- With gap removal **off**, a grouped card containing an omitted line is the whole span minus that line's
  own range (surrounding silences stay).
- Where an omitted line overlaps a kept line in time, the overlap is kept (kept dialogue wins).
- Grouped cards use the audio-clip pad for video and animated snapshots too.

## Deferred features

- **`subsretimer` integration**, in order of value:
  1. Wildcard patterns in Subs1/Subs2 are refused by the dialog. The planned batch mode resolves both
     patterns with `UtilsSubs.getSubsFiles`, pairs by index, runs `--auto` per pair behind a progress
     bar and offers to rewrite the pattern to the `_retimed` files.
  2. Small code follow-ups from the docs review: the two `SubsRetimer*` preferences are not in
     `DialogPref` or `Logger.writeSettingsToLog` (the "five places" rule); and `DialogSubsRetimer`
     has no `subs2srs.UiTests` coverage (it was verified once by a throwaway Xvfb harness:
     auto-align success handing the file back, failure re-enabling Run, wildcard validation).
- **`season` follow-ups** (the season-batch plan's "later, optional"): `season --fix`, opening the
  editor on each pair the last run could not retime, one after another, then making those cards; the
  GUI's Extract dialog on `MkvTracks` (track names and flags, errors not dropped); natural sort in
  `getNonHiddenFiles`, so `ep2` sorts before `ep10` (it changes the episode order of existing projects
  with unpadded names: say so in the CHANGELOG).

- **Batch API mode** (half price on all three providers). Deferred until a real run shows the
  per-episode cost. Nothing prepares for it: it means a second request path per adapter and a polling
  loop in the runner. Cached-input and batch prices are noted in the comment above `AiPricing`.
- **AI answer cache** has no expiry, size cap or pruning.
- **Aligning mismatched subs1/subs2 lines** is out of scope for grouping (the model sees subs1 only).
- **Moving sentence-join onto `Parts`**: it records `InfoLine.Segments` instead, which was enough for
  gap removal; two multi-range representations remain ([architecture.md](architecture.md)).
- Windows: `assets/subs2srs.ico` (the csproj picks it up when it exists) and an Inno Setup installer.
- Arming the eval regression gate (`subs2srs.Tests/Fixtures/eval/README.md`).

## Dated maintenance

- **2027-01-01**: Gemini 3.6 / 3.7 / 3.8 Flash prices double; refresh `AiPricing` from the three pricing
  pages (URLs above the table). Refresh it whenever a provider releases or retires models; an unlisted
  model shows "no price" and makes the eval's `--max-cost` refuse to run.
