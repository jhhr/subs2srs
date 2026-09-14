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
| `subs2srs.Tests` | `SnippetE2ETests.cs` | xUnit (ffmpeg) | Rules grouping + gap removal through the whole pipeline; clip durations checked with ffprobe; preview joins override the rules; video concat |
| `subs2srs.Tests` | `AnimatedSnapshotTests.cs` | xUnit | Animated snapshot settings (defaults, project round trip), `ffmpeg -encoders` parsing and encoder choice, `-vf`/codec argument builders (culture-safe) |
| `subs2srs.Tests` | `AnimatedSnapshotE2ETests.cs` | xUnit (ffmpeg + encoder) | Animated webp/avif through the whole pipeline: files, frame counts (WebP RIFF chunks, ffprobe for avif), gap removal shortens the animation, TSV column, Off, missing encoder reported |
| `subs2srs.UiTests` | `AnimatedSnapshotUiTests.cs` | xUnit (GTK) | Snapshots-tab controls follow the encoder probe and the checkbox; Go writes animated webp |
| `subs2srs.UiTests` | `PreviewGroupingTests.cs` | xUnit (GTK) | Preview proposes a grouping, buttons/keys/drop edit it, validation export |

`[RequiresFfmpegFact]` skips a test when ffmpeg is missing; `[RequiresFfmpegEncoderFact(format)]` also when the ffmpeg found has no encoder for that animated snapshot format.

## Running

```bash
# All tests
make test

# Individual suite (via dotnet)
dotnet test subs2srs.Tests/subs2srs.Tests.csproj --filter "FullyQualifiedName~UtilsSubsTests"
```

## How they work

### xUnit suites
All tests use the standard `xunit` package. No external test frameworks.
- **Parallelization disabled**: `[assembly: CollectionBehavior(DisableTestParallelization = true)]` prevents race conditions on the mutable `Settings.Instance` singleton.
- **Singleton reset**: `Settings.Instance.reset()` called in constructor and `Dispose()` to isolate test state.
- **Temp directories**: `Path.GetTempPath()` + `Guid` creates isolated dirs. Cleaned up via `IDisposable.Dispose()`.
- **Mocking**: External CLI tools (`ffmpeg`, `ffprobe`) are not invoked. File I/O tests use real temp files.

## Test environment
- All tests create temporary directories via `Path.Combine(Path.GetTempPath(), ...)` and clean up in `Dispose()`
- No root privileges required
- No real media files, subtitles, or system paths are touched
- Tests run sequentially to avoid singleton pollution
