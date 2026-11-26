#!/usr/bin/env bash
#
# The ladder engine check (Scripts/Fps/Ladder.cs, Scripts/Fps/PlayerClimbingState.cs,
# Scenes/Environment/ScaffoldBuilding.tscn, docs/LADDERS.md §7).
#
# listen: a listen host on the greybox at --fixed-fps 60, its own player through the input map.
# The scaffold building's four ladders are where the scene puts them; each is walked into,
# climbed, gone over onto its landing, taken again from the landing and come down to its foot;
# a jump pushes off one, and with the walk still into it catches it again; walking past one does
# not take it, and strafing on one does not move the body off it. The navigation mesh joins every
# level for a ground bot's agent and none for a unit's, and the player, steered by the ground
# bot's own ladder steering along a bot agent's path, goes from the ground to the roof, down to
# the first floor and out onto the ground beyond the scaffold.
#
# net: a dedicated server puts a client's player at the foot of the 7 m wall ladder; the client
# climbs it, goes over onto the roof, takes it again and comes down, predicted; the server has
# the player where the client does at the top and at the foot, and the corrections are counted.
#
# Headless, on a copy of the tree with tools/EngineChecks/LadderChecks.cs added as an autoload:
# the game itself never compiles or loads it.
#
#   ./scripts/ladder-checks.sh                       # listen and net
#   ./scripts/ladder-checks.sh listen
#   SHOTS=/some/dir ./scripts/ladder-checks.sh listen   # on a display: also screenshots
#
# With SHOTS, the listen part is run on a display rather than headless — $DISPLAY, or xvfb-run
# when there is none — with the Compatibility renderer, and saves views of the building and of
# each kind of ladder first. Requires a Godot 4.6 .NET binary on PATH (or $GODOT) and the .NET
# SDK. One line per fact, PASS or FAIL; exits non-zero when any fails. KEEP=1 keeps the copy and
# its logs.
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

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-ladder-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/LadderChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nLadderChecks="*res://EngineChecks/LadderChecks.cs"#' \
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
	local run=("$GODOT" --headless)
	if [ -n "${SHOTS:-}" ]; then
		mkdir -p "$SHOTS"
		SHOTS="$(cd "$SHOTS" && pwd)"
		run=("$GODOT" --rendering-method gl_compatibility --rendering-driver opengl3 --resolution 1280x720)
		if [ -z "${DISPLAY:-}" ]; then
			run=(xvfb-run -a -s "-screen 0 1280x720x24" "${run[@]}")
		fi
	fi

	LADDER_CHECK=listen LADDER_SHOTS="${SHOTS:-}" timeout 900 "${run[@]}" --path "$COPY" --fixed-fps 60 \
		-- --listen "$GAME_PORT" --no-bots > "$COPY/ladder-listen.log" 2>&1
	local status=$?
	grep '^\[checks\]' "$COPY/ladder-listen.log"
	[ "$status" -ne 0 ] && echo "listen: exit $status (log: $COPY/ladder-listen.log${KEEP:+, kept})"
	return "$status"
}

run_net() {
	local dir="$COPY/net"
	mkdir -p "$dir"
	rm -f "$dir"/*

	LADDER_CHECK=server LADDER_DIR="$dir" timeout 300 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --server "$NET_SERVER_PORT" --no-bots > "$COPY/ladder-server.log" 2>&1 &
	local server=$!
	sleep 5
	LADDER_CHECK=client LADDER_DIR="$dir" timeout 300 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --client "127.0.0.1:$NET_SERVER_PORT" > "$COPY/ladder-client.log" 2>&1 &
	local client=$!

	local status=0
	wait "$server" || status=1
	wait "$client" || status=1
	grep '^\[checks\]' "$COPY/ladder-server.log"
	grep '^\[checks\]' "$COPY/ladder-client.log"
	[ "$status" -ne 0 ] && echo "net: failed (logs: $COPY/ladder-server.log, $COPY/ladder-client.log${KEEP:+, kept})"
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
