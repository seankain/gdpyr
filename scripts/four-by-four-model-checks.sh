#!/usr/bin/env bash
#
# The 4x4 model engine check (Models/Vehicles/four_by_four/four_by_four.glb,
# tools/blender/four_by_four.py, Scripts/Fps/CrewVehicle.cs, docs/GROUND_VEHICLES.md §16).
#
# A floor and the vehicle test strip built by the check, and the real CrewVehicle stepped tick by
# tick at --fixed-fps 60. Parked, the 4x4 is drawn as the model, every wheel where the rules put
# it and on the floor, the wishbones and coilovers at rest, the gun where the definition mounts
# it, turning and elevating. Driven, every wheel rolls forward by the distance over its radius;
# steered, the front pair turn at the rules' Ackermann angles and the tandem does not, the outside
# wheels rolling further. Over the strip's bumps every wheel rides on its own spring within its
# travel, the front axle first and the tandem's two after it, the wishbones and coilovers
# following. A wreck is drawn burnt to the last wheel; with no model the greybox is drawn, rolling.
#
# Headless, on a copy of the tree with tools/EngineChecks/FourByFourModelChecks.cs added as an
# autoload: the game itself never compiles or loads it.
#
#   ./scripts/four-by-four-model-checks.sh
#   SHOTS=/some/dir ./scripts/four-by-four-model-checks.sh   # on a display: also screenshots
#
# With SHOTS, it is run on a display rather than headless — $DISPLAY, or xvfb-run when there is
# none — with the Compatibility renderer, and saves the 4x4 parked from the front, the side and
# behind, steered, and articulated over a bump. Requires a Godot 4.6 .NET binary on PATH (or
# $GODOT) and the .NET SDK. One line per fact, PASS or FAIL; exits non-zero when any fails.
# KEEP=1 keeps the copy and its log.
set -uo pipefail

GODOT="${GODOT:-godot}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-four-by-four-model-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/FourByFourModelChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nFourByFourModelChecks="*res://EngineChecks/FourByFourModelChecks.cs"#' \
	"$COPY/project.godot"

if ! (cd "$COPY" && dotnet build Gdpyr.csproj -nologo -v quiet > "$COPY/build.log" 2>&1); then
	cat "$COPY/build.log" >&2
	echo "error: the copy did not build" >&2
	exit 2
fi

if [ ! -d "$COPY/.godot/imported" ]; then
	"$GODOT" --headless --path "$COPY" --import > "$COPY/import.log" 2>&1
fi

run=("$GODOT" --headless)
if [ -n "${SHOTS:-}" ]; then
	mkdir -p "$SHOTS"
	SHOTS="$(cd "$SHOTS" && pwd)"
	run=("$GODOT" --rendering-method gl_compatibility --rendering-driver opengl3 --resolution 1280x720)
	if [ -z "${DISPLAY:-}" ]; then
		run=(xvfb-run -a -s "-screen 0 1280x720x24" "${run[@]}")
	fi
fi

FOUR_BY_FOUR_MODEL_CHECK=model FOUR_BY_FOUR_MODEL_SHOTS="${SHOTS:-}" timeout 600 "${run[@]}" --path "$COPY" \
	--fixed-fps 60 > "$COPY/model.log" 2>&1
status=$?
grep '^\[checks\]' "$COPY/model.log"
[ "$status" -ne 0 ] && echo "model: exit $status (log: $COPY/model.log${KEEP:+, kept})"
exit "$status"
