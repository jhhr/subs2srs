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

Do not set these unless the user asks. When a live run reveals a real response shape, save it as a
fixture under `subs2srs.Tests/Fixtures/ai/` (that is how `claude-cli-ok.json` came to be, and it caught
a token-accounting bug that hand-written samples had hidden).

Skip attributes: `[RequiresFfmpegFact]`/`[RequiresFfmpegTheory]`, `[RequiresFfmpegEncoderFact(format)]`
(also needs a webp/avif encoder in that ffmpeg), `[RequiresEnvFact(var)]`, `[RequiresEvalFixturesFact]`.
A skipped test is reported as skipped, not passed: check the count before claiming media code is tested.

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
  `UtilsAnimatedSnapshot.OverrideAvailableEncoders`, `EvalFixtures.Override`.

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
