#!/usr/bin/env bash
#
# The supply truck model engine check (Models/Vehicles/supply_truck/supply_truck.glb,
# tools/blender/supply_truck.py, Scripts/Rts/Unit.cs, docs/UNIT_MODELS.md §7.4).
#
# listen: a listen host places a supply truck. It is drawn as the model, not the capsule, a
# truck's size, its lamps down -Z and its tilt behind, casting no shadow, and still collides as its
# capsule and draws its supply ring; firing on a place to its side it does not turn to it; driving
# off, it turns to the way it goes and keeps firing; where it was sent, its move order standing,
# it holds that way though its feet step back and forth over the point; held, it keeps it.
#
# net: a server places a supply truck and drives it off; the client draws the model, facing the
# way the server parked it.
#
# Headless, on a copy of the tree with tools/EngineChecks/SupplyTruckModelChecks.cs added as an
# autoload: the game itself never compiles or loads it.
#
#   ./scripts/supply-truck-model-checks.sh               # listen and net
#   ./scripts/supply-truck-model-checks.sh listen
#
# Requires a Godot 4.6 .NET binary on PATH (or $GODOT) and the .NET SDK. One line per fact, PASS
# or FAIL; exits non-zero when any fails. KEEP=1 keeps the copy and its logs.
set -uo pipefail

GODOT="${GODOT:-godot}"
GAME_PORT="${GAME_PORT:-7801}"
NET_SERVER_PORT="${NET_SERVER_PORT:-7802}"
if [ $# -eq 0 ]; then
	MODES=(listen net)
else
	MODES=("$@")
fi

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-supply-truck-model-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/SupplyTruckModelChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nSupplyTruckModelChecks="*res://EngineChecks/SupplyTruckModelChecks.cs"#' \
	"$COPY/project.godot"

if ! (cd "$COPY" && dotnet build Gdpyr.csproj -nologo -v quiet > "$COPY/build.log" 2>&1); then
	cat "$COPY/build.log" >&2
	echo "error: the copy did not build" >&2
	exit 2
fi

if [ ! -d "$COPY/.godot/imported" ]; then
	"$GODOT" --headless --path "$COPY" --import > "$COPY/import.log" 2>&1
fi

run_listen() {
	TRUCK_MODEL_CHECK=listen timeout 300 "$GODOT" --headless --path "$COPY" --fixed-fps 60 \
		-- --listen "$GAME_PORT" --no-bots > "$COPY/truck-listen.log" 2>&1
	local status=$?
	grep '^\[checks\]' "$COPY/truck-listen.log"
	[ "$status" -ne 0 ] && echo "listen: exit $status (log: $COPY/truck-listen.log${KEEP:+, kept})"
	return "$status"
}

run_net() {
	local dir="$COPY/net"
	mkdir -p "$dir"
	rm -f "$dir"/*

	TRUCK_MODEL_CHECK=server TRUCK_MODEL_DIR="$dir" timeout 300 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --server "$NET_SERVER_PORT" --no-bots > "$COPY/truck-server.log" 2>&1 &
	local server=$!
	sleep 5
	TRUCK_MODEL_CHECK=client TRUCK_MODEL_DIR="$dir" timeout 300 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --client "127.0.0.1:$NET_SERVER_PORT" > "$COPY/truck-client.log" 2>&1 &
	local client=$!

	local status=0
	wait "$server" || status=1
	wait "$client" || status=1
	grep '^\[checks\]' "$COPY/truck-server.log"
	grep '^\[checks\]' "$COPY/truck-client.log"
	[ "$status" -ne 0 ] && echo "net: failed (logs: $COPY/truck-server.log, $COPY/truck-client.log${KEEP:+, kept})"
	return "$status"
}

overall=0
for mode in "${MODES[@]}"; do
	case "$mode" in
		listen) run_listen || overall=1 ;;
		net) run_net || overall=1 ;;
		*) echo "error: no such part: $mode (listen, net)" >&2; overall=2 ;;
	esac
done
exit "$overall"
