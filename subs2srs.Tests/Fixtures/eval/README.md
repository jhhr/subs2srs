# Grouping evaluation fixtures

The regression gate `EvalRegressionTests.CachedOutputs_ReachTheF1Floor_OnTheHoldoutSet` scores the
**cached** answers of one model against the hold-out validation files, with no network. It is
skipped until this directory holds data. `dotnet test` never calls a provider; only the
`subs2srs.Eval` console does.

## Directory convention

Two places share the same layout: the user's validation directory (the *Validation Directory*
preference, default `<Output Directory>/validation`) and this fixtures directory.

```
<set>/                         tuning set: every *.grouping.json below it, any depth
<set>/holdout/                 hold-out set: never tune the prompt on these; reported separately
<set>/eval-reports/            what subs2srs.Eval writes (<run>.run.json, .csv, .md); pass --out to move it
```

`subs2srs.Eval --holdout <dir>` uses another directory as the hold-out set instead.

Here, in `subs2srs.Tests/Fixtures/eval/`:

| Path | Content |
| --- | --- |
| `regression.json` | `model`, `chunkTargetLines`, `extraInstructions` of the run the gate scores, and `minBoundaryF1` (0..1) it must reach |
| `holdout/*.grouping.json` | the hold-out validation files (copied from `<set>/holdout/`) |
| `cache/<sha256>.json` | the model's answers in the app's cache format (`AiGroupingCache`), keyed by the exact lines, limits, model, chunking, prompt version and extra instructions |

Only `*.json` files are copied next to the test binaries (`Fixtures/**/*.json` in the csproj).

## Filling or refreshing the cache

The cache key includes `AiGroupingPrompt.PromptVersion`, so after every prompt change the gate
skips (with the reason in the test output) until the answers are regenerated:

```bash
# from the repository root; the provider key comes from preferences.json or the environment
dotnet run --project subs2srs.Eval -- --set subs2srs.Tests/Fixtures/eval \
  --model claude-sonnet-5 --cache subs2srs.Tests/Fixtures/eval/cache --out /tmp/eval-reports
```

Because everything under this directory is treated as the set, only the `holdout/` files are
requested. Match `--model`, `--chunk` and `--prompt` to `regression.json`, or update it. A
hold-out file without a cached answer fails the gate (it is not silently skipped) so that a newly
added file cannot go unscored; a whole cache without answers for the current prompt version skips it.

The hold-out files contain the subtitle text of the labelled episodes; choose material you may
keep in this repository.
