# AGENTS.md

Instructions for AI coding agents (Claude Code, GitHub Copilot, others) working in this repository.
`CLAUDE.md` and `.github/copilot-instructions.md` only point here; edit this file, not those.

## What this is

A fork of the GTK4 / .NET 10 port of subs2srs (Anki cards from video + subtitles). On top of upstream
this fork adds: Windows 10/11 support with a bundled GTK runtime, GTK UI tests, multi-line dialogue
**snippets** (rules or AI grouping, with a preview editor), **gap removal** inside a card's media,
**animated snapshots** (webp/avif), an AI provider layer (Anthropic, OpenAI, Gemini, and Claude through
the `claude` CLI) and a grouping **evaluation console**.

- `origin` = `jhhr/subs2srs` (this fork), `upstream` = `Ajatt-Tools/subs2srs`. Branches and PRs target
  the fork: `gh pr create --repo jhhr/subs2srs` (plain `gh pr create` may pick the upstream parent).
- Use the `gh` CLI for anything GitHub.

## Layout

| Path | What |
| --- | --- |
| `subs2srs/` | The app. Flat folder, one namespace. `Worker*` = pipeline steps, `Utils*` = ffmpeg/tool helpers, `Dialog*`/`MainWindow` = GTK UI, `Ai*`/`*Provider`/`ClaudeCli*` = AI grouping, `Snippet*`/`Grouping*` = snippet model, editor, validation files, scorer |
| `subs2srs.Tests/` | xUnit unit + card-generation e2e tests (real ffmpeg, no GTK, no network). `Harness/` holds the shared fixtures |
| `subs2srs.UiTests/` | xUnit GTK tests: one GTK thread fixture, real windows, optional screenshots |
| `subs2srs.Eval/` | Console that scores groupings against labelled validation files. The only place allowed to call a real AI provider outside the app |
| `dist/windows/` | `bundle-gtk.ps1` (MSYS2 GTK → publish dir), `smoke.ps1` |
| `docs/` | Agent/developer docs, see the index below |
| `tests.md` | One row per test file. `CHANGELOG.md` = user-visible changes |

There is no `.sln`. Every command names a project file.

## Build and test

```sh
dotnet build subs2srs/subs2srs.csproj
dotnet test subs2srs.Tests/subs2srs.Tests.csproj                       # unit + e2e, needs ffmpeg on PATH
dotnet test subs2srs.Tests/subs2srs.Tests.csproj --filter "FullyQualifiedName~SnippetMediaTests"
GSK_RENDERER=cairo dotnet test subs2srs.UiTests/subs2srs.UiTests.csproj # GTK tests, needs GTK 4 + a display
dotnet run --project subs2srs.Eval -- --set <dir> --rules              # eval console (make eval ARGS=...)
```

- **Windows**: GTK comes from MSYS2 UCRT64. Before running the app or the UI tests, put it on PATH:
  `export PATH="/c/msys64/ucrt64/bin:$PATH"` (Git Bash) or prepend `C:\msys64\ucrt64\bin` (PowerShell).
  No other GTK variables are needed for development runs.
- **Headless Linux**: `xvfb-run -a make test-ui`.
- Set `SUBS2SRS_UITEST_ARTIFACTS=<dir>` to get a PNG of every window the UI tests open. After a UI
  change, do this and *look at* the screenshot; layout bugs do not fail tests.
- `make test`, `make test-ui`, `make eval`, `make publish-windows` wrap the above (Release).
  On Windows without PowerShell 7: `make publish-windows PWSH=powershell`.
- Tests that need something missing **skip**, they do not fail: no ffmpeg, no webp/avif encoder,
  env-gated live tests, the eval regression gate without fixtures. Read the skip count: a run where
  the ffmpeg tests skipped has not tested media generation.

Details, harness and test hooks: [docs/testing.md](docs/testing.md).

## Definition of done

1. `dotnet build` clean; `subs2srs.Tests` green. Before a commit that closes a feature, also `-c Release`
   (CI runs Release).
2. UI touched (any `Dialog*`, `MainWindow`, a preference label, a UI string) → `subs2srs.UiTests` green.
3. `git diff --stat` shows only the lines you meant to change (see line endings below).
4. User-visible change → a `CHANGELOG.md` entry under *Unreleased*. New test file → a row in `tests.md`.
5. New NuGet package or project → regenerate `packages.lock.json` (CI restores with `--locked-mode`),
   and add a new project to `.github/workflows/ci.yml` (restore per project) and the `Makefile`.

## Hard rules

- **`dotnet test` never touches the network and never starts `claude`.** Provider code is tested with
  `ScriptedHandler` / `FakeChatProvider` / an injected process runner. Real calls exist only in the
  env-gated `AiLiveTests` and `ClaudeCliLiveTests` and in `subs2srs.Eval`. Do not run those without
  being asked: they cost the user money or subscription usage.
- **API keys are never logged**, never written to fixtures, never put in test output.
- **The `claude` child process must never see `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` or
  `ANTHROPIC_BASE_URL`, and must never get `--bare`.** Either one makes the CLI bill the API instead
  of the subscription, silently. See [docs/ai-grouping.md](docs/ai-grouping.md).
- **Anything sent to the model changes → bump `AiGroupingPrompt.PromptVersion`.** It is part of the
  cache key; without the bump users get stale cached answers. A bump also makes the eval regression
  gate skip until its fixture cache is regenerated.
- **Limits are enforced by `SnippetGrouping.Repair`, for every producer.** Never trust a grouping from
  the model, the rules or the editor to respect `MaxSnippetSeconds`; never throw on a bad model answer
  (repair, log, fall back to the rules for that chunk).
- Do not add AI SDK packages. Providers are raw `HttpClient` adapters behind `IChatProvider`.
- Stay on GirCore **0.7.0** (the Windows bundle depends on its `libgtk-4-1.dll` naming) and on managed
  GirCore APIs (no P/Invoke into GTK).

## Gotchas that have cost real time

- **Mixed line endings.** 27 files under `subs2srs/` are CRLF in the index and must stay CRLF; the rest
  are LF. List them with `git ls-files --eol | grep i/crlf` (the older core: `Info*` except `InfoLine`,
  `Logger`, `ObjectCloner`, `PropertyBag`, `SubsParser*`, `SubsProcessor`, `SubtitleCreator/*`,
  `UtilsAssembly/Audio/Lang/Mkv/Msg/Snapshot/Subs/Video`, `WorkerSubs`, `WorkerVars`). `sed -i` in Git
  Bash strips CR from the whole file; editor tools sometimes write CRLF into LF files. A 3-line change
  becomes a 2,000-line diff. After editing run `git diff --stat`; repair with `unix2dos` / `dos2unix`.
  In scripted edits, match `\r?\n` and reuse the matched ending.
- **Decimal-comma cultures.** The dev machine formats `1.5` as `1,5`. Every number that goes into an
  ffmpeg argument, a log/UI string a test asserts on, or JSON must use
  `CultureInfo.InvariantCulture` / `FormattableString.Invariant`. Parsing ffmpeg output likewise.
- **`Settings.Instance` and `ConstantSettings` are process-wide singletons.** Tests run with
  parallelisation disabled and must go through `TestScope` / `UiTestScope`, which redirect preferences,
  the log dir and the AI cache to temp dirs and reset the singleton on dispose. Static test hooks
  (`ChatProviders.Override`, `ClaudeCliProvider.RunnerOverride`, the encoder probe override, …) must be
  cleared in `Dispose`.
- **Adding a preference touches five places**: `PrefDefaults`, `PreferencesData`, `ConstantSettings`,
  `DialogPref` (`BuildPropTable` + `SavePreferences`), `Logger.writeSettingsToLog`. Changing a saved
  *default* additionally needs `PrefIO.UpgradeDefaults`, or existing users keep the old value forever.
  Removing one: old JSON keys must still load (they are ignored).
- **Adding a per-project setting**: the settings class, `Settings.RestoreFrom` and `Reset`, a `??=`
  default in `ProjectIO` so old `.s2s.json` files load, `Logger`, and a round-trip test.
- **GirCore 0.7 has no XML docs and hides nested signal-args types.** Do not guess GTK API names from
  the C docs; reflect over the assemblies (see [docs/gtk-and-windows.md](docs/gtk-and-windows.md)).
- **Shell scripts with code in heredocs break** (apostrophes end the quoting, `\n` gets un-escaped into
  C# string literals). Write edit scripts to a file and run the file.
- ffmpeg **5.1** is the floor that has been tested: it cannot demux animated webp (tests count `ANMF`
  RIFF chunks instead of using `ffprobe -count_frames`).

## Docs index

Read the one that matches the task; they are written to be read on their own.

| Doc | Read it when you touch |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | The pipeline, `InfoCombined`, snippets / join vectors, omitted lines, gap removal, media workers, the preview ↔ Go hand-off, settings vs preferences |
| [docs/ai-grouping.md](docs/ai-grouping.md) | Providers, rate limiting and retries, prompt / cache, the `claude` CLI transport and its half-documented contract, pricing, the eval console and regression gate |
| [docs/testing.md](docs/testing.md) | Any test: harness classes, fakes and hooks, skip attributes, env vars, UI test mechanics, how to test media output |
| [docs/gtk-and-windows.md](docs/gtk-and-windows.md) | GTK widgets or GirCore APIs, external tool launching, Windows packaging scripts, CI workflows |
| [docs/open-items.md](docs/open-items.md) | Picking up next work: defaults that were never measured, provider details never exercised live, deferred features, manual follow-ups |

Keep these docs to what the code cannot tell you: models that span files, external contracts, reasons
for a non-obvious choice, traps. When a decision changes, update the doc in the same commit. Do not
record bug histories or per-commit status here; that is what `git log` and `CHANGELOG.md` are for.
