#!/usr/bin/env bash
#
# Train both gdpyr seats against each other on one set of servers
# (docs/TRAINING.md §8).
#
# Two learners, one game. The strategist owns the round and the ground learner
# follows it (--no-reset), because `reset` ends the round for everybody on the
# server and exactly one process may decide when an episode is over. Stepped mode
# composes the rest: the simulation advances only while every stepped session has
# ticks outstanding, so the two learners are a barrier rather than a race
# (docs/AGENT_API.md §5.2).
#
#   ./scripts/train-selfplay.sh                              # both sides learn
#   ./scripts/train-selfplay.sh --ports 7900,7901 --steps 200000
#   ./scripts/train-selfplay.sh --freeze ground --load-ground runs/ppo-ground
#
# Checkpoints go to <out>/ground and <out>/strategist, metrics to <out>/*.csv.
#
# **A round is twenty minutes of wall clock** (60 ticks of game per second, and a
# headless server is capped there — docs/AGENT_API.md §5.4), and a strategist
# episode is a whole round. Self-play is therefore the slowest thing in this
# repository: shorten `RoundDurationMinutes` in Match/default_gamemode.tres for
# training runs, and add servers with --ports.
#
# Requires a Godot .NET binary on PATH (or $GODOT) and the .NET SDK.
set -euo pipefail

GODOT="${GODOT:-godot}"
PORTS="${PORTS:-7900}"
GAME_PORT="${GAME_PORT:-7777}"
BOTS="${BOTS:-6:1}"
OUT="${OUT:-runs/selfplay}"
LOG_DIR="${LOG_DIR:-build/selfplay}"
STEPS="${STEPS:-100000}"
ALGO="${ALGO:-ppo}"
HISTORY="${HISTORY:-1}"
FREEZE="${FREEZE:-none}"
LOAD_GROUND="${LOAD_GROUND:-}"
LOAD_STRATEGIST="${LOAD_STRATEGIST:-}"

# The hold one learner waits through is the other one's optimizer step, so the
# server must be willing to stand still for longer than it would for one
# (docs/AGENT_API.md §5.2).
STEP_TIMEOUT="${STEP_TIMEOUT:-60000}"

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_DIR"

while [ $# -gt 0 ]; do
	case "$1" in
		--ports) PORTS="$2"; shift 2 ;;
		--bots) BOTS="$2"; shift 2 ;;
		--game-port) GAME_PORT="$2"; shift 2 ;;
		--out) OUT="$2"; shift 2 ;;
		--steps) STEPS="$2"; shift 2 ;;
		--algo) ALGO="$2"; shift 2 ;;
		--history) HISTORY="$2"; shift 2 ;;
		--step-timeout) STEP_TIMEOUT="$2"; shift 2 ;;
		--freeze) FREEZE="$2"; shift 2 ;;
		--load-ground) LOAD_GROUND="$2"; shift 2 ;;
		--load-strategist) LOAD_STRATEGIST="$2"; shift 2 ;;
		-h|--help)
			sed -n '2,26p' "${BASH_SOURCE[0]}"
			exit 0 ;;
		*)
			echo "error: unrecognized argument '$1'" >&2
			exit 2 ;;
	esac
done

case "$FREEZE" in
	none|ground|strategist) ;;
	*) echo "error: --freeze is 'none', 'ground' or 'strategist'" >&2; exit 2 ;;
esac

if [ "$FREEZE" = "ground" ] && [ -z "$LOAD_GROUND" ]; then
	echo "error: --freeze ground needs --load-ground: there is nothing to play without a policy" >&2
	exit 2
fi

if [ "$FREEZE" = "strategist" ] && [ -z "$LOAD_STRATEGIST" ]; then
	echo "error: --freeze strategist needs --load-strategist" >&2
	exit 2
fi

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 1
fi

mkdir -p "$LOG_DIR" "$OUT"

PIDS=()
cleanup() {
	for pid in ${PIDS[@]+"${PIDS[@]}"}; do
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

	PIDS+=("$!")
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

common=(--port "$PORTS" --algo "$ALGO" --steps "$STEPS" --history "$HISTORY"
	--step-timeout "$STEP_TIMEOUT")

strategist=("${common[@]}" --policy strategist --save "$OUT/strategist"
	--metrics "$OUT/strategist.csv")
ground=("${common[@]}" --policy ground --no-reset --save "$OUT/ground"
	--metrics "$OUT/ground.csv")

[ -n "$LOAD_STRATEGIST" ] && strategist+=(--load "$LOAD_STRATEGIST")
[ -n "$LOAD_GROUND" ] && ground+=(--load "$LOAD_GROUND")
[ "$FREEZE" = "strategist" ] && strategist+=(--play)
[ "$FREEZE" = "ground" ] && ground+=(--play)

# The strategist starts first: it owns the round.
echo ">> strategist learner -> $LOG_DIR/strategist.log"
dotnet run --project tools/Gdpyr.Trainer -c Release -- "${strategist[@]}" \
	> "$LOG_DIR/strategist.log" 2>&1 &
STRATEGIST_PID=$!
PIDS+=("$STRATEGIST_PID")

# Long enough for the first learner to hold a seat and reset the round. A ground
# learner that attached first would spend its first episode in whatever state the
# server booted into.
sleep 5

echo ">> ground learner -> $LOG_DIR/ground.log"
dotnet run --project tools/Gdpyr.Trainer -c Release -- "${ground[@]}" \
	> "$LOG_DIR/ground.log" 2>&1 &
GROUND_PID=$!
PIDS+=("$GROUND_PID")

echo ">> watch with: tail -f $LOG_DIR/strategist.log $LOG_DIR/ground.log"

status=0
wait "$STRATEGIST_PID" || status=$?
wait "$GROUND_PID" || status=$?

echo ">> checkpoints in $OUT"
exit "$status"
