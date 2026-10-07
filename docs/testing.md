# Testing

`tests.md` at the repository root lists every test file and what it covers; add a row when you add a
file. This page is how to run, write and trust the tests.

## Projects and commands

| Project | Needs | Command |
| --- | --- | --- |
| `subs2srs.Tests` | ffmpeg + ffprobe on PATH (else the e2e tests skip) | `dotnet test subs2srs.Tests/subs2srs.Tests.csproj` |
| `subs2srs.UiTests` | GTK 4 and a display | `GSK_RENDERER=cairo dotnet test subs2srs.UiTests/subs2srs.UiTests.csproj` |

- One class: `--filter "FullyQualifiedName~ClassName"`. CI and `make test` run `-c Release`; run Release
  yourself before wrapping up a feature.
- Windows: `export PATH="/c/msys64/ucrt64/bin:$PATH"` first for the UI tests (MSYS2 UCRT64
  `mingw-w64-ucrt-x86_64-gtk4`). Headless Linux: `xvfb-run -a -s "-screen 0 1280x1024x24" …` with
  `GDK_BACKEND=x11`.
- `GSK_RENDERER=cairo` avoids GPU-dependent blank windows and makes screenshots work everywhere.
- `SUBS2SRS_UITEST_ARTIFACTS=<dir>` → a PNG per window (`Screenshot.TrySave`). Look at them after UI
  changes; CI uploads them as artifacts.

### Opt-in tests (normally skipped)

| Env var | Runs | Cost |
| --- | --- | --- |
| `SUBS2SRS_AI_LIVE_MODEL=<model>` + the provider's key var | `AiLiveTests`: one real API call | API money |
| `SUBS2SRS_AI_CLI_LIVE=1` (Claude Code installed and signed in) | `ClaudeCliLiveTests`: one real `claude -p` run and a flag check. `SUBS2SRS_AI_CLI_LIVE_DUMP=<file>` keeps the raw output | Subscription usage |
| hold-out files + cache under `Fixtures/eval/` | `EvalRegressionTests` gate | none |
| `SUBSRETIMER_EXE=<path to a built subsretimer>` | `SubsRetimerLauncherTests.RealTool_*` and `RetimeStageTests.RealTool_*`: the real tool on generated files (a Japanese folder, a two-episode season), checking the stdout/exit-code contract, `--min-match`, `--report` and the editor command end to end | none (local process, no network) |

`SUBSRETIMER_EXE` is the one to set after changing `SubsRetimerLauncher`, `RetimeStage` or the tool's
command line: the other tests run a scripted subsretimer. It points at the `subsretimer` binary from the
tool's `make build` (`SubsRetimer/bin/Release/net10.0/publish/`). Unset, those tests skip (CI runs them
skipped); set to a name that is not a file, they fail. A build older than the tool's `--min-match` and
`--report` (2026-10-04) fails them with "Unknown option": keep the build current, or unset the variable
in a shell that inherits a stale one.

Do not set these unless the user asks. When a live run reveals a real response shape, save it as a
fixture under `subs2srs.Tests/Fixtures/ai/` (that is how `claude-cli-ok.json` came to be, and it caught
a token-accounting bug that hand-written samples had hidden).

Skip attributes: `[RequiresFfmpegFact]`/`[RequiresFfmpegTheory]`, `[RequiresFfmpegEncoderFact(format)]`
(also needs a webp/avif encoder in that ffmpeg), `[RequiresEnvFact(var)]`, `[RequiresEvalFixturesFact]`,
`[RequiresMkvToolnixFact]` (mkvmerge and mkvextract found as the app finds them; CI installs MKVToolNix
on both jobs), `[RequiresPosixShellFact]` (`/bin/sh`, so not on Windows). A skipped test is reported as
skipped, not passed: check the count before claiming media code is tested.

## State isolation

The app keeps its state in process-wide singletons (`Settings.Instance`, `ConstantSettings`, `Logger`,
several static caches and hooks), so:

- Both assemblies disable xUnit parallelisation (`AssemblyInfo.cs`). Do not re-enable it.
- Every test that touches settings, preferences, files or the pipeline creates a **`TestScope`**
  (`using var scope = new TestScope();`). It makes a temp dir, redirects `preferences.json`, the log dir
  and the AI cache dir into it, turns file logging off, forces media parallelism to 1, and on dispose
  resets `Settings.Instance` and deletes the dir with retries (Windows keeps handles open briefly).
- `%APPDATA%`/`%LOCALAPPDATA%` cannot be redirected through env vars in .NET; the scopes set the
  internal setters instead. `MainWindow` calls `PrefIO.read()` on construction, so `UiTestScope` writes
  the preferences **file**; setting `ConstantSettings` properties in a UI test before opening the main
  window is overwritten.
- Static hooks must be reset in `Dispose`: `ChatProviders.Override` (`FakeChatProvider.Install()` does
  both), `ClaudeCliProvider.RunnerOverride` / `HelpOverride` / `ExecutableOverride`,
  `ClaudeCli.ResetProbeCache()`, the usage-limit state (`ClaudeCliProvider.Reset()`),
  `UtilsAnimatedSnapshot.OverrideAvailableEncoders`, `EvalFixtures.Override`, the process runners of
  `MkvTracks`, `MkvExtract` and `SubsRetimerLauncher` (`RunnerOverride`),
  `ConstantSettings.MkvToolNixDirsOverride`, and `PATH` when a test changes it.

## Harness (`subs2srs.Tests/Harness`)

- **`TestMedia`**: generates (once, cached in the temp dir) a 10 s mp4 with ffmpeg, UTF-8 and Shift-JIS
  srt files, and the dialogue set used by snippet tests (`DialogueLines`, `WriteDialogueSrt`,
  `WriteDialogueTranslationSrt`). No media is checked into the repository.
- **`RecordingMsgHooks`**: captures what would have been error/info dialogs (`UtilsMsg`). Assert on it;
  an unexpected error dialog should fail a test.
- **`NullProgressReporter`** / **`CancellingProgressReporter`**: drive the pipeline without UI, and test
  cancellation.
- **`FakeChatProvider`**: canned answers (`JoinAll`, `JoinPairs`), a `Failure` mode, and a record of the
  requests, so tests can assert on what was *sent* (e.g. that no `t2` leaves the machine).
- **`ScriptedHandler`** (in `AiProviderTests.cs`): a fake `HttpMessageHandler`;
  `Reply(status, body, retryAfter, headers)` plus a `beforeReply` hook for mid-flight scenarios.
  **`FakeClock`** and the `Loop` fixture (in `AiRateLimitTests.cs`) make every wait instantaneous and
  recorded. `AiProviderTests.FastRetry` = fake clock + a private `RateLimitTracker`. Provider tests never
  really sleep; a test that takes seconds is wrong.
- `ClaudeCliProvider` takes an injected process runner; tests script `CliProcessResult`s. Nothing in
  `dotnet test` may start `claude`.

## Testing media output (e2e)

The e2e tests run the real pipeline (`SubsProcessor.StartAsync` with a `NullProgressReporter`) into the
scope's temp dir and then inspect files:

- Durations via ffprobe (`UtilsCommon.getFFprobeStdout`): audio is sample-accurate, so assert tightly;
  **video is keyframe-snapped, assert loosely** (the existing tests accept 1.5–3.4 s for 2.2 s of audio).
- Animated webp: ffmpeg 5.1 cannot demux it, so tests count `ANMF` chunks in the RIFF container;
  `ffprobe -count_frames` is used for avif only.
- TSV: read the file and assert on fields. For snippet behaviour the priority is proving that an omitted
  line appears **nowhere** (neither text field, no media range, not in the duration budget).
- Output paths in tests include a space and non-ASCII characters on purpose; keep that when adding cases.

Prefer a pure argument-builder function plus a unit test over a new e2e test; e2e tests cost seconds each.

Explicit episode numbers are tested here too: `SubsProcessorE2ETests.EpisodeNumbers_InTagsSequenceMarkersAndEveryMediaName`
sets `Settings.EpisodeNumbers` (and `EpisodeCountForNames`) by hand and checks the TSV tags, sequence
markers and every media name, and that nothing else lands in `.media`;
`TimeShiftRule_IsChosenByTheExplicitEpisodeNumber` checks the per-episode time-shift rule. The
`PipelineResult` tests sit beside them (`CompletedRun_*`, `FailingWorker_*`, `CancelWhileAWorkerRuns_*`,
`UnwritableOutputDir_*`).

## Command-line tests (`subs2srs-cli`)

| Class | Covers |
| --- | --- |
| `EpisodeListTests` | The episode list: `EpisodeList.ForSeason` as a pure function of a folder listing, the folder read, the patterns paired |
| `ProjectFilesTests` | `ProjectFiles.Resolve` giving the arrays the GUI's `SaveSettings` gives (`MainWindowFlowTests` pins those with the same `PatternSet`) |
| `GoChecksTests` | Each check of `GoChecks` alone |
| `CliTests` | The command: usage, dry runs, the checks under the table, real runs, exit codes, the built console once |
| `CliAiPrePassTests` | `go` on a project grouped by AI: the pre-pass, the usage limit, the AI column |
| `MkvTracksTests`, `MkvExtractTests` | `season`'s EN track: `mkvmerge -J` parsed and the pick, the extraction, MKVToolNix's Windows folders; scripted runners, and the real tools on generated mkv files |
| `SubsRetimerLauncherTests`, `RetimeStageTests` | The launcher (arguments, start info, report, editor command for both shells, a real child killed on cancel) and the retime stage (the JP lookup, keep-if-newer, the deletes, the Retime column) |
| `CliSeasonTests` | `season`: options, the table without mkvmerge, `--only`, and a generated season through every stage with a scripted subsretimer |

- **In-process.** `CliTests.Run(args)` calls `CliRunner.RunAsync(args, stdout, stderr)` with two
  `StringWriter`s inside a `TestScope`. `CliTests.SaveProject` saves the scope's settings as a
  `.s2s.json`, then resets `Settings.Instance`, so only the file carries them, as for a user. Most
  pass `--no-prefs`, which leaves the preferences in memory (what the test set in
  `ConstantSettings`) as they are; `--prefs <file>` reads a file the test wrote.
- `UtilsMsg` writes to `Console.Error`, not to the writer `RunAsync` was given, so its lines (the
  pipeline's error, a warning's question) are not in the captured stderr. Assert on the CLI's own
  lines, the table and the files.
- **Fake ffmpeg.** The checks need ffmpeg, so a dry-run test that is about the list calls
  `CliTests.FakeFfmpeg`: an empty file named `ffmpeg` in the *Tools Directory*. The ffmpeg check only
  looks for the file; the audio-stream check, the one place a dry run starts it, then finds no stream.
  Tests that really run the pipeline use the real ffmpeg (`[RequiresFfmpegFact]`).
- **Season folders.** `CliTests.MakeSeason` creates `Show S1 [Grp] 日本/` with an `s2s/` subfolder and
  the empty files it is given (bracketed and Japanese names); a dry run never reads them. A real run
  needs real files: `RealEpisode` copies the test video as `<name>.mkv` and writes the harness dialogue
  as `<name>.ja.srt` and `<name>.en.srt`. `CliAiPrePassTests.Episode` starts every line with `E<n> ` so
  each episode asks the model something else (the cache key is the content) and a card shows whose text
  it carries; all but one of its runs make the TSV only, so an empty `.mkv` will do.
- Padding needs at least ten videos to be tested: with three, the run's count and the season's give
  the same single digit (the padding test adds empty videos 4 to 10).
- `CliAiPrePassTests` never starts `claude`: the model is `FakeChatProvider`, or the `claude` transport
  with `ClaudeCliProvider.RunnerOverride` scripted to answer or to hit the usage limit. Its `Dispose`
  resets the overrides, the probe cache and the usage limit (`ClaudeCliProvider.Reset()`), which
  otherwise stays set for the rest of the test process.
- **The built console**, once: `CliTests.RealProcess_JapaneseNames_ReachARedirectedCallerAsUtf8` runs
  `dotnet subs2srs-cli.dll` (the project reference copies it next to the tests) under a Latin-1
  `LC_ALL`, with the fake ffmpeg's folder on `PATH`, and checks that stdout is UTF-8 without a BOM.
- **Generated mkv files.** `MkvTracksTests.Mux(output, inputs)` runs the real mkvmerge on subtitles
  from `MkvTracksTests.Srt`/`Ass`; it passes `--command-line-charset UTF-8` and a UTF-8 `LC_ALL` only
  off Windows (Windows' mkvmerge has no such option). Tests that use it are `[RequiresMkvToolnixFact]`;
  the rest of the MKVToolNix code runs on a scripted `RunnerOverride`, with an empty `mkvmerge` in
  the *Tools Directory* for the lookup to find. "MKVToolNix not found" needs `PATH` and
  `MkvToolNixDirsOverride` pointed at empty folders too. `Fixtures/mkv` holds `mkvmerge -J` output
  recorded with mkvmerge 82 (no paths) and one hand-written file with image tracks, marked so; its
  README lists the tracks.
- **A scripted subsretimer.** `SubsRetimerLauncher.RunnerOverride` gets the start info and returns
  what the tool would: `CliSeasonTests.ScriptRetimer` copies the JP file to `--output` (so `go` reads
  real text) and a report from `Fixtures/retime` to `--report`. Those reports were written by the real
  subsretimer (the folder's README says how); `RetimeReport` is tested on them. An empty `subsretimer` in the
  *Tools Directory* makes the lookup find it.
- **File times, not sleeps.** Keep-if-newer compares last-write times, so a test that runs `season`
  again sets the files back (`CliSeasonTests.SetBack`: every file an hour ago, the retimes ten minutes
  later) instead of sleeping: an extraction and its retime can fall in one clock tick of the file
  system, which made a re-run "to retime" once.
- **A real child process** for the cancel: `SubsRetimerLauncherTests` kills a sleeping `sh`
  (`powershell` on Windows) through the shared runner, and runs the POSIX editor command through
  `/bin/sh` (`[RequiresPosixShellFact]`) to check the arguments arrive intact. The PowerShell command
  is checked as a string only.

## GTK UI tests (`subs2srs.UiTests`)

- `GtkFixture` owns the one GTK thread for the whole run (`[Collection(GtkCollection.Name)]` on every
  class). The application is created `NonUnique` and held, so windows can come and go.
- **All widget access goes through `gtk.RunOnGtk(...)` / `RunOnGtkAsync(...)`.** Touching a widget from
  the xUnit thread crashes natively or, worse, works sometimes.
- Never `Thread.Sleep`. Use `Pump.WaitUntilAsync(condition)`, `Pump.SettleAsync(window)` (mapped + two
  frame-clock ticks + idle) or `Pump.IdleAsync()`.
- `UiTestScope` extends `TestScope`: opens windows (`OpenMainWindowAsync`, `OpenAsync`), tracks them,
  and on dispose fails the test if a window leaked or an unexpected error dialog was recorded (call
  `ExpectErrors()` when errors are the point). Leaks are detected against `Gtk.Window.GetToplevels()`
  because `Widget.OnDestroy` does not fire for windows closed with `Close()`.
- The app exposes a small `internal` surface for tests (`InternalsVisibleTo`): `MainWindow` widgets,
  `GoAsync`, `SaveSettings`, `CheckExternalTools`; `DialogPref.AcceptAndClose`/`SetPropertyValue`/
  `GetPropertyValue`; and similar on the other dialogs. Add to it rather than reflecting over privates
  or simulating pointer input.
- Key handling is tested by calling the handler directly (`DialogPreview.OnListKeyPressed(keyval,
  modifiers)`), not by synthesising GDK events; see `PreviewGroupingScrollTests` for the pattern,
  including scroll-position and focus assertions. That does not prove the event *reaches* the handler
  (see the capture-phase note in [gtk-and-windows.md](gtk-and-windows.md)); check that by hand.
