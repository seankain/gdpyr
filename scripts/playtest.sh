#!/usr/bin/env bash
#
# Run a scenario against a headless gdpyr server (docs/AGENT_API.md §9).
#
# This is the CI contract, and it is deliberately three things: a deterministic
# exit code, a machine-readable summary on --json, and a trace file that replays.
# A coding agent with no Godot knowledge adds a scenario file, runs this, and
# gets back a failed assertion with the tick it failed on.
#
#   ./scripts/playtest.sh Tests/Scenarios/rifle_lethality.json
#   ./scripts/playtest.sh Tests/Scenarios/*.json --json
#   ./scripts/playtest.sh Tests/Scenarios/round_length.json --attach --port 7900
#
# Exit codes: 0 every claim held · 1 a claim failed · 2 the harness could not run.
#
# The harness starts the server itself, one per scenario, because the roster a
# scenario wants is in the scenario file. Everything binds loopback: the agent
# socket can spawn players and reset rounds, and deploy/gdpyr-server.service
# never passes --agent-api (docs/AGENT_API.md §4.1).
#
# Requires a Godot .NET binary on PATH (or $GODOT) and the .NET SDK. It needs no
# RLMatrix and no libtorch.
set -uo pipefail

GODOT="${GODOT:-godot}"
PORT="${PORT:-7900}"
GAME_PORT="${GAME_PORT:-7777}"
TRACE_DIR="${TRACE_DIR:-build/playtest}"

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_DIR"

if [ $# -eq 0 ]; then
	echo "usage: ./scripts/playtest.sh <scenario.json> [more.json ...] [--json] [--attach]" >&2
	exit 2
fi

# --attach means somebody already has a server listening; only then is Godot not
# needed here.
NEEDS_GODOT=1
for arg in "$@"; do
	[ "$arg" = "--attach" ] && NEEDS_GODOT=0
done

if [ "$NEEDS_GODOT" -eq 1 ] && ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary, or pass --attach." >&2
	exit 2
fi

mkdir -p "$TRACE_DIR"

dotnet run --project tools/Gdpyr.Playtest -c Release -- \
	--godot "$GODOT" \
	--project "$PROJECT_DIR" \
	--port "$PORT" \
	--game-port "$GAME_PORT" \
	--trace-dir "$TRACE_DIR" \
	"$@"
