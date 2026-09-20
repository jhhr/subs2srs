# Tests

## Overview

| Project | File | Framework | What it tests |
|---------|------|-----------|---------------|
| `subs2srs.Tests` | `UtilsSubsTests.cs` | xUnit | Time formatting, padding, overlap, roundtrip |
| `subs2srs.Tests` | `UtilsNameTests.cs` | xUnit | Token replacement, zero-padding, escape chars |
| `subs2srs.Tests` | `PrefIOTests.cs` | xUnit | JSON round-trip, defaults, migration, special chars |
| `subs2srs.Tests` | `ProjectIOTests.cs` | xUnit | `.s2s.json` save/load, corruption handling, transient fields |
| `subs2srs.Tests` | `SettingsSnapshotTests.cs` | xUnit | Deep copy, isolation, restore, defaults |
| `subs2srs.Tests` | `TimeShiftRuleTests.cs` | xUnit | Cascading lookup, edge cases, negative shifts |
| `subs2srs.Tests` | `SubsParserTests.cs` | xUnit | SRT/ASS/LRC parsing, error handling, resource disposal |
| `subs2srs.Tests` | `SnippetGroupingTests.cs` | xUnit | Join vectors, trimmed durations, limit repair, materialisation, rule-based grouper |
| `subs2srs.Tests` | `GroupingEditorTests.cs` | xUnit | Attach/detach actions, limit validation, undo/redo, next-disagreement |
| `subs2srs.Tests` | `UtilsGapRemovalTests.cs` | xUnit | ffmpeg `aselect`/`select` filter and concat-list builders (culture-safe) |
| `subs2srs.Tests` | `GroupingValidationFileTests.cs` | xUnit | Validation JSON: build from lines, round trip, omission settings |
| `subs2srs.Tests` | `KeyBindingTests.cs` | xUnit | GTK accelerator string parsing and matching |
| `subs2srs.Tests` | `SnippetSettingsTests.cs` | xUnit | Snippet settings defaults, project-file round trip, old projects |
| `subs2srs.Tests` | `SnippetE2ETests.cs` | xUnit (ffmpeg) | Rules grouping + gap removal through the whole pipeline; clip durations checked with ffprobe; preview joins override the rules; video concat; an omitted line inside a snippet appears in no text field and no clip, with gap removal on and off |
| `subs2srs.Tests` | `SnippetMediaTests.cs` | xUnit | The one range list a grouped card's audio, video and animated snapshot are cut from: kept lines only, omitted lines subtracted in both gap modes, kept dialogue wins over an overlapping omitted line, shared pad, plain-line and sentence-join fallbacks, settings plumbing |
| `subs2srs.Tests` | `AnimatedSnapshotTests.cs` | xUnit | Animated snapshot settings (defaults, project round trip), `ffmpeg -encoders` parsing and encoder choice, `-vf`/codec argument builders (culture-safe) |
| `subs2srs.Tests` | `AnimatedSnapshotE2ETests.cs` | xUnit (ffmpeg + encoder) | Animated webp/avif through the whole pipeline: files, frame counts (WebP RIFF chunks, ffprobe for avif), gap removal shortens the animation, TSV column, Off, missing encoder reported |
| `subs2srs.UiTests` | `AnimatedSnapshotUiTests.cs` | xUnit (GTK) | Snapshots-tab controls follow the encoder probe and the checkbox; Go writes animated webp |
| `subs2srs.UiTests` | `PreviewGroupingTests.cs` | xUnit (GTK) | Preview proposes a grouping, buttons/keys/drop edit it, validation export |
| `subs2srs.UiTests` | `PreviewGroupingScrollTests.cs` | xUnit (GTK) | Grouping edits in a 150-line Preview keep the scroll position and selection; attach on an attached side detaches that side only; buttons relabel; every default key (incl. Ctrl+Up/Down and plain Backspace) and Undo |
| `subs2srs.Tests` | `AiStreamingTests.cs` | xUnit | Server-sent-events reader; Anthropic streamed answers assembled (text, cumulative usage, model), `max_tokens` and thinking-only stops, a mid-stream overload retried like a 529 and a mid-stream invalid request terminal, output limit from the model's cap, 600 s request timeout default |
| `subs2srs.Tests` | `AiProviderTests.cs` | xUnit | Provider layer without network: prefix dispatch, key lookup with env override, retry loop basics (`Retry-After`, backoff, give-up, timeouts, cancel), request shape and recorded-response fixtures (`Fixtures/ai/*.json`) for Anthropic, OpenAI and Gemini, error text, effort fallback, schema cleanup |
| `subs2srs.Tests` | `AiRateLimitTests.cs` | xUnit | Response-driven rate limiting (port of `test_api_client.py`): `Retry-After`/RFC 3339/Go/Google duration parsers with junk rejection, the classify table per provider (terminal quotas, hints, soonest/longest bucket), the proactive hold from a success's headers, the shared cooldown tracker on a fake clock (never shortened, stale success does not clear), the retry loop through the adapters (waits, holds, give-up above the maximum, backoff on expired hints, 5xx/timeouts never hold the model, mid-flight cooldown survives, cancellation) |
| `subs2srs.Tests` | `AiGroupingTests.cs` | xUnit | Gap-split chunker, prompt content, output schema, answer parser repairs, cache key/round trip, bulk runner (concurrency cap, all items at once, failure isolation, cancel), `AiGrouper` end to end with `FakeChatProvider` (notes, cache, force refresh, rules fallback, cost estimate) |
| `subs2srs.Tests` | `AiGroupingE2ETests.cs` | xUnit (ffmpeg) | AI mode through the whole pipeline with the fake provider: "AI grouping" step with *AI Grouping On Go*, rules fallback without it or on provider failure, preview joins skip the step |
| `subs2srs.Tests` | `ClaudeCliTests.cs` | xUnit | The `terminal-` helpers without starting a process: prefix and model mapping, Claude-only model check, the child environment (no API key, no thinking, no telemetry), finding the executable (configured path, `.exe`, npm shim → native binary, shim alone → none), the `claude --help` flag probe (cached per executable, nothing optional assumed when it fails), the argument list (flags of the probed build only, schema round trip, long system prompt moved into the prompt or into a file, never `--bare`), the classify table (structured output, text only, upstream 429/529/5xx retried, unknown model and max-structured-output-retries terminal, usage-limit wording, non-JSON output) a recorded real result object (cached prompt tokens counted as input, model ID from the usage breakdown) and the last-JSON-object rule |
| `subs2srs.Tests` | `ClaudeCliProviderTests.cs` | xUnit | `ClaudeCliProvider` on a scripted runner and a fake clock: structured output and its tokens, the last JSON object of a text answer, what the process is told (arguments, stdin prompt, working directory, hidden API key, timeout), retry on an upstream rate limit and a timeout, give-up after the retries, terminal failures not retried, a usage limit failing every later request without running anything, no executable, cancellation before and during a run, and the process cap |
| `subs2srs.Tests` | `ClaudeCliLiveTests.cs` | xUnit (live, skipped) | One grouping through the real `claude` process and a check of this build’s flags; runs only with `SUBS2SRS_AI_CLI_LIVE=1` and Claude Code signed in. Records the arguments, exit code and raw output; run once on 2026-09-16 and its response kept as `Fixtures/ai/claude-cli-ok.json` |
| `subs2srs.Tests` | `AiLiveTests.cs` | xUnit (live, skipped) | One real provider call; runs only with `SUBS2SRS_AI_LIVE_MODEL=<model>` and the provider's key in the environment |
| `subs2srs.UiTests` | `PreviewAiGroupingTests.cs` | xUnit (GTK) | Preview in AI mode with the fake provider: pass on open, note tooltips, Regroup (AI), validation proposal block, provider error shown with rules fallback |
| `subs2srs.UiTests` | `SnippetsOptionsUiTests.cs` | xUnit (GTK) | Snippets options page loads and saves the AI mode, model, lines per request and extra instructions |
| `subs2srs.Tests` | `GroupingEvalTests.cs` | xUnit | Grouping scorer (boundary P/R/F1, snippet exact / over- / under-merged, micro-averaged sums, diff) and evaluation-set discovery with the `holdout/` convention |
| `subs2srs.Tests` | `EvalRunnerTests.cs` | xUnit | The `subs2srs.Eval` console: rules and model runs with the fake provider (scores, cache reuse, `--refresh`, `--estimate`, `--max-cost`, provider failure, `--compare`), argument parsing, and the built console launched once on a validation file made from the test dialogue |
| `subs2srs.Tests` | `EvalRegressionTests.cs` | xUnit (fixtures, skipped) | Gate on the hold-out set: cached model answers in `Fixtures/eval` must reach the F1 floor of `regression.json`; the gate itself is verified on a synthetic fixture directory |

`[RequiresFfmpegFact]` skips a test when ffmpeg is missing; `[RequiresFfmpegEncoderFact(format)]` also when the ffmpeg found has no encoder for that animated snapshot format.
`[RequiresEnvFact(var)]` runs only with that environment variable set (the live provider test). `[RequiresEvalFixturesFact]` runs only when `Fixtures/eval` holds hold-out files and cached answers for the current prompt version.

## Running

```bash
# All tests
make test

# Individual suite (via dotnet)
dotnet test subs2srs.Tests/subs2srs.Tests.csproj --filter "FullyQualifiedName~UtilsSubsTests"

# GTK UI tests (needs GTK 4 and a display; see docs/testing.md)
make test-ui
```

## How they work

See [docs/testing.md](docs/testing.md): state isolation (`TestScope`, sequential runs), the harness and
fakes, opt-in live tests, how media output is checked, and the GTK test mechanics. In short: the suites
marked *ffmpeg* run the real ffmpeg on generated test media in temp directories, nothing in `dotnet test`
touches the network or the user's real preferences, and tests whose prerequisites are missing skip.
