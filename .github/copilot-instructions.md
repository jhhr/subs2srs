# Copilot instructions

The instructions for this repository are in [`AGENTS.md`](../AGENTS.md) at the repository root, shared
with other AI tools. Read it before changing code, and follow its *Definition of done*, *Hard rules*
and *Gotchas* sections. Topic docs are in [`docs/`](../docs/); `AGENTS.md` says which one to read for
which task. Do not add instructions to this file; edit `AGENTS.md`.

The points that matter even for a one-line completion:

- C# on .NET 10, GTK 4 through GirCore 0.7.0, xUnit. No `.sln`: commands name a project file, e.g.
  `dotnet test subs2srs.Tests/subs2srs.Tests.csproj`.
- Format and parse numbers with `CultureInfo.InvariantCulture` (ffmpeg arguments, JSON, asserted strings).
- Some files are CRLF and some LF; keep each file's existing line endings.
- Tests never call the network or start the `claude` CLI; use `FakeChatProvider`, `ScriptedHandler` or
  the injectable process runner.
- Never log or persist API keys outside `preferences.json`.
