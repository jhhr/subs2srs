# AI grouping: providers, rate limiting, the `claude` CLI, evaluation

What an agent needs before touching `Ai*.cs`, `*Provider.cs`, `ClaudeCli*.cs`, `GroupingEval.cs` or
`subs2srs.Eval/`. Much of this is knowledge about *external* behaviour (provider APIs, the `claude`
CLI) that was learned from docs and one live run, and cannot be re-derived from the code.

## Flow

`AiGrouper.GroupAsync`: cache lookup → `AiChunker` splits the episode → `AiBulkRunner` sends every chunk
at once → `IChatProvider.CompleteJsonAsync` (schema-constrained JSON) → `AiAnswerParser` → a failed chunk
falls back to the rules → `SnippetGrouping.Repair` → notes go onto `InfoCombined.GroupNote` → cache write.
The app (preview and Go) and the eval console share this exact path; keep it that way, so an eval score
describes what the app does.

Invariants:

- **The model sees subs1 only**: `i`, `s`, `e`, `actor`, `t` per line. Nothing of subs2 is sent (user
  decision: fewer tokens, less subtitle text leaves the machine). The translation follows the grouping
  because a snippet merges both tracks with the same parts. This is enforced in one place,
  `AiGroupingPrompt.BuildUser`. The validation file's `t2` field is labelling context for humans only.
- **Line index `i` is the position among kept lines** (the kept projection, see
  [architecture.md](architecture.md)); omitted lines are not sent at all. The model lists multi-line
  snippets only (`{snippets:[{first,last,note}]}`), which keeps the output small.
- **A bad answer never breaks a run.** The parser clips ranges to the chunk, drops overlaps, logs what it
  repaired and never throws; `Repair` then enforces the limits.
- **Chunks** break at gaps ≥ `MaxSnippetSeconds` (no snippet can span such a gap, so nothing is lost),
  choosing the boundary closest to the target of 200 lines. `0` = whole episode in one request.
- **Cache key** = SHA-256 over the prompt version, model string, limits, chunk target, extra instructions
  and the exact whole-episode user content. Hashing the model input (rather than the file) means the
  omission settings are covered for free. A result with any failed chunk is **not cached**. *Regroup (AI)*
  forces a refresh. The model string is used verbatim, so `terminal-claude-sonnet-5` and
  `claude-sonnet-5` cache separately (on purpose: different transports enforce the schema differently).
- **`PromptVersion`** (currently 2) must be bumped when the system prompt, the user payload or the
  schema changes. Extra instructions are *not* a version: they are part of the key already.
- Failure surface: no key, unknown model prefix, or every chunk failing → `ProviderException`, shown in
  the preview's status line or logged on Go. Partial failure → rules for those chunks, silently except
  for the log.

## Providers

Model IDs are plain strings; only the prefix → provider dispatch is hard-coded (`ChatProviders`):
`terminal-` → `claude-cli` (checked **first**), `claude`/`anthropic`, `gpt`/`o1`/`o3`/`o4`/`chatgpt`,
`gemini`. Keys come from `preferences.json` with `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` /
`GEMINI_API_KEY` overriding.

Request details, as aligned with the official docs on 2026-09-14. **None of the three HTTP adapters has
ever been exercised against the real API**; the fixtures in `subs2srs.Tests/Fixtures/ai/` were written
from documentation, except `claude-cli-ok.json`, which is a recorded real response.

| | Anthropic | OpenAI | Gemini |
| --- | --- | --- | --- |
| Endpoint | Messages, version `2023-06-01` | **Chat Completions** (not Responses), one path for old and new models | `generateContent`, key in the `x-goog-api-key` header |
| Structured output | `output_config.format` json_schema | `response_format` json_schema, `strict: true` | `responseMimeType` + `responseJsonSchema`. The deprecated `responseSchema` + `additionalProperties` cleanup survives behind `UseLegacyResponseSchema` |
| Effort | `output_config.effort: medium` | `reasoning_effort: low` | Left at the model default (2.5 uses `thinkingBudget`, 3.x `thinkingLevel`) |
| Output cap | **Streams**; `max_tokens` = min(32,000, the model's documented cap) | 16,384, not streamed | 16,384 (covers thinking), not streamed |

- **No `temperature` anywhere**: current Anthropic and OpenAI reasoning models reject it.
- **One-shot option drop**: after a 400 whose message names `effort` / `reasoning_effort` / `thinking`,
  the request is repeated once without that option. This replaces per-model capability tables; keep it
  that way.
- **Why Anthropic streams**: `max_tokens` caps thinking and text *together*; at 8,192 real episodes had
  chunks cut off (and a cut-off chunk is never cached), and Anthropic advises streaming for a large
  `max_tokens`. The stream path reads headers first (so rate-limit classification still works), still
  accepts a plain JSON body, and maps a mid-stream `error` event to the HTTP status that error type has
  outside a stream, so an `overloaded_error` retries like a 529. OpenAI and Gemini were left alone only
  because their streaming formats and per-model caps could not be verified from docs.
- Request timeout default is 600 s (what the official SDKs use); a saved old default of 120 is upgraded
  by `PrefIO.UpgradeDefaults`.

### Rate limiting is response-driven

User decision: "as fast and as concurrent as possible, limited only by what the provider answers". It is
a port of the user's Python `api_client.py` (`jhhr/anki_addons`, `japanese_note_ai_ops/async_api_ops/`),
and `AiRateLimitTests` mirrors that project's `test_api_client.py`. There is **no RPM pacer and no token
budgeting**; do not add one back.

- `AiBulkRunner` starts every chunk at once, up to *AI Max Concurrent Requests* (0 = auto = 32, kept
  below the shared `HttpClient`'s `MaxConnectionsPerServer` of 64).
- Each adapter's `Classify(status, headers, body)` returns OK / RETRY(delay?) / FAIL:
  - Anthropic: retry 429, 500–504, 529; delay = `retry-after`, else the **soonest**
    `anthropic-ratelimit-*-reset` (RFC 3339).
  - OpenAI: 429 with `insufficient_quota` (type or code), `credit_balance_exhausted` or
    `billing_hard_limit_exceeded` is **terminal**; any other 429 retries with `Retry-After`, else the
    **longest** `x-ratelimit-reset-*` (Go durations like `6m0s`, `20ms`). Beware suffix rules: an earlier
    `*_limit_exceeded` = terminal rule swallowed the ordinary `rate_limit_exceeded`.
  - Gemini: 429 whose `QuotaFailure` has a `quotaId` containing `PerDay` is terminal; other 429 and
    500–504 retry with `RetryInfo.retryDelay` (`"34s"`). Gemini sends no limit headers.
  - Everything else, including 408 and 409, is terminal.
- `RateLimitTracker`: one shared cooldown per `provider:model`. Only a 429/529 or a proactive hold sets
  it; every request waits it out before sending; it is **never shortened**; it is cleared early only by
  a success whose request was **sent after** the cooldown went up (a response already in flight proves
  nothing).
- Proactive hold: a 200 whose headers show a bucket with `remaining <= 0` installs the **latest** such
  reset. Clamped to the max retry wait (the Python does not clamp; an uncapped hold would stall a run
  silently for longer than a rejection is allowed to).
- Timeouts, connection errors and 5xx back off **per request** and never touch the shared cooldown.
- No hint (a hint of 0 is no hint) → `min(60 s, 2^attempt) + U(0, 1 s)`. Max 5 retries = 6 sends. A hint
  above *AI Max Retry Wait Seconds* (120) gives up at once and installs **no** cooldown. No jitter on
  the shared wake-up (limits are per-minute buckets; it would only make tests non-deterministic).
- `AiRetryLoop` / `AiAttempt<T>` is the transport-independent skeleton of all this, shared by
  `HttpChatProvider` and `ClaudeCliProvider`, so a `terminal-` model paces exactly like an API model.
  The clock and the tracker are injectable (`RetryPolicy.UtcNow`, `.Delay`, `.Tracker`); tests must
  inject a private tracker so parallel classes never share `RateLimitTracker.Shared`.

### Pricing and the cost estimate

`AiPricing` (in `AiGrouper.cs`) is a hand-maintained table of standard USD-per-million prices from the
three official pricing pages; the source URLs and fetch date are in the comment above it, along with
long-context, cached and batch prices that are *not* in code. Only models listed on a page and still
served are priced. Matching is longest-prefix where the prefix must end at `-` or the end of the ID, so
a dated snapshot matches its family but an unlisted sibling gets **no price** rather than a wrong one.
Gemini 3.6–3.8 Flash prices double on 2027-01-01.

Tokens are estimated as characters ÷ 2.5 for input and 20 + 4 × lines for output, with no tokenizer.
Never validated against an invoice.

## Claude through the `claude` CLI (`terminal-` models)

`terminal-claude-sonnet-5` runs each chunk as one `claude -p` process on the user's **Claude
subscription**, which is far cheaper per token than the API; that is the entire point of the feature.
Only Claude IDs or the aliases `opus`/`sonnet`/`haiku`/`fable` are accepted after the prefix. Port of the
user's `terminal_client.py` (`jhhr/anki_addons`, branch `jnaio-word_array_generator`).

Rules that protect the point of the feature:

- The child's environment has `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_BASE_URL`
  **removed**. With a key present the CLI silently bills the API.
- **Never `--bare`**, even though it is the documented hermetic mode: its own help says auth is then
  strictly `ANTHROPIC_API_KEY`/`apiKeyHelper` and "OAuth and keychain are never read".
- Isolation comes instead from `--safe-mode --tools "" --no-session-persistence`, an **empty working
  directory** under the app data dir (so no `CLAUDE.md`, settings or git state leak into the prompt),
  and `DISABLE_TELEMETRY=1`, `DISABLE_ERROR_REPORTING=1`, `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`.
- Thinking is off two ways: `--effort low` (documented) and `MAX_THINKING_TOKENS=0` (undocumented, but
  proven in the Python addon to halve call time). Keep both.

The CLI contract is only half documented, which shapes the code:

- **Flags are probed from `claude --help`, once per executable, cached.** The online CLI reference and
  the installed build (2.1.268) disagreed *in both directions* (e.g. `--system-prompt-file` documented
  but absent; hardening flags present but undocumented), and the CLI **rejects unknown flags**. When the
  probe fails, only `-p`, `--model`, `--output-format`, `--json-schema`, `--system-prompt` are assumed
  and every hardening flag is left off: a flag the build lacks fails every request, while a missing
  `--safe-mode` only loses isolation.
- The system prompt goes inline; above 8,000 characters it moves into the stdin prompt under a
  `# Instructions` heading (or `--system-prompt-file` where the probe found it). The Windows command
  line caps at 32,767 characters.
- The result object (`--output-format json`): documented fields are `result`, `session_id`,
  `total_cost_usd`, `usage`, `structured_output`. `is_error`, `subtype`, `api_error_status` are
  undocumented but were all present in the recorded real run. `stop_reason` was `"tool_use"` (structured
  output is served as a tool call).
- **Token accounting trap**: the CLI serves nearly the whole prompt from its prompt cache, so
  `usage.input_tokens` was **4** of 3,376 real input tokens. `cache_read_input_tokens` and
  `cache_creation_input_tokens` must be added. There is **no top-level `model` field**; the ID is the
  key of `modelUsage`.
- Classification is **defensive**: every field optional; anything unrecognised is a *retryable* failure
  with the full stdout/stderr logged, so an unknown shape costs a retry, never a wrong answer. Still
  unknown: how an upstream 429/5xx surfaces in print mode (non-zero exit, `is_error`, both, or a silent
  internal retry).
- `subtype == "error_max_structured_output_retries"` is terminal for that chunk (the CLI already retried
  the schema internally).
- A **subscription usage limit** is detected by regex over the error text (`ClaudeCli` usage-limit
  pattern; the docs describe only the wording, e.g. "You've hit your session limit"). It is terminal for
  the **whole run**: a static state fails every later request immediately, so the remaining chunks fall
  back to the rules at once instead of each stalling. If a differently worded limit ever shows up, widen
  that regex.
- Concurrency has its own cap, *Claude CLI Max Concurrent Processes* (default 4), a static semaphore on
  top of the runner's cap: each process is a few hundred MB and CPU-heavy at startup (~1.4 s of the
  4.3 s measured), so the CPU is the limit, not the provider.
- On npm installs, `claude`, `claude.cmd`, `claude.ps1` are shims; the real binary is
  `node_modules/@anthropic-ai/claude-code/bin/claude.exe`. `ClaudeCli.Find` resolves a shim to it and
  treats a shim alone as "not found". The *Claude CLI Path* preference overrides the search.
- Cost reads "on the Claude subscription (no API cost)" rather than "price unknown"; the eval's
  `--max-cost` never refuses a `terminal-` model. There is deliberately no zero-price row in `AiPricing`.
- The CLI self-updates independently of subs2srs. A removed flag fails loudly (CLI message in the log
  and preview status line); that is accepted.

## Evaluation (`subs2srs.Eval`)

Purpose: tune the prompt and the rules defaults against human-labelled groupings, and gate regressions
without network access.

1. **Label**: in the preview, fix the grouping by hand and *Save as validation* → `<name>.grouping.json`
   in the *Validation Directory* preference. It stores the kept lines, the saved joins (kept
   projection), the limits in force, and a `proposal` block (`producer`, `model`, `promptVersion`,
   `joins`) recording what the rules/AI had proposed.
2. **Set layout**: every `*.grouping.json` under a directory; files under a `holdout/` folder at any
   depth (or `--holdout <dir>`) are the hold-out set. Reports go to `<set>/eval-reports/`. Hold-out files
   contain subtitle text: think before committing them.
3. **Run**: one producer per run, compared afterwards.

   ```sh
   dotnet run --project subs2srs.Eval -- --set <dir> --rules                       # baseline
   dotnet run --project subs2srs.Eval -- --set <dir> --model <id> --estimate        # cost first, no requests
   dotnet run --project subs2srs.Eval -- --set <dir> --model <id> --max-cost 1
   dotnet run --project subs2srs.Eval -- --set <dir> --model <id> --prompt "<extra>" --compare <run>
   ```

   `EvalOptions.Usage` is the full flag reference. `--prompt` is **extra instructions** (text or `@file`),
   not a prompt version: iterate through appended text, and when wording wins, move it into
   `AiGroupingPrompt.BuildSystem` and bump `PromptVersion`. Rules knobs (`--rules-gap`, `--rules-cues`,
   `--rules-no-cue`) tune `RuleGrouperOptions` the same way. The eval reuses the app's `AiGroupingCache`
   through `--cache <dir>`; there is no second raw-output store. It reads the user's `preferences.json`
   for keys (`--prefs` / `--no-prefs`). Exit codes: 1 usage, 2 no files, 3 cost cap, 4 every call failed.
4. **Scoring** (`GroupingScorer`): boundary precision/recall/F1 of `join == true` on the kept projection.
   A truth snippet with any internal boundary missing = under-merged, else if extended = over-merged; a
   single truth line swallowed into a group = over-merged. P/R are 1 when neither side joins, 0 when only
   one side does.
5. **Regression gate** (`EvalRegressionTests`): scores *cached* answers for the hold-out files in
   `subs2srs.Tests/Fixtures/eval/` against the F1 floor in `regression.json`. It skips until `holdout/`
   and `cache/` exist; once armed, a hold-out file **without** a cached answer fails (not skips). A
   `PromptVersion` bump turns it back into a skip until someone regenerates the cache with real calls, so
   say so in the PR when you bump it. `Fixtures/eval/README.md` has the arming steps.
