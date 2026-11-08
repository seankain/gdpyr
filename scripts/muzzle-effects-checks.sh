#!/usr/bin/env bash
#
# The muzzle effects engine check (Scripts/Effects/MuzzleEffects.cs, docs/MUZZLE_EFFECTS.md §5).
#
# A listen host on Test.tscn at --fixed-fps 60, its own player through the input map: their
# own rifle draws a flash at the end of the viewmodel's barrel and following it, a puff and a
# case a round, the case out to the right and at rest on the floor; rounds fired in nobody's
# name draw every part 3 m from the camera, fewer further off and nothing at 30 m; a
# strategist's rifleman 4 m off flashes ahead of its eye and a tank at the edge of its hull,
# with no case; a hand grenade draws nothing; everything is taken back; and the console's
# `muzzle`. Headless, on a copy of the tree with tools/EngineChecks/MuzzleEffectsChecks.cs
# added as an autoload: the game itself never compiles or loads it.
#
#   ./scripts/muzzle-effects-checks.sh
#   SHOTS=/some/dir ./scripts/muzzle-effects-checks.sh   # on a display: also save screenshots
#
# With SHOTS, the copy is run on a display rather than headless — $DISPLAY, or xvfb-run when
# there is none — with the Compatibility renderer, and time is slowed round each shot so its
# flash is still up to be photographed. Requires a Godot 4.6 .NET binary on PATH (or $GODOT)
# and the .NET SDK. One line per fact, PASS or FAIL; exits non-zero when any fails. KEEP=1
# keeps the copy and its log.
set -uo pipefail

GODOT="${GODOT:-godot}"
GAME_PORT="${GAME_PORT:-7796}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-muzzle-effects-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/MuzzleEffectsChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nMuzzleEffectsChecks="*res://EngineChecks/MuzzleEffectsChecks.cs"#' \
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

MUZZLE_EFFECTS_CHECK=listen MUZZLE_SHOTS="${SHOTS:-}" timeout 900 "${RUN[@]}" --path "$COPY" --fixed-fps 60 \
	-- --listen "$GAME_PORT" --no-bots > "$COPY/muzzle-effects.log" 2>&1
status=$?

grep '^\[checks\]' "$COPY/muzzle-effects.log"
if [ "$status" -ne 0 ]; then
	echo "muzzle effects: exit $status (log: $COPY/muzzle-effects.log${KEEP:+, kept})"
fi
exit "$status"
