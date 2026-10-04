#!/bin/sh
# No cd, unlike the GUI's launcher: relative paths in the arguments (--project show.s2s.json,
# --season .) are the caller's. .NET returns the preferences, log and AI cache folders as
# absolute paths only when ~/.config and ~/.local/share exist (otherwise "", and they would
# resolve under the current directory), so make sure both exist.
mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}" "${XDG_DATA_HOME:-$HOME/.local/share}"
exec /usr/lib/subs2srs/subs2srs-cli "$@"
