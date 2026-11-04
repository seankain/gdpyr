#!/usr/bin/env bash
#
# The crew vehicles' engine checks (docs/GROUND_VEHICLES.md §12, GV2 to GV5).
#
# What the 4x4 relies on the engine for — a body pushed out of a hull by its own
# MoveAndSlide, a bevelled hull in MotionMode.Floating sliding off a wall's end at
# 18 m/s, the springs over the test strip — and the 4x4 and the helicopter driven end
# to end on a listen host, through the input map. Headless, on a copy of the tree with
# tools/EngineChecks/CrewVehicleChecks.cs, CrewVehicleNetChecks.cs and CrewVehicleCombatChecks.cs
# added as autoloads, and CrewHelicopterChecks.cs as well for `heli` and `heli-net`: the game
# itself never compiles or loads them.
#
#   ./scripts/crew-vehicle-checks.sh            # physics, then the listen host
#   ./scripts/crew-vehicle-checks.sh physics
#   ./scripts/crew-vehicle-checks.sh game
#   ./scripts/crew-vehicle-checks.sh net        # GV3: a dedicated server and four clients
#   ./scripts/crew-vehicle-checks.sh combat     # GV4: a dedicated server, a driver, a gunner, a passenger
#   ./scripts/crew-vehicle-checks.sh heli       # GV5: the helicopter on a listen host
#   ./scripts/crew-vehicle-checks.sh heli-net   # GV5: a dedicated server, a pilot and a door passenger
#
# `net` runs in real time, not at --fixed-fps: a dedicated server, a UDP relay that
# delays every datagram NET_DELAY_MS ± NET_JITTER_MS each way (40 ± 10 by default: 80 ms
# RTT, 20 ms jitter — DEPLOYMENT.md §4's tc netem line, in user space, for kernels
# without sch_netem), and a driver, a passenger and a strategist through it; a fourth
# client joins once the server asks. `combat` is the same set-up with a driver, a gunner
# and a passenger, firing from the 4x4 and at it; `heli-net` a pilot and a passenger in the
# helicopter's door. All three require python3 as well.
#
# Requires a Godot 4.6 .NET binary on PATH (or $GODOT) and the .NET SDK. One line per
# fact, PASS or FAIL; exits non-zero when any fails. KEEP=1 keeps the copy and its logs.
set -uo pipefail

GODOT="${GODOT:-godot}"
GAME_PORT="${GAME_PORT:-7791}"
NET_SERVER_PORT="${NET_SERVER_PORT:-7792}"
NET_RELAY_PORT="${NET_RELAY_PORT:-7793}"
NET_DELAY_MS="${NET_DELAY_MS:-40}"
NET_JITTER_MS="${NET_JITTER_MS:-10}"
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
cp "$PROJECT_DIR/tools/EngineChecks/CrewVehicleChecks.cs" "$PROJECT_DIR/tools/EngineChecks/CrewVehicleNetChecks.cs" \
	"$PROJECT_DIR/tools/EngineChecks/CrewVehicleCombatChecks.cs" "$PROJECT_DIR/tools/EngineChecks/CrewHelicopterChecks.cs" \
	"$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nCrewVehicleChecks="*res://EngineChecks/CrewVehicleChecks.cs"\nCrewVehicleNetChecks="*res://EngineChecks/CrewVehicleNetChecks.cs"\nCrewVehicleCombatChecks="*res://EngineChecks/CrewVehicleCombatChecks.cs"#' \
	"$COPY/project.godot"

# GV5's checks are an autoload only while `heli` or `heli-net` runs, so that every other mode
# runs with the three autoloads it was written against (docs/GROUND_VEHICLES.md §12, GV5).
heli_autoload() {
	sed -i '/^CrewHelicopterChecks=/d' "$COPY/project.godot"
	if [ "$1" = "on" ]; then
		sed -i 's#^CrewVehicleCombatChecks=.*#&\nCrewHelicopterChecks="*res://EngineChecks/CrewHelicopterChecks.cs"#' \
			"$COPY/project.godot"
	fi
}

if ! (cd "$COPY" && dotnet build Gdpyr.csproj -nologo -v quiet > "$COPY/build.log" 2>&1); then
	cat "$COPY/build.log" >&2
	echo "error: the copy did not build" >&2
	exit 2
fi

if [ ! -d "$COPY/.godot/imported" ]; then
	"$GODOT" --headless --path "$COPY" --import > "$COPY/import.log" 2>&1
fi

# GV3: the dedicated server, the relay, three clients through it and a fourth when the
# server asks for one. Every part's log is kept in the copy; their [checks] lines are
# printed in the order the parts are listed.
run_net() {
	local dir="$COPY/net"
	mkdir -p "$dir"
	rm -f "$dir"/*

	CREW_NET_CHECK=server CREW_NET_DIR="$dir" timeout 600 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --server "$NET_SERVER_PORT" --no-bots > "$COPY/net-server.log" 2>&1 &
	local server=$!
	python3 "$PROJECT_DIR/tools/EngineChecks/delay_relay.py" --listen "$NET_RELAY_PORT" \
		--server "127.0.0.1:$NET_SERVER_PORT" --delay-ms "$NET_DELAY_MS" --jitter-ms "$NET_JITTER_MS" \
		> "$COPY/net-relay.log" 2>&1 &
	local relay=$!
	sleep 5

	local pids=()
	for part in driver passenger strategist; do
		CREW_NET_CHECK=$part CREW_NET_DIR="$dir" timeout 600 "$GODOT" --headless --path "$COPY" --max-fps 60 \
			-- --client "127.0.0.1:$NET_RELAY_PORT" > "$COPY/net-$part.log" 2>&1 &
		pids+=($!)
		sleep 2
	done

	local waited=0
	while [ ! -f "$dir/join" ] && kill -0 "$server" 2>/dev/null && [ "$waited" -lt 420 ]; do
		sleep 1
		waited=$((waited + 1))
	done

	if [ -f "$dir/join" ]; then
		CREW_NET_CHECK=joiner CREW_NET_DIR="$dir" timeout 300 "$GODOT" --headless --path "$COPY" --max-fps 60 \
			-- --client "127.0.0.1:$NET_RELAY_PORT" > "$COPY/net-joiner.log" 2>&1 &
		pids+=($!)
	fi

	local status=0
	local code
	wait "$server"
	code=$?
	echo "net-server: exit $code" >> "$COPY/net-exits.log"
	[ "$code" -ne 0 ] && status=1
	local parts=(driver passenger strategist joiner)
	for i in "${!pids[@]}"; do
		wait "${pids[$i]}"
		code=$?
		echo "net-${parts[$i]}: exit $code" >> "$COPY/net-exits.log"
		[ "$code" -ne 0 ] && status=1
	done

	kill "$relay" 2>/dev/null
	wait "$relay" 2>/dev/null

	for part in server driver passenger strategist joiner; do
		[ -f "$COPY/net-$part.log" ] && grep '^\[checks\]' "$COPY/net-$part.log"
	done
	tail -n 1 "$COPY/net-relay.log"
	cat "$COPY/net-exits.log"
	return "$status"
}

# GV4: the dedicated server, the relay, and a driver, a gunner and a passenger through it.
run_combat() {
	local dir="$COPY/combat"
	mkdir -p "$dir"
	rm -f "$dir"/*

	CREW_COMBAT_CHECK=server CREW_NET_DIR="$dir" timeout 900 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --server "$NET_SERVER_PORT" --no-bots > "$COPY/combat-server.log" 2>&1 &
	local server=$!
	python3 "$PROJECT_DIR/tools/EngineChecks/delay_relay.py" --listen "$NET_RELAY_PORT" \
		--server "127.0.0.1:$NET_SERVER_PORT" --delay-ms "$NET_DELAY_MS" --jitter-ms "$NET_JITTER_MS" \
		> "$COPY/combat-relay.log" 2>&1 &
	local relay=$!
	sleep 5

	local parts=(driver gunner passenger)
	local pids=()
	for part in "${parts[@]}"; do
		CREW_COMBAT_CHECK=$part CREW_NET_DIR="$dir" timeout 900 "$GODOT" --headless --path "$COPY" --max-fps 60 \
			-- --client "127.0.0.1:$NET_RELAY_PORT" > "$COPY/combat-$part.log" 2>&1 &
		pids+=($!)
		sleep 2
	done

	local status=0
	local code
	wait "$server"
	code=$?
	echo "combat-server: exit $code" >> "$COPY/combat-exits.log"
	[ "$code" -ne 0 ] && status=1
	for i in "${!pids[@]}"; do
		wait "${pids[$i]}"
		code=$?
		echo "combat-${parts[$i]}: exit $code" >> "$COPY/combat-exits.log"
		[ "$code" -ne 0 ] && status=1
	done

	kill "$relay" 2>/dev/null
	wait "$relay" 2>/dev/null

	for part in server driver gunner passenger; do
		[ -f "$COPY/combat-$part.log" ] && grep '^\[checks\]' "$COPY/combat-$part.log"
	done
	tail -n 1 "$COPY/combat-relay.log"
	cat "$COPY/combat-exits.log"
	return "$status"
}

# GV5: the dedicated server, the relay, and a pilot and a passenger through it.
run_heli_net() {
	local dir="$COPY/heli"
	mkdir -p "$dir"
	rm -f "$dir"/*

	CREW_HELI_CHECK=server CREW_NET_DIR="$dir" timeout 900 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --server "$NET_SERVER_PORT" --no-bots > "$COPY/heli-server.log" 2>&1 &
	local server=$!
	python3 "$PROJECT_DIR/tools/EngineChecks/delay_relay.py" --listen "$NET_RELAY_PORT" \
		--server "127.0.0.1:$NET_SERVER_PORT" --delay-ms "$NET_DELAY_MS" --jitter-ms "$NET_JITTER_MS" \
		> "$COPY/heli-relay.log" 2>&1 &
	local relay=$!
	sleep 5

	local parts=(pilot passenger)
	local pids=()
	for part in "${parts[@]}"; do
		CREW_HELI_CHECK=$part CREW_NET_DIR="$dir" timeout 900 "$GODOT" --headless --path "$COPY" --max-fps 60 \
			-- --client "127.0.0.1:$NET_RELAY_PORT" > "$COPY/heli-$part.log" 2>&1 &
		pids+=($!)
		sleep 2
	done

	local status=0
	local code
	wait "$server"
	code=$?
	echo "heli-server: exit $code" >> "$COPY/heli-exits.log"
	[ "$code" -ne 0 ] && status=1
	for i in "${!pids[@]}"; do
		wait "${pids[$i]}"
		code=$?
		echo "heli-${parts[$i]}: exit $code" >> "$COPY/heli-exits.log"
		[ "$code" -ne 0 ] && status=1
	done

	kill "$relay" 2>/dev/null
	wait "$relay" 2>/dev/null

	for part in server pilot passenger; do
		[ -f "$COPY/heli-$part.log" ] && grep '^\[checks\]' "$COPY/heli-$part.log"
	done
	tail -n 1 "$COPY/heli-relay.log"
	cat "$COPY/heli-exits.log"
	return "$status"
}

failed=0
for mode in "${MODES[@]}"; do
	if [ "$mode" = "heli" ] || [ "$mode" = "heli-net" ]; then
		heli_autoload on
	else
		heli_autoload off
	fi

	if [ "$mode" = "heli-net" ]; then
		run_heli_net
		status=$?
		echo "heli-net: exit $status"
		[ "$status" -ne 0 ] && failed=1
		continue
	fi

	if [ "$mode" = "heli" ]; then
		CREW_HELI_CHECK=listen timeout 900 "$GODOT" --headless --path "$COPY" --fixed-fps 60 \
			-- --listen "$GAME_PORT" --no-bots > "$COPY/heli.log" 2>&1
		status=$?
		grep '^\[checks\]' "$COPY/heli.log"
		echo "heli: exit $status"
		[ "$status" -ne 0 ] && failed=1
		continue
	fi

	if [ "$mode" = "combat" ]; then
		run_combat
		status=$?
		echo "combat: exit $status"
		[ "$status" -ne 0 ] && failed=1
		continue
	fi

	if [ "$mode" = "net" ]; then
		run_net
		status=$?
		echo "net: exit $status"
		[ "$status" -ne 0 ] && failed=1
		continue
	fi

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
