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

## Defaults that are guesses

No labelled data existed when these were chosen. The eval console exists to settle them
([ai-grouping.md](ai-grouping.md)); it needs ≥ 6 labelled episodes over 2+ shows, some in `holdout/`.

| Default | Value | Concern |
| --- | --- | --- |
| Rules grouper | join gap ≤ 1500 ms **and** a cue (line ends in one of `?？…→、,`, or the actor changes) | The original design had cues optional and no commas; with commas the rules join many ordinary sentence continuations |
| Gap removal | on by default, `GapKeepMs` 500, also affects sentence-joined lines in snippet mode Off | Changes output for users who never enabled snippets |
| `MaxSnippetSeconds` / chunk target | 15 s / 200 lines | From the plan, unmeasured |
| System prompt wording | `PromptVersion` 2 | Never tuned |
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
  2. Small code follow-ups from the docs review: `SubsRetimerLauncher.RunAsync` builds its own
     `ProcessStartInfo` instead of `UtilsCommon.makeToolStartInfo`, so its pipes are not forced to
     UTF-8 (a non-ASCII saved path on Windows would come back garbled); the two `SubsRetimer*`
     preferences are not in `DialogPref` or `Logger.writeSettingsToLog` (the "five places" rule);
     the env-gated `RealTool_*` tests should use `[RequiresEnvFact("SUBSRETIMER_EXE")]`; and
     `DialogSubsRetimer` has no `subs2srs.UiTests` coverage (it was verified once by a throwaway
     Xvfb harness: auto-align success handing the file back, failure re-enabling Run, wildcard
     validation).

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
