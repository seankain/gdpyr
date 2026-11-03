#!/usr/bin/env bash
#
# The crew vehicles' engine checks (docs/GROUND_VEHICLES.md §12, GV2).
#
# What the 4x4 relies on the engine for — a body pushed out of a hull by its own
# MoveAndSlide, a bevelled hull in MotionMode.Floating sliding off a wall's end at
# 18 m/s, the springs over the test strip — and the 4x4 and the helicopter driven end
# to end on a listen host, through the input map. Headless, on a copy of the tree with
# tools/EngineChecks/CrewVehicleChecks.cs added as an autoload: the game itself never
# compiles or loads it.
#
#   ./scripts/crew-vehicle-checks.sh            # both: physics, then the listen host
#   ./scripts/crew-vehicle-checks.sh physics
#   ./scripts/crew-vehicle-checks.sh game
#
# Requires a Godot 4.6 .NET binary on PATH (or $GODOT) and the .NET SDK. One line per
# fact, PASS or FAIL; exits non-zero when any fails. KEEP=1 keeps the copy and its logs.
set -uo pipefail

GODOT="${GODOT:-godot}"
GAME_PORT="${GAME_PORT:-7791}"
if [ $# -eq 0 ]; then
	MODES=(physics game)
else
	MODES=("$@")
fi

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-crew-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

# The tree as it stands, without history or build output; the import cache comes along
# when there is one, which saves importing it again.
(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/CrewVehicleChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nCrewVehicleChecks="*res://EngineChecks/CrewVehicleChecks.cs"#' \
	"$COPY/project.godot"

if ! (cd "$COPY" && dotnet build Gdpyr.csproj -nologo -v quiet > "$COPY/build.log" 2>&1); then
	cat "$COPY/build.log" >&2
	echo "error: the copy did not build" >&2
	exit 2
fi

if [ ! -d "$COPY/.godot/imported" ]; then
	"$GODOT" --headless --path "$COPY" --import > "$COPY/import.log" 2>&1
fi

failed=0
for mode in "${MODES[@]}"; do
	args=()
	[ "$mode" = "game" ] && args=(-- --listen "$GAME_PORT" --no-bots)
	CREW_VEHICLE_CHECK="$mode" timeout 900 "$GODOT" --headless --path "$COPY" --fixed-fps 60 "${args[@]}" \
		> "$COPY/$mode.log" 2>&1
	status=$?
	grep '^\[checks\]' "$COPY/$mode.log"
	echo "$mode: exit $status"
	[ "$status" -ne 0 ] && failed=1
done

[ "${KEEP:-0}" = "1" ] && echo "kept $COPY"
exit "$failed"
