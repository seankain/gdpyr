#!/usr/bin/env bash
#
# The strategist's map engine check (Scripts/Ui/Minimap.cs, Scripts/Ui/MinimapView.cs).
#
# A listen host on Test.tscn at --fixed-fps 60 with no bots takes the strategist seat: the map
# in the bottom-right corner and the card beside it; the picture of the ground; the side's units,
# vehicles, barracks, structures and sites drawn on it; a click on the map putting the camera over
# that point looking straight down, and a drag carrying it; the middle button and the wheel over
# the map zooming the map and not the camera, the wheel off it still the camera's; and the map
# following a camera flown off it. Headless, on a copy of the tree with
# tools/EngineChecks/MinimapChecks.cs added as an autoload: the game itself never compiles or
# loads it.
#
#   ./scripts/minimap-checks.sh
#   SHOTS=/some/dir ./scripts/minimap-checks.sh   # on a display: also save screenshots
#   MAP=hills ./scripts/minimap-checks.sh          # on a data map (--map) rather than Test.tscn;
#                                                  # a site its things refuse there is measured, not failed
#
# With SHOTS, the copy is run on a display rather than headless — $DISPLAY, or xvfb-run when
# there is none — with the Compatibility renderer. Requires a Godot 4.6 .NET binary on PATH (or
# $GODOT) and the .NET SDK. One line per fact, PASS or FAIL; exits non-zero when any fails.
# KEEP=1 keeps the copy and its log.
set -uo pipefail

GODOT="${GODOT:-godot}"
GAME_PORT="${GAME_PORT:-7797}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-minimap-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/MinimapChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nMinimapChecks="*res://EngineChecks/MinimapChecks.cs"#' \
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

MINIMAP_CHECK=listen MINIMAP_SHOTS="${SHOTS:-}" timeout 900 "${RUN[@]}" --path "$COPY" --fixed-fps 60 \
	-- --listen "$GAME_PORT" --no-bots ${MAP:+--map "$MAP"} > "$COPY/minimap.log" 2>&1
status=$?

grep '^\[checks\]' "$COPY/minimap.log"
if [ "$status" -ne 0 ]; then
	echo "minimap: exit $status (log: $COPY/minimap.log${KEEP:+, kept})"
fi
exit "$status"
