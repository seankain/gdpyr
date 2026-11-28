#!/usr/bin/env bash
#
# The helicopter model engine check (Models/Vehicles/helicopter/helicopter.glb,
# tools/blender/helicopter.py, Scripts/Fps/CrewVehicle.cs, docs/GROUND_VEHICLES.md §17).
#
# A floor built by the check, and the real CrewVehicle at --fixed-fps 60. Parked, the helicopter is
# drawn as the model, standing on its wheels; each seat of Vehicles/helicopter.tres rests on the
# cushion the model draws for it, the pilot's inside the hull's boxes and the two outer seats'
# outside them; the main rotor's hub is over the origin, above the cabin, and the fan is in the
# fenestron's box. The rotors stand still with the rotor stopped and turn at their rates as it
# spins up. Tilted, and flown off the floor and ahead, every seat stays on its drawn cushion. A
# wreck is drawn burnt to the last mesh; with no model the greybox is drawn.
#
# Headless, on a copy of the tree with tools/EngineChecks/HelicopterModelChecks.cs added as an
# autoload: the game itself never compiles or loads it.
#
#   ./scripts/helicopter-model-checks.sh
#   SHOTS=/some/dir ./scripts/helicopter-model-checks.sh   # on a display: also screenshots
#
# With SHOTS, it is run on a display rather than headless — $DISPLAY, or xvfb-run when there is
# none — with the Compatibility renderer, and saves the helicopter parked from the front, the side
# and behind, from the pilot's chase camera and the left seat, and in flight, nose down. Requires a
# Godot 4.6 .NET binary on PATH (or $GODOT) and the .NET SDK. One line per fact, PASS or FAIL;
# exits non-zero when any fails. KEEP=1 keeps the copy and its log.
set -uo pipefail

GODOT="${GODOT:-godot}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-helicopter-model-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/HelicopterModelChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nHelicopterModelChecks="*res://EngineChecks/HelicopterModelChecks.cs"#' \
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

HELICOPTER_MODEL_CHECK=model HELICOPTER_MODEL_SHOTS="${SHOTS:-}" timeout 600 "${run[@]}" --path "$COPY" \
	--fixed-fps 60 > "$COPY/model.log" 2>&1
status=$?
grep '^\[checks\]' "$COPY/model.log"
[ "$status" -ne 0 ] && echo "model: exit $status (log: $COPY/model.log${KEEP:+, kept})"
exit "$status"
