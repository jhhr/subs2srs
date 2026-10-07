# subsretimer `--report` fixtures

Used by `SubsRetimerLauncherTests` (`RetimeReport.Read`). Written on 2026-10-04 by
`subsretimer` 0.1.0 (jhhr/subsretimer at 5d8475e, Linux), run as the season's retime stage
runs it:

    subsretimer --print-output --auto --output <s2s/name.ja.srt> [--min-match 0.8]
      --report <s2s/name.retime.json> --ref-encoding utf-8 --target-encoding utf-8
      -- <s2s/name.en.srt> <name.srt>

on generated SRT files (120 numbered lines, the JP file's lines after the 40th 15 s late) in a
folder `アニメ S1`. The files are kept byte for byte, with one change: the folder the run was
in, a temporary one, is replaced by `/home/user/Anime` in every path.

| Fixture | JP file | Exit | Reason | Segments | Reference covered |
| --- | --- | --- | --- | --- | --- |
| `saved.retime.json` | `第1話.srt`, the first 114 lines; `--min-match 0.8` | 0 | null | 2 | 0.95 |
| `below-min-match.retime.json` | `第2話.srt`, the first 60 lines; `--min-match 0.8` | 2 | `below min-match` | 2 | 0.5 |
| `no-timed-lines.retime.json` | `第3話.srt`, empty | 2 | `no timed lines` | 0 | null |
