#!/usr/bin/env bash
#
# The outpost engine check (docs/OUTPOST.md §6.3).
#
# A listen host on Test.tscn at --fixed-fps 60 with no bots takes the strategist seat and puts an
# outpost up: drawn with its mast; its card offering the scout drone and the operative; - and =
# queueing them, and both coming out; the drone climbing to its cruise and holding it, drawn with
# four rotors; the operative drawn as a body; a smoke grenade going off into a cloud that sight
# lines through it meet and the client draws. Headless, on a copy of the tree with
# tools/EngineChecks/OutpostChecks.cs added as an autoload: the game itself never compiles or
# loads it.
#
#   ./scripts/outpost-checks.sh
#   SHOTS=/some/dir ./scripts/outpost-checks.sh   # on a display: also save screenshots
#
# With SHOTS, the copy is run on a display rather than headless — $DISPLAY, or xvfb-run when
# there is none — with the Compatibility renderer. Requires a Godot 4.6 .NET binary on PATH (or
# $GODOT) and the .NET SDK. One line per fact, PASS or FAIL; exits non-zero when any fails.
# KEEP=1 keeps the copy and its log.
set -uo pipefail

GODOT="${GODOT:-godot}"
GAME_PORT="${GAME_PORT:-7798}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-outpost-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/OutpostChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nOutpostChecks="*res://EngineChecks/OutpostChecks.cs"#' \
	"$COPY/project.godot"

if ! (cd "$COPY" && dotnet build Gdpyr.csproj -nologo -v quiet > "$COPY/build.log" 2>&1); then
	cat "$COPY/build.log" >&2
	echo "error: the copy did not build" >&2
	exit 2
fi

if [ ! -d "$COPY/.godot/imported" ]; then
	"$GODOT" --headless --path "$COPY" --import > "$COPY/import.log" 2>&1
fi

RUN=("$GODOT" --headless)
if [ -n "${SHOTS:-}" ]; then
	mkdir -p "$SHOTS"
	SHOTS="$(cd "$SHOTS" && pwd)"
	RUN=("$GODOT" --rendering-method gl_compatibility --rendering-driver opengl3 --resolution 1280x720)
	if [ -z "${DISPLAY:-}" ]; then
		RUN=(xvfb-run -a -s "-screen 0 1280x720x24" "${RUN[@]}")
	fi
fi

OUTPOST_CHECK=listen OUTPOST_SHOTS="${SHOTS:-}" timeout 900 "${RUN[@]}" --path "$COPY" --fixed-fps 60 \
	-- --listen "$GAME_PORT" --no-bots > "$COPY/outpost.log" 2>&1
status=$?

grep '^\[checks\]' "$COPY/outpost.log"
if [ "$status" -ne 0 ]; then
	echo "outpost: exit $status (log: $COPY/outpost.log${KEEP:+, kept})"
fi
exit "$status"
