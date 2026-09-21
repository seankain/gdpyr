#!/usr/bin/env bash
#
# Train a ground-force policy against headless gdpyr servers (docs/TRAINING.md).
#
# Starts one server per agent-api port, runs tools/Gdpyr.Trainer against them,
# and takes the servers down again on the way out. Everything binds loopback and
# nothing is exposed: the agent channel can spawn players and reset rounds, and
# deploy/gdpyr-server.service never passes --agent-api (docs/AGENT_API.md §4.1).
#
#   ./scripts/train.sh                          # one server, PPO, 100k steps
#   ./scripts/train.sh --ports 7900,7901,7902   # three servers, one learner
#   ./scripts/train.sh --algo dqn --steps 20000 --save runs/dqn
#
# Anything this script does not recognise is passed through to the trainer, so
# `./scripts/train.sh --lr 3e-4 --width 1024` works.
#
# Requires a Godot .NET binary on PATH (or $GODOT) and the .NET SDK.
set -euo pipefail

GODOT="${GODOT:-godot}"
PORTS="${PORTS:-7900}"
GAME_PORT="${GAME_PORT:-7777}"
BOTS="${BOTS:-6:1}"
LOG_DIR="${LOG_DIR:-build/train}"

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_DIR"

PASSTHROUGH=()
while [ $# -gt 0 ]; do
	case "$1" in
		--ports) PORTS="$2"; shift 2 ;;
		--bots) BOTS="$2"; shift 2 ;;
		--game-port) GAME_PORT="$2"; shift 2 ;;
		*) PASSTHROUGH+=("$1"); shift ;;
	esac
done

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 1
fi

mkdir -p "$LOG_DIR"

SERVERS=()
cleanup() {
	for pid in ${SERVERS[@]+"${SERVERS[@]}"}; do
		kill "$pid" 2>/dev/null || true
	done
}
trap cleanup EXIT INT TERM

index=0
IFS=',' read -ra PORT_LIST <<< "$PORTS"
for port in "${PORT_LIST[@]}"; do
	game_port=$((GAME_PORT + index))
	log="$LOG_DIR/server-$port.log"

	# --bots fills the other side. An attached policy counts as somebody playing,
	# so the backfill arrives around it rather than leaving it an empty map
	# (docs/AGENT_API.md §2).
	"$GODOT" --headless --path "$PROJECT_DIR" -- \
		--server "$game_port" --agent-api "127.0.0.1:$port" --bots "$BOTS" > "$log" 2>&1 &

	SERVERS+=("$!")
	echo ">> server on game port $game_port, agent api 127.0.0.1:$port -> $log"
	index=$((index + 1))
done

# Give the servers time to bind and bake navigation before the trainer connects.
for port in "${PORT_LIST[@]}"; do
	for _ in $(seq 1 100); do
		if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
			exec 3<&- 3>&-
			break
		fi
		sleep 0.2
	done
done

echo ">> training"
# An empty array must expand to nothing, not to one empty argument.
dotnet run --project tools/Gdpyr.Trainer -c Release -- --port "$PORTS" ${PASSTHROUGH[@]+"${PASSTHROUGH[@]}"}
