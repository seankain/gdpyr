#!/usr/bin/env bash
#
# The divergence probe (docs/AGENT_API.md §3, §10).
#
# Runs the same seeded episode twice against one headless server and reports the
# tick at which their state hashes part company. Its output is a number to be
# believed rather than a pass or a fail: Scripts/Sim is reproducible,
# MoveAndSlide() and NavigationAgent3D are not, and how tightly a regression
# assertion may be written depends on this measurement.
#
#   ./scripts/divergence.sh                 # 600 ticks, seed 7
#   ./scripts/divergence.sh --ticks 3600
#
# Requires a Godot .NET binary on PATH (or $GODOT) and the .NET SDK. It needs no
# RLMatrix and no libtorch.
set -euo pipefail

GODOT="${GODOT:-godot}"
PORT="${PORT:-7900}"
GAME_PORT="${GAME_PORT:-7777}"
BOTS="${BOTS:-6:1}"
LOG="${LOG:-build/divergence-server.log}"

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_DIR"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 1
fi

mkdir -p "$(dirname "$LOG")"

"$GODOT" --headless --path "$PROJECT_DIR" -- \
	--server "$GAME_PORT" --agent-api "127.0.0.1:$PORT" --bots "$BOTS" > "$LOG" 2>&1 &
SERVER=$!
trap 'kill "$SERVER" 2>/dev/null || true' EXIT INT TERM

for _ in $(seq 1 100); do
	if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then
		exec 3<&- 3>&-
		break
	fi
	sleep 0.2
done

dotnet run --project tools/Gdpyr.Probe -c Release -- --port "$PORT" "$@"
