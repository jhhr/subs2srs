# mkvmerge -J fixtures

Used by `MkvTracksTests`. Recorded on 2026-10-04 with
`mkvmerge v82.0 ('I'm The President') 64-bit` (Ubuntu, `/usr/bin/mkvmerge`), by running
`mkvmerge -J <name>.mkv` in the folder of the file, so `file_name` holds no path. The
output is kept byte for byte.

The mkv files were made with mkvmerge from generated subtitle files (numbered lines such
as `English full line 3.`; no third-party content) and, for `multi-tracks.json`, a
2-second ffmpeg `testsrc` video with a sine tone:

| Fixture | Tracks (id: what, events) | Picked |
| --- | --- | --- |
| `multi-tracks.json` | 0 video, 1 audio, 2 eng ASS "English" 6, 3 eng SRT "English (full)" 9, 4 eng ASS "Signs" forced 12, 5 jpn SRT "Japanese" 15 | 3 |
| `en-us-webvtt.json` | 0 eng ASS "English" 6, 1 SRT "English (US)" muxed with `--language 0:en-US` 11, 2 eng WebVTT 20, 3 jpn SRT 15 | 1 |
| `image-tracks.handwritten.json` | **Hand-written** (nothing here to mux PGS or VobSub from): 0 video, 1 audio, 2 eng PGS 640, 3 en-US VobSub 320, 4 eng SRT "Signs" forced 12, 5 jpn PGS 700 | none |

mkvmerge 82 reports `--language 0:en-US` as `"language": "eng"` with
`"language_ietf": "en-US"`, sets `default_track` on every subtitle track it muxes, and
reports `num_index_entries: 0` (not a missing field) for a file written with `--no-cues`.
