#!/usr/bin/env bash
#
# Play a saved policy against the scripted bots and report what it won
# (docs/TRAINING.md §9, docs/RL_ARCHITECTURE.md §6).
#
# The same server and the same seat as a training run, with the learner switched
# off: the policy acts, nothing is optimized, and the run stops after a fixed
# number of *finished rounds* rather than a fixed number of decisions — "how
# often does this win" is a question about rounds, and a round is a different
# number of decisions every time.
#
#   ./scripts/evaluate.sh runs/ppo-ground                      # 20 rounds, ground
#   ./scripts/evaluate.sh runs/strategist --policy strategist --episodes 10
#   ./scripts/evaluate.sh runs/ppo-ground --ports 7900,7901    # four at a time
#
# The number to write down is the last line: episodes, decided rounds, and the
# win rate over the decided ones. A truncated episode has no outcome and is
# counted in neither, which is why a ground run with the default two-minute
# episode cap reports far fewer decided rounds than episodes — raise
# --episode-steps if you want the policy judged on whole rounds.
#
# Requires a Godot .NET binary on PATH (or $GODOT) and the .NET SDK.
set -euo pipefail

GODOT="${GODOT:-godot}"
PORTS="${PORTS:-7900}"
GAME_PORT="${GAME_PORT:-7777}"
BOTS="${BOTS:-6:1}"
POLICY="${POLICY:-ground}"
EPISODES="${EPISODES:-20}"
LOG_DIR="${LOG_DIR:-build/evaluate}"

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_DIR"

LOAD=""
PASSTHROUGH=()
while [ $# -gt 0 ]; do
	case "$1" in
		--ports) PORTS="$2"; shift 2 ;;
		--bots) BOTS="$2"; shift 2 ;;
		--game-port) GAME_PORT="$2"; shift 2 ;;
		--policy) POLICY="$2"; shift 2 ;;
		--episodes) EPISODES="$2"; shift 2 ;;
		--load) LOAD="$2"; shift 2 ;;
		-h|--help)
			sed -n '2,20p' "${BASH_SOURCE[0]}"
			exit 0 ;;
		--*) PASSTHROUGH+=("$1"); shift ;;
		*) LOAD="$1"; shift ;;
	esac
done

if [ -z "$LOAD" ]; then
	echo "usage: ./scripts/evaluate.sh <checkpoint-dir> [--policy ground|strategist] [--episodes 20]" >&2
	exit 2
fi

if [ ! -d "$LOAD" ]; then
	echo "error: '$LOAD' is not a directory. RLMatrix saves a folder of networks, not a file." >&2
	exit 2
fi

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

	"$GODOT" --headless --path "$PROJECT_DIR" -- \
		--server "$game_port" --agent-api "127.0.0.1:$port" --bots "$BOTS" > "$log" 2>&1 &

	SERVERS+=("$!")
	echo ">> server on game port $game_port, agent api 127.0.0.1:$port -> $log"
	index=$((index + 1))
done

for port in "${PORT_LIST[@]}"; do
	for _ in $(seq 1 100); do
		if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
			exec 3<&- 3>&-
			break
		fi
		sleep 0.2
	done
done

echo ">> evaluating $LOAD as a $POLICY policy over $EPISODES episodes"

# --steps is the ceiling rather than the plan: --episodes is what stops the run,
# and a round that never decides must not run for ever.
dotnet run --project tools/Gdpyr.Trainer -c Release -- \
	--port "$PORTS" --policy "$POLICY" --play --load "$LOAD" \
	--episodes "$EPISODES" --steps 2000000 \
	${PASSTHROUGH[@]+"${PASSTHROUGH[@]}"}
