#!/usr/bin/env bash
#
# The hurt feedback engine check (Scripts/Effects/CameraShake.cs, Scripts/Effects/TunnelVision.cs,
# Effects/Hurt/, docs/HURT_FEEDBACK.md §5).
#
# listen: a listen host on its default map at --fixed-fps 60, its own player. The effects are
# the scenes in Effects/Hurt/, wired into the player's scene with their curves, noise and shader;
# a hit of each severity goes as deep and shakes as hard as the scenes' curves say, deeper and
# longer the more grievous; the shake turns and slides what is drawn and never where a shot
# leaves from or where it goes, and gives the look back exactly; the tunnel only fades and is
# gone in its time; two hits stack; real rounds draw it through the authority's damage, a
# killing blow the deepest, and a respawn clears it; and the console's `hurt`.
#
# net: a dedicated server fires a rifle round and then DMR rounds at a client's player until one
# kills; the client draws each from its player record, as grievous as the record says it was.
#
# Headless, on a copy of the tree with tools/EngineChecks/HurtFeedbackChecks.cs added as an
# autoload: the game itself never compiles or loads it.
#
#   ./scripts/hurt-feedback-checks.sh               # listen and net
#   ./scripts/hurt-feedback-checks.sh listen
#   SHOTS=/some/dir ./scripts/hurt-feedback-checks.sh listen   # on a display: also screenshots
#
# With SHOTS, the listen part is run on a display rather than headless — $DISPLAY, or xvfb-run
# when there is none — with the Compatibility renderer, and saves the view with no hit and at the
# top of a 0.2, a 0.6 and a 1.0 hit. Requires a Godot 4.6 .NET binary on PATH (or $GODOT) and the
# .NET SDK. One line per fact, PASS or FAIL; exits non-zero when any fails. KEEP=1 keeps the copy
# and its logs.
set -uo pipefail

GODOT="${GODOT:-godot}"
GAME_PORT="${GAME_PORT:-7797}"
NET_SERVER_PORT="${NET_SERVER_PORT:-7798}"
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

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-hurt-feedback-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/HurtFeedbackChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nHurtFeedbackChecks="*res://EngineChecks/HurtFeedbackChecks.cs"#' \
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

	HURT_FEEDBACK_CHECK=listen HURT_SHOTS="${SHOTS:-}" timeout 600 "${run[@]}" --path "$COPY" --fixed-fps 60 \
		-- --listen "$GAME_PORT" --no-bots > "$COPY/hurt-listen.log" 2>&1
	local status=$?
	grep '^\[checks\]' "$COPY/hurt-listen.log"
	[ "$status" -ne 0 ] && echo "listen: exit $status (log: $COPY/hurt-listen.log${KEEP:+, kept})"
	return "$status"
}

run_net() {
	local dir="$COPY/net"
	mkdir -p "$dir"
	rm -f "$dir"/*

	HURT_FEEDBACK_CHECK=server HURT_FEEDBACK_DIR="$dir" timeout 300 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --server "$NET_SERVER_PORT" --no-bots > "$COPY/hurt-server.log" 2>&1 &
	local server=$!
	sleep 5
	HURT_FEEDBACK_CHECK=client HURT_FEEDBACK_DIR="$dir" timeout 300 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --client "127.0.0.1:$NET_SERVER_PORT" > "$COPY/hurt-client.log" 2>&1 &
	local client=$!

	local status=0
	wait "$server" || status=1
	wait "$client" || status=1
	grep '^\[checks\]' "$COPY/hurt-server.log"
	grep '^\[checks\]' "$COPY/hurt-client.log"
	[ "$status" -ne 0 ] && echo "net: failed (logs: $COPY/hurt-server.log, $COPY/hurt-client.log${KEEP:+, kept})"
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
