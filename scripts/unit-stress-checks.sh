#!/usr/bin/env bash
#
# The unit stress test's engine check (Scenes/Dev/UnitStressTest.tscn, docs/UNIT_STRESS_TEST.md).
#
# The scene run on its own, as the editor's Run Current Scene runs it, at --fixed-fps 60: an
# offline round on its own map with no bots; the local player put on the ground force without
# being asked; the navigation baked; the locker, and the kiosk with both pads passing and each kind
# handed out; F10 and Escape opening and closing the panel; a wave put in rings round the barracks,
# told to hunt the player, closing on them; a wave past the field's limit stopping at it; Clear;
# Keep alive replacing the dead; a ramp stepping to the limit. Headless, on a copy of the tree with
# tools/EngineChecks/UnitStressChecks.cs added as an autoload: the game itself never compiles or
# loads it.
#
#   ./scripts/unit-stress-checks.sh
#   SHOTS=/some/dir ./scripts/unit-stress-checks.sh   # on a display: also save screenshots
#
# With SHOTS, the copy is run on a display rather than headless — $DISPLAY, or xvfb-run when
# there is none — with the Compatibility renderer. Requires a Godot 4.6 .NET binary on PATH (or
# $GODOT) and the .NET SDK. One line per fact, PASS or FAIL; exits non-zero when any fails.
# KEEP=1 keeps the copy and its log.
set -uo pipefail

GODOT="${GODOT:-godot}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-unit-stress-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/UnitStressChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nUnitStressChecks="*res://EngineChecks/UnitStressChecks.cs"#' \
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

# The scene's path in place of the main scene, as the editor's Run Current Scene does.
UNIT_STRESS_CHECK=1 UNIT_STRESS_SHOTS="${SHOTS:-}" timeout 900 "${RUN[@]}" --path "$COPY" --fixed-fps 60 \
	res://Scenes/Dev/UnitStressTest.tscn > "$COPY/unit-stress.log" 2>&1
status=$?

grep -E '^\[checks\]|^\[stress\]' "$COPY/unit-stress.log"
if [ "$status" -ne 0 ]; then
	echo "unit stress: exit $status (log: $COPY/unit-stress.log${KEEP:+, kept})"
fi
exit "$status"
