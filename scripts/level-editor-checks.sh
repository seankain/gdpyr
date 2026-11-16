#!/usr/bin/env bash
#
# The level editor's engine checks (docs/LEVEL_EDITOR.md §11). LE0: the baseline and the spike; LE3: the world from a file;
# LE4: the editor.
#
# What the plan could not know without the engine, measured on a copy of the tree whose
# greybox has its slab replaced by terrain (tools/EngineChecks/LevelTerrain.cs): 241 x 241
# samples as the plan's collider, one ConcavePolygonShape3D for each 16 x 16 cells, or as the
# one HeightMapShape3D it started from (LE-D5), with tools/EngineChecks/LevelChecks.cs added
# as an autoload. The game itself never compiles or loads either.
#
#   ./scripts/level-editor-checks.sh            # ground, then slopes
#   ./scripts/level-editor-checks.sh ground     # rays against Sample, the collider's data, the bake: slab, flat, hills
#   ./scripts/level-editor-checks.sh slopes     # a body, a unit, the 4x4, the helicopter, structure sites
#   ./scripts/level-editor-checks.sh frame      # a dedicated server's tick with its bots: slab, flat, hills
#   ./scripts/level-editor-checks.sh net        # prediction on a 20° slope, behind an 80 ms link: slab, then the slope
#   ./scripts/level-editor-checks.sh net-1cm    # the slope again with corrections from 1 cm, as before LE-D25 made it 2 cm
#   ./scripts/level-editor-checks.sh export     # the stripped server export: the collider from bytes, the bake
#   ./scripts/level-editor-checks.sh suite      # the scenario suite on the flat terrain
#
# LE3, the world from a file, on the game's own path: the bundled data map (MAP, `hills`
# unless given) by --map, through MapLibrary, MapHost.tscn and LevelRoot, held to its file
# by tools/EngineChecks/LevelMapChecks.cs (LE3_CHECK). These parts run the tree as it is:
# the greybox is not edited for them.
#
#   ./scripts/level-editor-checks.sh map-practice  # --practice --map: the file's world, its rays, a bot round (LE3_SECONDS, 600)
#   ./scripts/level-editor-checks.sh map-server    # the same on a --server in real time, with a client that checks its own world
#   ./scripts/level-editor-checks.sh map-frame     # a dedicated server's tick with its bots and a client: the greybox, then MAP
#   ./scripts/level-editor-checks.sh map-export    # map-server from the stripped server export, the map read from its pack
#   ./scripts/level-editor-checks.sh map-suite     # the scenarios that name a data map, and the ones they were copied from
#   ./scripts/level-editor-checks.sh map-basin     # map-practice for LE3_SECONDS (60), a basin dug under the water: the bake's floor
#   ./scripts/level-editor-checks.sh map-shot      # the map drawn under a virtual display (xvfb-run): pictures in SHOT_OUT
#
# LE4, the editor (Scenes/MapEditor), driven with no window through its own commands by
# tools/EngineChecks/LevelEditorChecks.cs (LE4_CHECK), under a HOME of its own so that
# nobody's user://maps is touched: --edit LE4_NEW (le4-new) makes a new map; strokes of every
# brush, undo and redo, the keys, the water, save, open, autosave, save as; a clone of the
# bundled map, a stroke across a pad; and Test, whose second process plays the saved file
# and reports the round live.
#
#   ./scripts/level-editor-checks.sh editor        # the editor, then its test process
#   ./scripts/level-editor-checks.sh editor-shot   # the editor drawn under a virtual display (xvfb-run): pictures in SHOT_OUT
#
# LE5, prefabs and a round's pieces, by the same file (LE4_CHECK=prefabs): every prefab on a 6°
# slope, each of §6.5's answers, select, turn, parameters, take up, the deep check; then a map made
# only in the editor, tested — its test process (LE4_CHECK=round) plays a bot round for LE5_SECONDS
# (600) in which the pillbox the map stands at the start fires and is destroyed.
#
#   ./scripts/level-editor-checks.sh editor-prefabs
#
# LE6, roads and bridges, by the same file's LE6 half (LevelEditorChecks.Roads.cs): roads drawn
# across a ridge with the road tool, held to their limit by the collider at every centreline
# sample; then a river with three spans over it, roads to its ends and a span floating on piers,
# saved, and saved again with the three spans taken up (LE4_CHECK=roads). Each map is then played
# by --listen --map with no bots: the bridge and its piers, the floating span, a rifleman ordered
# across and a dropship ordered onto the deck (LE4_CHECK=bridge-round); the rifleman ordered
# across with the bridge gone (LE4_CHECK=bridge-gone); and tools/level_editor_checks/
# le6_4x4_bridge.json, a 4x4 driven across by a scenario's script, by tools/Gdpyr.Playtest.
#
#   ./scripts/level-editor-checks.sh editor-roads
#
# LE7, fetching a map, by tools/EngineChecks/LevelFetchChecks.cs (LE7_CHECK): a --server on a map
# no client has (gdpyr-map generate --seed FETCH_SEED, 7 unless given), and clients behind
# tools/EngineChecks/delay_relay.py, each under a HOME of its own. Behind NET_DELAY_MS ±
# NET_JITTER_MS each way: a client fetches the map and records a demo on it; the same client joins
# again from its cache and asks for nothing; one with another map's bytes under the hash's name
# fetches again; one whose server stops sending at half the chunks refuses the stream after 10 s
# and drops the link; one sent the map with a byte changed refuses it. Behind a second relay with
# no jitter that drops FETCH_LOSS (0.1) of datagrams each way, FETCH_DROPS (4) clients fetch while
# the server sends each, every tick, a reliable message on the default channel and one on the
# map's: a chunk sent again holds back the second and not the first. The first of them is traced
# (LE7_TRACE): a line for each of those messages as it arrives. Then the demo plays back on
# the map it names, from the cache, and in a home without the map it is refused, naming the hash.
#
#   ./scripts/level-editor-checks.sh map-fetch
#
# LE8, the dedicated server, by tools/EngineChecks/LevelUploadChecks.cs (LE8_CHECK): three maps of
# gdpyr-map generate (--seed UPLOAD_SEEDS: 7, 11 and 13 unless given), servers started one after
# another on the same port, each under a HOME of its own, and clients each under theirs.
# scripts/deploy-map.sh puts the first map in a home's user://maps and names it in maps/active
# (GDPYR_LOCAL=1: no ssh, no unit), and a file that is not a map is not copied; its path to a box is
# run with ssh, scp, sudo and systemctl stood in for. A server run by hand
# with --map-upload-token and no --map plays it, from where the script put it; a client builds a
# request for each of §9.5's answers, a wrong token and a replayed proof among them; two clients
# upload at once; and a next-map request sends the server away at once, with 0, saying nothing
# starts it again. A server with no token takes nothing, the map's whole stream sent to it all the
# same; one started with --map keeps an upload and will not be told its next map. Then a server
# with its bots, a client in its round and GDPYR_SUPERVISED=1: the editor publishes a map and names
# it next, by its own command, while the round is live; the server plays the round out and exits 0
# when the intermission is over; started again by the same command it plays the new map, and a
# client that has not got it fetches it (LE7's part, LE7_CHECK=fetch).
#
#   ./scripts/level-editor-checks.sh map-upload
#   ./scripts/level-editor-checks.sh map-upload-export   # the same, the servers the stripped server export
#
# Each part runs on the plan's collider and, where the two could differ, on the heightmap
# beside it. `frame` and the bake's times in `ground` are wall time: run them on a machine
# doing nothing else. `net` runs in real time behind tools/EngineChecks/delay_relay.py
# (NET_DELAY_MS ± NET_JITTER_MS each way, 40 ± 10 by default: 80 ms RTT) and needs python3; so
# does everything here, for the scene's edit and §10's hills (numpy). `export` needs the
# export templates. `suite` hands the copy to tools/Gdpyr.Playtest as its project: COLLIDER
# names the collider (trimesh, or heightmap), SCENARIOS the files (Tests/Scenarios/*.json by
# default) and SUITE_OUT the file its output goes to;
# tools/level_editor_checks/compare_suites.py holds that against the slab's own runs.
#
# Requires a Godot 4.6 .NET binary on PATH (or $GODOT) and the .NET SDK. One line per fact,
# PASS or FAIL, and `measured` lines; exits non-zero when any fact fails. KEEP=1 keeps the
# copy and its logs.
set -uo pipefail

GODOT="${GODOT:-godot}"
GAME_PORT="${GAME_PORT:-7795}"
NET_SERVER_PORT="${NET_SERVER_PORT:-7796}"
NET_RELAY_PORT="${NET_RELAY_PORT:-7797}"
NET_DELAY_MS="${NET_DELAY_MS:-40}"
NET_JITTER_MS="${NET_JITTER_MS:-10}"
AGENT_PORT="${AGENT_PORT:-7901}"
MAP="${MAP:-hills}"
LE4_NEW="${LE4_NEW:-le4-new}"
LE4_HILLS="${LE4_HILLS:-le4-hills}"
LE6_BRIDGE="${LE6_BRIDGE:-le6-bridge}"
LE6_GONE="${LE6_GONE:-le6-nobridge}"
LE3_SECONDS="${LE3_SECONDS:-600}"
FRAME_SECONDS="${FRAME_SECONDS:-300}"
FETCH_SEED="${FETCH_SEED:-7}"
FETCH_LOSS="${FETCH_LOSS:-0.1}"
FETCH_DROPS="${FETCH_DROPS:-4}"
DROP_RELAY_PORT="${DROP_RELAY_PORT:-7798}"
UPLOAD_SEEDS="${UPLOAD_SEEDS:-7 11 13}"
UPLOAD_TOKEN="${UPLOAD_TOKEN:-le8-check-token-0123456789}"
if [ $# -eq 0 ]; then
	MODES=(ground slopes)
else
	MODES=("$@")
fi

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-level-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/LevelChecks.cs" "$PROJECT_DIR/tools/EngineChecks/LevelTerrain.cs" \
	"$PROJECT_DIR/tools/EngineChecks/LevelMapChecks.cs" "$PROJECT_DIR/tools/EngineChecks/LevelEditorChecks.cs" \
	"$PROJECT_DIR/tools/EngineChecks/LevelEditorChecks.Roads.cs" "$PROJECT_DIR/tools/EngineChecks/LevelFetchChecks.cs" \
	"$PROJECT_DIR/tools/EngineChecks/LevelUploadChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nLevelChecks="*res://EngineChecks/LevelChecks.cs"\nLevelMapChecks="*res://EngineChecks/LevelMapChecks.cs"\nLevelEditorChecks="*res://EngineChecks/LevelEditorChecks.cs"\nLevelFetchChecks="*res://EngineChecks/LevelFetchChecks.cs"\nLevelUploadChecks="*res://EngineChecks/LevelUploadChecks.cs"#' \
	"$COPY/project.godot"

# §10's hills as a map file's samples would be, and the greybox with and without its slab.
if ! python3 "$PROJECT_DIR/tools/level_editor_checks/le0_terrain.py" hills "$COPY/EngineChecks/hills.i16"; then
	echo "error: the hills were not written (python3 with numpy is needed)" >&2
	exit 2
fi
cp "$COPY/Scenes/Test.tscn" "$COPY/Scenes/Test.tscn.slab"
cp "$COPY/Scenes/Test.tscn" "$COPY/Scenes/Test.tscn.heightmap"
if ! python3 "$PROJECT_DIR/tools/level_editor_checks/le0_terrain.py" scene "$COPY/Scenes/Test.tscn.heightmap"; then
	echo "error: the greybox is not what LE0's edit expects" >&2
	exit 2
fi

# The ground a part runs on: `slab` is the greybox as it is; anything else is the terrain
# started on that ground (LE0_TERRAIN), as the collider named second (LE0_COLLIDER).
ground() {
	if [ "$1" = "slab" ]; then
		cp "$COPY/Scenes/Test.tscn.slab" "$COPY/Scenes/Test.tscn"
		unset LE0_TERRAIN LE0_COLLIDER
	else
		cp "$COPY/Scenes/Test.tscn.heightmap" "$COPY/Scenes/Test.tscn"
		export LE0_TERRAIN="$1"
		export LE0_COLLIDER="${2:-trimesh}"
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

# What a part's log says: its facts and measures, and what the game says of its own bakes.
report() {
	grep -E '^\[checks\]|^\[rts\] navigation' "$1"
}

# A dedicated server on a ground, the relay, and one client through it, walking.
run_net() {
	local name="$1"
	local dir="$COPY/net-$name"
	mkdir -p "$dir"
	rm -f "$dir"/*

	LE0_CHECK=net-server LE0_DIR="$dir" timeout 900 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --server "$NET_SERVER_PORT" --no-bots > "$COPY/net-$name-server.log" 2>&1 &
	local server=$!
	python3 "$PROJECT_DIR/tools/EngineChecks/delay_relay.py" --listen "$NET_RELAY_PORT" \
		--server "127.0.0.1:$NET_SERVER_PORT" --delay-ms "$NET_DELAY_MS" --jitter-ms "$NET_JITTER_MS" \
		> "$COPY/net-$name-relay.log" 2>&1 &
	local relay=$!
	sleep 5

	LE0_CHECK=net-client LE0_DIR="$dir" timeout 900 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --client "127.0.0.1:$NET_RELAY_PORT" > "$COPY/net-$name-client.log" 2>&1 &
	local client=$!

	local status=0
	wait "$server" || status=1
	wait "$client" || status=1
	kill "$relay" 2>/dev/null
	wait "$relay" 2>/dev/null

	report "$COPY/net-$name-server.log"
	report "$COPY/net-$name-client.log"
	tail -n 1 "$COPY/net-$name-relay.log"
	return "$status"
}

# LE3: what MAPS.md §4 says must not appear in a map's log, counted. Each is a fact.
forbidden() {
	local log="$1" name="$2" line count
	for line in "no nodes in group" "no NavigationRegion3D in group" "navigation bake produced no geometry" \
		"is scaled or tilted" "are addressable on the wire"; do
		count=$(grep -c -- "$line" "$log")
		if [ "$count" -eq 0 ]; then
			echo "[checks] PASS $name: the log has no '$line'"
		else
			echo "[checks] FAIL $name: the log has '$line' $count times"
			failed=1
		fi
	done
}

# LE3: a dedicated server on the map, real time, and one client that checks its own world.
# $1 names the part; $2 is the server's command (the editor binary on the copy, or the export).
run_map_server() {
	local name="$1"
	shift
	local dir="$COPY/$name"
	mkdir -p "$dir"
	rm -f "$dir"/*
	LE3_CHECK=play LE3_DIR="$dir" LE3_MAP="$MAP" LE3_SECONDS="$LE3_SECONDS" timeout 3600 "$@" \
		-- --server "$GAME_PORT" --map "$MAP" --no-advertise > "$COPY/$name-server.log" 2>&1 &
	local server=$!
	sleep 8
	LE3_CHECK=client LE3_DIR="$dir" LE3_MAP="$MAP" timeout 3600 "$GODOT" --headless --path "$COPY" --max-fps 60 \
		-- --client "127.0.0.1:$GAME_PORT" > "$COPY/$name-client.log" 2>&1 &
	local client=$!
	local status=0
	wait "$server" || status=$?
	wait "$client" || status=$(( status > 0 ? status : $? ))
	report "$COPY/$name-server.log"
	grep -E '^\[level\]|^\[map\]' "$COPY/$name-server.log"
	report "$COPY/$name-client.log"
	forbidden "$COPY/$name-server.log" "$name, the server's log"
	forbidden "$COPY/$name-client.log" "$name, the client's log"
	return "$status"
}

failed=0
for mode in "${MODES[@]}"; do
	case "$mode" in
	ground)
		for spec in "slab" "flat trimesh" "hills trimesh" "flat heightmap" "hills heightmap"; do
			# shellcheck disable=SC2086
			ground $spec
			name="${spec// /-}"
			LE0_CHECK=ground timeout 1800 "$GODOT" --headless --path "$COPY" --fixed-fps 60 \
				-- --listen "$GAME_PORT" --no-bots > "$COPY/ground-$name.log" 2>&1
			status=$?
			report "$COPY/ground-$name.log"
			echo "ground, $spec: exit $status"
			[ "$status" -ne 0 ] && failed=1
		done
		;;

	slopes)
		# Everything on the plan's collider; then a body alone on the heightmap, which hands
		# the solver the same faces.
		for spec in "trimesh " "heightmap body"; do
			ground flat "${spec%% *}"
			name="${spec%% *}"
			LE0_CHECK=slopes LE0_ONLY="${spec#* }" timeout 3600 "$GODOT" --headless --path "$COPY" --fixed-fps 60 \
				-- --listen "$GAME_PORT" --no-bots > "$COPY/slopes-$name.log" 2>&1
			status=$?
			report "$COPY/slopes-$name.log"
			grep -c '^\[rts\] .* refused for peer' "$COPY/slopes-$name.log" | sed 's/^/the game refused a site, by its own log: /'
			echo "slopes, $name: exit $status"
			[ "$status" -ne 0 ] && failed=1
		done
		;;

	frame)
		for spec in "slab" "flat trimesh" "flat heightmap" "hills trimesh" "hills heightmap"; do
			# shellcheck disable=SC2086
			ground $spec
			name="${spec// /-}"
			# The server and its seven bots, and one client that joins and picks no side: the
			# bots are there only while somebody is connected.
			LE0_CHECK=frame timeout 3600 "$GODOT" --headless --path "$COPY" --fixed-fps 60 \
				-- --server "$GAME_PORT" --no-advertise > "$COPY/frame-$name.log" 2>&1 &
			server=$!
			sleep 6
			timeout 3600 "$GODOT" --headless --path "$COPY" --max-fps 30 \
				-- --client "127.0.0.1:$GAME_PORT" > "$COPY/frame-$name-client.log" 2>&1 &
			client=$!
			wait "$server"
			status=$?
			kill "$client" 2>/dev/null
			wait "$client" 2>/dev/null
			report "$COPY/frame-$name.log"
			grep -m 1 '^\[bots\]' "$COPY/frame-$name.log"
			grep '^\[rts\] .* refused for peer' "$COPY/frame-$name.log" | sed 's/ for peer [0-9]*//' | sort | uniq -c
			echo "frame, $spec: exit $status"
			[ "$status" -ne 0 ] && failed=1
		done
		;;

	net)
		for spec in "slab" "ramp:20:12 trimesh" "ramp:20:12 heightmap"; do
			# shellcheck disable=SC2086
			ground $spec
			name="${spec// /-}"
			run_net "${name//:/-}"
			status=$?
			echo "net, $spec: exit $status"
			[ "$status" -ne 0 ] && failed=1
		done
		;;

	net-1cm)
		# LE-D25's evidence, kept: the slope again, in a copy whose correction threshold is
		# 1 cm, as it was before LE3 made it 2 cm. The copy is built with it, run, and put back.
		ground "ramp:20:12" trimesh
		cp "$COPY/Scripts/Sim/SimConfig.cs" "$COPY/SimConfig.cs.kept"
		sed -i 's/public const float MispredictionThreshold = 0.02f;/public const float MispredictionThreshold = 0.01f;/' \
			"$COPY/Scripts/Sim/SimConfig.cs"
		if cmp -s "$COPY/Scripts/Sim/SimConfig.cs" "$COPY/SimConfig.cs.kept" \
			|| ! (cd "$COPY" && dotnet build Gdpyr.csproj -nologo -v quiet > "$COPY/build-1cm.log" 2>&1); then
			echo "net-1cm: SimConfig.MispredictionThreshold is not 0.02f, or the copy did not build with 0.01f"
			failed=1
		else
			echo "net-1cm: SimConfig.MispredictionThreshold is 0.01f in this copy"
			run_net ramp-20-12-trimesh-1cm
			status=$?
			echo "net-1cm, ramp:20:12 trimesh: exit $status"
			[ "$status" -ne 0 ] && failed=1
		fi
		cp "$COPY/SimConfig.cs.kept" "$COPY/Scripts/Sim/SimConfig.cs"
		(cd "$COPY" && dotnet build Gdpyr.csproj -nologo -v quiet > "$COPY/build.log" 2>&1) || failed=1
		;;

	export)
		# The deploy path's own preset, with the one change §9.1 plans: a file that is not a
		# resource is exported only through the include filter.
		ground hills trimesh
		sed -i 's#^include_filter="\*.map"#include_filter="*.map, EngineChecks/*.i16"#' "$COPY/export_presets.cfg"
		mkdir -p "$COPY/build/server"
		if ! "$GODOT" --headless --path "$COPY" --export-release "Linux Server" "$COPY/build/server/gdpyr-server" \
			> "$COPY/export.log" 2>&1 || [ ! -x "$COPY/build/server/gdpyr-server" ]; then
			tail -n 20 "$COPY/export.log" >&2
			echo "export: the stripped server did not export (are the export templates installed?)"
			failed=1
			continue
		fi

		ls -l "$COPY/build/server/gdpyr-server" | awk '{print "[export] gdpyr-server, pack embedded: " $5 " bytes"}'
		(cd "$COPY/build/server" && LE0_CHECK=export timeout 600 ./gdpyr-server --headless --fixed-fps 60 \
			-- --server "$GAME_PORT" --no-bots --no-advertise > "$COPY/export-run.log" 2>&1)
		status=$?
		report "$COPY/export-run.log"
		echo "export: exit $status"
		[ "$status" -ne 0 ] && failed=1
		;;

	suite)
		# With the slab's 30 m outside the walls kept, as boxes: a scenario stands a player
		# there, and a 240 m field has no ground there to stand on.
		ground flat "${COLLIDER:-trimesh}"
		export LE0_RIM=1
		out="${SUITE_OUT:-$COPY/suite.txt}"
		wrapper="$COPY/godot-fixed-fps"
		printf '#!/usr/bin/env bash\nexec "%s" --fixed-fps 60 "$@"\n' "$(command -v "$GODOT" || echo "$GODOT")" > "$wrapper"
		chmod +x "$wrapper"
		# shellcheck disable=SC2086
		(cd "$PROJECT_DIR" && dotnet run --project tools/Gdpyr.Playtest -c Release -- \
			--godot "$wrapper" --project "$COPY" --port "$AGENT_PORT" --game-port "$GAME_PORT" \
			--trace-dir "$COPY/traces" ${SCENARIOS:-Tests/Scenarios/*.json}) > "$out" 2>&1
		status=$?
		grep -c '\.\. PASS' "$out" | sed "s/^/suite on the flat $LE0_COLLIDER: scenarios that pass: /"
		grep -c '\.\. FAIL' "$out" | sed "s/^/suite on the flat $LE0_COLLIDER: scenarios that fail: /"
		echo "suite: exit $status (output: $out)"
		# A scenario failing is the suite's answer, not this script's: the slab fails some too.
		[ "$status" -ge 2 ] && failed=1
		unset LE0_RIM
		;;

	editor)
		# The editor and its test process, in a HOME of their own: user://maps starts empty.
		ground slab
		dir="$COPY/editor"
		home="$COPY/editor-home"
		rm -rf "$dir" "$home"
		mkdir -p "$dir" "$home"
		env -u XDG_DATA_HOME HOME="$home" LE4_CHECK=editor LE4_DIR="$dir" LE4_NEW="$LE4_NEW" LE4_HILLS="$LE4_HILLS" \
			timeout 1800 "$GODOT" --headless --path "$COPY" -- --edit "$LE4_NEW" > "$COPY/editor.log" 2>&1
		status=$?
		report "$COPY/editor.log"
		grep -E '^\[editor\]|^\[map\]|^\[level\] built|^\[match\] round live' "$COPY/editor.log"
		forbidden "$COPY/editor.log" "editor, the editor's and its test process's log"
		find "$home" -path '*app_userdata/gdpyr/maps/*' -type f -printf '[maps] %P %s bytes\n' | sort
		grep -q 'finished-editor' <(ls "$dir") || { echo "[checks] FAIL editor: the editor's part did not finish"; failed=1; }
		echo "editor: exit $status"
		[ "$status" -ne 0 ] && failed=1
		;;

	editor-prefabs)
		# The editor with no name, and the test process of the map it makes, in a HOME of their own.
		ground slab
		dir="$COPY/editor-prefabs"
		home="$COPY/editor-prefabs-home"
		rm -rf "$dir" "$home"
		mkdir -p "$dir" "$home"
		env -u XDG_DATA_HOME HOME="$home" LE4_CHECK=prefabs LE4_DIR="$dir" LE5_SECONDS="${LE5_SECONDS:-600}" \
			timeout 3600 "$GODOT" --headless --path "$COPY" -- --edit > "$COPY/editor-prefabs.log" 2>&1
		status=$?
		report "$COPY/editor-prefabs.log"
		grep -E '^\[editor\] (deep|testing)|^\[rts\] the map|^\[level\] built|^\[match\] round live' "$COPY/editor-prefabs.log"
		forbidden "$COPY/editor-prefabs.log" "editor-prefabs, the editor's and its test process's log"
		grep -q 'finished-prefabs' <(ls "$dir") || { echo "[checks] FAIL editor-prefabs: the editor's part did not finish"; failed=1; }
		echo "editor-prefabs: exit $status"
		[ "$status" -ne 0 ] && failed=1
		;;

	editor-roads)
		# The editor with no name makes the maps, in a HOME of its own; each is then played from that
		# library, and the 4x4's scenario is played on the one with the bridge.
		ground slab
		dir="$COPY/editor-roads"
		home="$COPY/editor-roads-home"
		rm -rf "$dir" "$home"
		mkdir -p "$dir" "$home"
		env -u XDG_DATA_HOME HOME="$home" LE4_CHECK=roads LE4_DIR="$dir" LE6_BRIDGE="$LE6_BRIDGE" LE6_GONE="$LE6_GONE" \
			timeout 1800 "$GODOT" --headless --path "$COPY" -- --edit > "$COPY/editor-roads.log" 2>&1
		status=$?
		report "$COPY/editor-roads.log"
		grep -E '^\[editor\] (road [0-9]+ graded|placed bridge|deep|saved)' "$COPY/editor-roads.log"
		forbidden "$COPY/editor-roads.log" "editor-roads, the editor's log"
		grep -q 'finished-roads' <(ls "$dir") || { echo "[checks] FAIL editor-roads: the editor's part did not finish"; failed=1; }
		echo "editor-roads: exit $status"
		[ "$status" -ne 0 ] && failed=1

		for part in bridge-round:"$LE6_BRIDGE" bridge-gone:"$LE6_GONE"; do
			name="${part%%:*}"
			env -u XDG_DATA_HOME HOME="$home" LE4_CHECK="$name" LE4_DIR="$dir" timeout 1200 "$GODOT" --headless \
				--path "$COPY" --fixed-fps 60 -- --listen "$GAME_PORT" --map "${part#*:}" --no-bots > "$COPY/$name.log" 2>&1
			status=$?
			report "$COPY/$name.log"
			grep -E '^\[level\] built|^\[rts\] the map' "$COPY/$name.log"
			forbidden "$COPY/$name.log" "$name, its log"
			held="$(cat "$dir/$name-done" 2>/dev/null || echo none)"
			if [ "${held%% of *}" != "${held##* of }" ] || [ "$held" = "none" ]; then
				echo "[checks] FAIL $name: its facts, as it wrote them: $held"
				failed=1
			fi
			echo "$name: exit $status, its facts $held"
		done

		# The 4x4, by a scenario's script, on the map with the bridge, from the same library.
		wrapper="$COPY/godot-fixed-fps"
		printf '#!/usr/bin/env bash\nexec "%s" --fixed-fps 60 "$@"\n' "$(command -v "$GODOT" || echo "$GODOT")" > "$wrapper"
		chmod +x "$wrapper"
		out="$COPY/le6-4x4.txt"
		# Godot's user:// is under the HOME the maps were saved in; dotnet's own files stay where they are.
		(cd "$PROJECT_DIR" && env -u XDG_DATA_HOME HOME="$home" DOTNET_CLI_HOME="$HOME" \
			NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}" dotnet run --project tools/Gdpyr.Playtest -c Release -- \
			--godot "$wrapper" --project "$COPY" --port "$AGENT_PORT" --game-port "$GAME_PORT" \
			--trace-dir "$COPY/traces" tools/level_editor_checks/le6_4x4_bridge.json) > "$out" 2>&1
		status=$?
		grep -h '^\[boot\]\|^\[level\] built' "$COPY/traces"/server-*.log
		grep -E 'PASS|FAIL|ERROR|  [a-z.]+.*(ok|FAILED)' "$out" | head -40
		if grep -q '\.\. FAIL' "$out" || ! grep -q '\.\. PASS' "$out"; then
			echo "[checks] FAIL editor-roads: the 4x4's scenario on $LE6_BRIDGE did not pass (output: $out)"
			failed=1
		else
			echo "[checks] PASS editor-roads: the 4x4's scenario on $LE6_BRIDGE: a 4x4 driven by its script crosses the bridge"
		fi
		echo "le6-4x4: exit $status"
		;;

	editor-shot)
		# Drawn, with the Compatibility renderer, in a virtual display and a HOME of its own. For a
		# person to look at; the facts are only that it drew.
		ground slab
		out="${SHOT_OUT:-$COPY/shots}/editor-shot"
		home="$COPY/editor-shot-home"
		rm -rf "$home"
		mkdir -p "$out" "$home"
		env -u XDG_DATA_HOME HOME="$home" LE4_CHECK=shot LE4_DIR="$out" timeout 900 \
			xvfb-run -a -s "-screen 0 1280x720x24" "$GODOT" --path "$COPY" --rendering-driver opengl3 \
			--resolution 1280x720 -- --edit > "$COPY/editor-shot.log" 2>&1
		status=$?
		report "$COPY/editor-shot.log"
		grep -m 1 '^OpenGL API' "$COPY/editor-shot.log"
		echo "editor-shot: exit $status (pictures: $out)"
		[ "$status" -ne 0 ] && failed=1
		;;

	map-practice)
		ground slab
		dir="$COPY/map-practice"
		mkdir -p "$dir"
		LE3_CHECK=play LE3_DIR="$dir" LE3_MAP="$MAP" LE3_SECONDS="$LE3_SECONDS" timeout 3600 "$GODOT" --headless \
			--path "$COPY" --fixed-fps 60 -- --practice --map "$MAP" > "$COPY/map-practice.log" 2>&1
		status=$?
		report "$COPY/map-practice.log"
		grep -E '^\[level\]|^\[map\]|^\[match\] round live' "$COPY/map-practice.log"
		forbidden "$COPY/map-practice.log" "map-practice"
		echo "map-practice, $MAP: exit $status"
		[ "$status" -ne 0 ] && failed=1
		;;

	map-server)
		ground slab
		run_map_server map-server "$GODOT" --headless --path "$COPY"
		status=$?
		echo "map-server, $MAP: exit $status"
		[ "$status" -ne 0 ] && failed=1
		;;

	map-frame)
		# LE0's frame part on the game's own path: a dedicated server at --fixed-fps 60 with
		# its 6 + 1 bots, and a client that joins and picks no side; the greybox, then the map.
		ground slab
		for map in greybox "$MAP"; do
			dir="$COPY/map-frame-$map"
			mkdir -p "$dir"
			LE3_CHECK=play LE3_DIR="$dir" LE3_MAP="$map" LE3_SECONDS="$FRAME_SECONDS" timeout 3600 "$GODOT" --headless \
				--path "$COPY" --fixed-fps 60 -- --server "$GAME_PORT" --map "$map" --no-advertise \
				> "$COPY/map-frame-$map.log" 2>&1 &
			server=$!
			sleep 6
			timeout 3600 "$GODOT" --headless --path "$COPY" --max-fps 30 \
				-- --client "127.0.0.1:$GAME_PORT" > "$COPY/map-frame-$map-client.log" 2>&1 &
			client=$!
			wait "$server"
			status=$?
			kill "$client" 2>/dev/null
			wait "$client" 2>/dev/null
			report "$COPY/map-frame-$map.log"
			grep -m 1 '^\[bots\]' "$COPY/map-frame-$map.log"
			grep '^\[rts\] .* refused for peer' "$COPY/map-frame-$map.log" | sed 's/ for peer [0-9]*//' | sort | uniq -c
			echo "map-frame, $map: exit $status"
			[ "$status" -ne 0 ] && failed=1
		done
		;;

	map-export)
		# The deploy path's own preset, as it is: its include filter takes *.map (§9.1).
		ground slab
		mkdir -p "$COPY/build/server"
		if ! "$GODOT" --headless --path "$COPY" --export-release "Linux Server" "$COPY/build/server/gdpyr-server" \
			> "$COPY/map-export.log" 2>&1 || [ ! -x "$COPY/build/server/gdpyr-server" ]; then
			tail -n 20 "$COPY/map-export.log" >&2
			echo "map-export: the stripped server did not export (are the export templates installed?)"
			failed=1
			continue
		fi

		grep -n '^include_filter' "$COPY/export_presets.cfg" | sed 's/^/[export] the preset: /'
		ls -l "$COPY/build/server/gdpyr-server" | awk '{print "[export] gdpyr-server, pack embedded: " $5 " bytes"}'
		run_map_server map-export "$COPY/build/server/gdpyr-server" --headless
		status=$?
		echo "map-export, $MAP: exit $status"
		[ "$status" -ne 0 ] && failed=1
		;;

	map-suite)
		# The scenarios that name a data map, and the ones each was copied from.
		ground slab
		out="${SUITE_OUT:-$COPY/map-suite.txt}"
		list=()
		for file in "$PROJECT_DIR"/Tests/Scenarios/*.json; do
			if grep -q '"map": "'"$MAP"'"' "$file"; then
				list+=("$file" "$PROJECT_DIR/Tests/Scenarios/$(basename "$file" | sed "s/^${MAP}_//")")
			fi
		done
		(cd "$PROJECT_DIR" && dotnet run --project tools/Gdpyr.Playtest -c Release -- \
			--godot "$GODOT" --project "$COPY" --port "$AGENT_PORT" --game-port "$GAME_PORT" \
			--trace-dir "$COPY/traces" "${list[@]}") > "$out" 2>&1
		status=$?
		grep -E '\.\. (PASS|FAIL)' "$out"
		echo "map-suite: exit $status (output: $out)"
		[ "$status" -ne 0 ] && failed=1
		;;

	map-basin)
		# The bundled map has no ground under its water: LE3_BASIN digs a disc of it before the
		# world is built, which the bake's box (LE-D8, §7.3) has to leave out.
		ground slab
		dir="$COPY/map-basin"
		mkdir -p "$dir"
		LE3_CHECK=play LE3_DIR="$dir" LE3_MAP="$MAP" LE3_SECONDS="${BASIN_SECONDS:-60}" LE3_BASIN="${BASIN:--20,-75,12,3}" \
			timeout 1800 "$GODOT" --headless --path "$COPY" --fixed-fps 60 -- --practice --map "$MAP" \
			> "$COPY/map-basin.log" 2>&1
		status=$?
		report "$COPY/map-basin.log"
		echo "map-basin, $MAP: exit $status"
		[ "$status" -ne 0 ] && failed=1
		;;

	map-fetch)
		ground slab
		dir="$COPY/map-fetch"
		rm -rf "$dir"
		mkdir -p "$dir"
		userdir() { echo "$1/.local/share/godot/app_userdata/gdpyr"; }
		if ! (cd "$PROJECT_DIR" && dotnet run --project tools/Gdpyr.MapTool -c Release -- generate "$dir/ridge.map" \
			--seed "$FETCH_SEED" --name Ridge) > "$COPY/map-fetch-maptool.log" 2>&1; then
			cat "$COPY/map-fetch-maptool.log" >&2
			echo "map-fetch: the map was not generated"
			failed=1
			continue
		fi
		sed 's/^/[map] /' "$COPY/map-fetch-maptool.log"
		hash=$(sha256sum "$dir/ridge.map" | cut -d' ' -f1)
		echo none > "$dir/fault"

		env -u XDG_DATA_HOME HOME="$dir/home-server" LE7_CHECK=server LE7_DIR="$dir" LE7_HASH="$hash" timeout 1800 \
			"$GODOT" --headless --path "$COPY" -- --server "$GAME_PORT" --map "$dir/ridge.map" --no-advertise --no-bots \
			> "$COPY/map-fetch-server.log" 2>&1 &
		server=$!
		python3 "$PROJECT_DIR/tools/EngineChecks/delay_relay.py" --listen "$NET_RELAY_PORT" \
			--server "127.0.0.1:$GAME_PORT" --delay-ms "$NET_DELAY_MS" --jitter-ms "$NET_JITTER_MS" \
			> "$COPY/map-fetch-relay.log" 2>&1 &
		relay=$!
		python3 "$PROJECT_DIR/tools/EngineChecks/delay_relay.py" --listen "$DROP_RELAY_PORT" \
			--server "127.0.0.1:$GAME_PORT" --delay-ms "$NET_DELAY_MS" --jitter-ms 0 --loss "$FETCH_LOSS" --seed 7 \
			> "$COPY/map-fetch-relay-drops.log" 2>&1 &
		relay_drops=$!
		sleep 8

		# One client: its phase for the server, its part, the relay, its home, the exit it should
		# end with, and its own arguments after those.
		fetch_client() {
			local name="$1" role="$2" port="$3" home="$4" expect="$5" status
			shift 5
			echo "$name" > "$dir/phase"
			mkdir -p "$home"
			env -u XDG_DATA_HOME HOME="$home" LE7_CHECK="$role" LE7_DIR="$dir" LE7_HASH="$hash" LE7_EXPECT="${expect_text:-}" LE7_TRACE="${trace:-0}" \
				timeout 300 "$GODOT" --headless --path "$COPY" --max-fps 60 -- --client "127.0.0.1:$port" "$@" \
				> "$COPY/map-fetch-$name.log" 2>&1
			status=$?
			report "$COPY/map-fetch-$name.log"
			grep -E '\[maps\]|^gdpyr:' "$COPY/map-fetch-$name.log" | sed 's/^WARNING: //'
			if [ "$status" -eq "$expect" ]; then
				echo "[checks] PASS map-fetch, $name: the client exited $status"
			else
				echo "[checks] FAIL map-fetch, $name: the client exited $status, not $expect"
				failed=1
			fi
			grep -q '^\[checks\] FAIL' "$COPY/map-fetch-$name.log" && failed=1
			return 0
		}

		# What is left in a home's cache, as a fact.
		cache_holds() {
			local home="$1" name="$2" want="$3" have
			have=$(ls "$(userdir "$home")/maps/cache" 2>/dev/null | tr '\n' ' ')
			if [ "$have" = "$want" ]; then
				echo "[checks] PASS map-fetch, $name: the cache holds '${have% }'"
			else
				echo "[checks] FAIL map-fetch, $name: the cache holds '${have% }', not '${want% }'"
				failed=1
			fi
		}

		expect_text=""
		fetch_client fetch fetch "$NET_RELAY_PORT" "$dir/home-1" 0 --record le7-fetch
		cache_holds "$dir/home-1" fetch "$hash.map "
		fetch_client cached cached "$NET_RELAY_PORT" "$dir/home-1" 0

		mkdir -p "$(userdir "$dir/home-2")/maps/cache"
		cp "$COPY/Maps/hills.map" "$(userdir "$dir/home-2")/maps/cache/$hash.map"
		fetch_client wrong wrong "$NET_RELAY_PORT" "$dir/home-2" 0
		cache_holds "$dir/home-2" wrong "$hash.map "

		echo cut > "$dir/fault"
		expect_text="the map stopped arriving"
		fetch_client cut refused "$NET_RELAY_PORT" "$dir/home-3" 1
		cache_holds "$dir/home-3" cut ""

		echo mismatch > "$dir/fault"
		expect_text="the host named"
		fetch_client mismatch refused "$NET_RELAY_PORT" "$dir/home-4" 1
		cache_holds "$dir/home-4" mismatch ""

		echo none > "$dir/fault"
		expect_text=""
		for n in $(seq 1 "$FETCH_DROPS"); do
			# The first is traced: a line for each probe as it arrives.
			trace=$([ "$n" -eq 1 ] && echo 1 || echo 0)
			fetch_client drops drops "$DROP_RELAY_PORT" "$dir/home-drops-$n" 0
			mv "$COPY/map-fetch-drops.log" "$COPY/map-fetch-drops-$n.log"
		done
		trace=0

		touch "$dir/stop"
		wait "$server"
		status=$?
		kill "$relay" "$relay_drops" 2>/dev/null
		wait "$relay" "$relay_drops" 2>/dev/null
		report "$COPY/map-fetch-server.log"
		grep -E '\[maps\]' "$COPY/map-fetch-server.log" | sed 's/^WARNING: //'
		echo "[relay] jitter: $(tail -n 1 "$COPY/map-fetch-relay.log")"
		echo "[relay] drops: $(tail -n 1 "$COPY/map-fetch-relay-drops.log")"
		echo "map-fetch, the server: exit $status"
		[ "$status" -ne 0 ] && failed=1

		# The demo the first client recorded, played back: in its home, where the cache has the map;
		# then in a home that has the demo and not the map.
		env -u XDG_DATA_HOME HOME="$dir/home-1" LE7_CHECK=playback LE7_DIR="$dir" LE7_HASH="$hash" timeout 300 \
			"$GODOT" --headless --path "$COPY" --max-fps 60 -- --playdemo le7-fetch > "$COPY/map-fetch-playback.log" 2>&1
		status=$?
		report "$COPY/map-fetch-playback.log"
		grep -E '^\[demo\]|^\[level\] built' "$COPY/map-fetch-playback.log" | head -n 3
		echo "map-fetch, playback: exit $status"
		[ "$status" -ne 0 ] && failed=1

		mkdir -p "$(userdir "$dir/home-5")"
		cp -r "$(userdir "$dir/home-1")/demos" "$(userdir "$dir/home-5")/"
		env -u XDG_DATA_HOME HOME="$dir/home-5" timeout 120 "$GODOT" --headless --path "$COPY" --max-fps 60 \
			-- --playdemo le7-fetch > "$COPY/map-fetch-playback-nomap.log" 2>&1
		status=$?
		line=$(grep -m 1 '^gdpyr: --playdemo' "$COPY/map-fetch-playback-nomap.log")
		if [ "$status" -eq 1 ] && echo "$line" | grep -q "recorded on map $hash, which is not in this build"; then
			echo "[checks] PASS map-fetch, playback without the map: exit 1, '$line'"
		else
			echo "[checks] FAIL map-fetch, playback without the map: exit $status, '$line'"
			failed=1
		fi
		;;

	map-upload|map-upload-export)
		ground slab
		part="$mode"
		server_cmd=("$GODOT" --headless --path "$COPY")
		if [ "$mode" = "map-upload-export" ]; then
			# The servers are the stripped export, by the deploy path's own preset: what the box runs.
			mkdir -p "$COPY/build/server"
			if ! "$GODOT" --headless --path "$COPY" --export-release "Linux Server" "$COPY/build/server/gdpyr-server" \
				> "$COPY/$part-export.log" 2>&1 || [ ! -x "$COPY/build/server/gdpyr-server" ]; then
				tail -n 20 "$COPY/$part-export.log" >&2
				echo "$part: the stripped server did not export (are the export templates installed?)"
				failed=1
				continue
			fi
			ls -l "$COPY/build/server/gdpyr-server" | awk '{print "[export] gdpyr-server, pack embedded: " $5 " bytes"}'
			server_cmd=("$COPY/build/server/gdpyr-server" --headless)
		fi
		dir="$COPY/$part"
		rm -rf "${dir:?}"
		mkdir -p "$dir"
		userdir() { echo "$1/.local/share/godot/app_userdata/gdpyr"; }

		# Three maps: the one the servers play at first, the one that is published, one that never arrives whole.
		# shellcheck disable=SC2086
		set -- $UPLOAD_SEEDS
		generated=1
		for spec in "ridge:$1:Ridge" "valley:$2:Valley" "third:$3:Third"; do
			IFS=: read -r file seed title <<< "$spec"
			if ! (cd "$PROJECT_DIR" && dotnet run --project tools/Gdpyr.MapTool -c Release -- generate "$dir/$file.map" \
				--seed "$seed" --name "$title") > "$COPY/$part-maptool-$file.log" 2>&1; then
				cat "$COPY/$part-maptool-$file.log" >&2
				generated=0
			fi
			sed 's/^/[map] /' "$COPY/$part-maptool-$file.log"
		done
		if [ "$generated" -ne 1 ]; then
			echo "$part: the maps were not generated"
			failed=1
			continue
		fi
		ridge=$(sha256sum "$dir/ridge.map" | cut -d' ' -f1)
		valley=$(sha256sum "$dir/valley.map" | cut -d' ' -f1)
		valley_chunks=$(( ($(wc -c < "$dir/valley.map") + 1023) / 1024 ))

		fact() {  # whether it holds, then what it is
			if [ "$1" -eq 0 ]; then
				echo "[checks] PASS $part: $2"
			else
				echo "[checks] FAIL $part: $2"
				failed=1
			fi
		}

		# deploy-map.sh, against a home on this machine: the map validated, copied, named in maps/active.
		deploy() {  # the home, then the script's own arguments
			local home="$1"
			shift
			GDPYR_LOCAL=1 GDPYR_MAP_DIR="$(userdir "$home")/maps" "$PROJECT_DIR/scripts/deploy-map.sh" "$@"
		}

		# A server: its name, its part, its home and its marks, a directory of its own, then its own
		# arguments. What it is told is what the caller set for it; systemd's variable and the
		# supervisor's are taken out of its environment, and the second put back when `supervised` is set.
		server_start() {
			local name="$1" role="$2" home="$3"
			marks="$4"
			shift 4
			mkdir -p "$home" "$marks"
			expect_n=0
			env -u XDG_DATA_HOME -u INVOCATION_ID -u GDPYR_SUPERVISED ${supervised:+GDPYR_SUPERVISED=1} HOME="$home" \
				LE8_CHECK="$role" LE8_LABEL="$name" LE8_DIR="$marks" LE8_TOKEN="$UPLOAD_TOKEN" \
				LE8_RIDGE="$dir/ridge.map" LE8_VALLEY="$dir/valley.map" LE8_THIRD="$dir/third.map" \
				LE8_MAPS="${maps:-}" LE8_PLAYS="${plays:-$ridge}" LE8_FROM="${from:-}" LE8_TAKES="${takes:-0}" \
				timeout 900 "${server_cmd[@]}" -- --server "$GAME_PORT" --no-advertise "$@" \
				> "$COPY/$part-$server_log.log" 2>&1 &
			server=$!
			for _ in $(seq 1 450); do
				[ -e "$marks/server-up" ] && return 0
				kill -0 "$server" 2>/dev/null || break
				sleep 0.2
			done
			fact 1 "$name: the server did not come up"
			return 1
		}

		# What a server's log holds, and how it ended: with the exit expected, and no fact failing.
		server_end() {  # its name, the exit it should end with
			local name="$1" expect="$2" status
			wait "$server"
			status=$?
			report "$COPY/$part-$server_log.log"
			grep -E '^\[map\]|\[maps\]|^gdpyr:' "$COPY/$part-$server_log.log" | sed 's/^WARNING: //'
			[ "$status" -eq "$expect" ]
			fact $? "$name: the server exited $status; $expect expected"
			grep -q '^\[checks\] FAIL' "$COPY/$part-$server_log.log" && failed=1
			grep -q "^\[checks\] $name: .* facts hold" "$COPY/$part-$server_log.log"
			fact $? "$name: the server's part ran to its end"
		}

		# What the server is held to, now: answered by its seen-N.
		expect() {
			expect_n=$((expect_n + 1))
			echo "$*" > "$marks/expect-$expect_n.part"
			mv "$marks/expect-$expect_n.part" "$marks/expect-$expect_n"
			for _ in $(seq 1 150); do
				[ -e "$marks/seen-$expect_n" ] && return 0
				sleep 0.2
			done
			fact 1 "the server did not answer what it was held to: $*"
		}

		# A client: its name, its part and its home, then its own arguments. What it is told is what
		# the caller set for it.
		client_start() {
			local name="$1" role="$2" home="$3"
			shift 3
			mkdir -p "$home"
			env -u XDG_DATA_HOME HOME="$home" LE8_CHECK="$role" LE8_LABEL="$name" LE8_DIR="$marks" LE8_PORT="$GAME_PORT" \
				LE8_TOKEN="$UPLOAD_TOKEN" LE8_RIDGE="$dir/ridge.map" LE8_VALLEY="$dir/valley.map" LE8_THIRD="$dir/third.map" \
				LE8_PLAYS="${plays:-$ridge}" LE8_EXPECT="${answer:-}" LE8_OFFER="${offer:-}" LE8_LEAVES="${leaves:-0}" \
				LE8_STREAM="${stream:-0}" LE8_NEXT="${next:-0}" LE8_WAIT="${wait_for:-}" LE8_AGAIN="${again:-0}" \
				timeout 400 "$GODOT" --headless --path "$COPY" --max-fps 60 -- "$@" > "$COPY/$part-$name.log" 2>&1 &
			client=$!
		}

		client_end() {  # its name, its process
			local name="$1" status
			wait "$2"
			status=$?
			report "$COPY/$part-$name.log"
			grep -E '\[maps\]|^\[editor\] (publishing|published|not published)|^gdpyr:' "$COPY/$part-$name.log" \
				| sed 's/^WARNING: //;s/^ERROR: //'
			[ "$status" -eq 0 ]
			fact $? "$name: the client exited $status"
			grep -q "^\[checks\] $name: .* facts hold" "$COPY/$part-$name.log"
			fact $? "$name: the client's part ran to its end"
		}

		run_client() {
			client_start "$@"
			client_end "$1" "$client"
		}

		# An editor's home with the map to publish in it, as one's own.
		editor_home() {
			mkdir -p "$(userdir "$1")/maps"
			cp "$dir/valley.map" "$(userdir "$1")/maps/valley.map"
		}

		# ---- deploy-map.sh ----------------------------------------------------------------------
		home="$dir/home-hand"
		deploy "$home" "$dir/ridge.map" --activate > "$COPY/$part-deploy.log" 2>&1
		status=$?
		sed 's/^/[deploy] /' "$COPY/$part-deploy.log"
		[ "$status" -eq 0 ] && [ "$(sha256sum "$(userdir "$home")/maps/ridge.map" | cut -d' ' -f1)" = "$ridge" ] \
			&& [ "$(cat "$(userdir "$home")/maps/active")" = "$ridge" ]
		fact $? "deploy-map.sh --activate: exit $status; ridge.map is in the home's user://maps, hashing to $ridge, and maps/active holds that"

		head -c 3000 /dev/urandom > "$dir/noise.map"
		deploy "$home" "$dir/noise.map" --activate > "$COPY/$part-deploy-noise.log" 2>&1
		status=$?
		sed 's/^/[deploy] /' "$COPY/$part-deploy-noise.log"
		[ "$status" -eq 1 ] && [ "$(cat "$(userdir "$home")/maps/active")" = "$ridge" ] \
			&& [ "$(ls "$(userdir "$home")/maps" | tr '\n' ' ')" = "active ridge.map " ]
		fact $? "deploy-map.sh on 3,000 bytes that are not a map: exit $status, nothing copied, maps/active as it was"

		cp "$dir/valley.map" "$dir/Not A Name.map"
		deploy "$home" "$dir/Not A Name.map" > "$COPY/$part-deploy-name.log" 2>&1
		status=$?
		[ "$status" -eq 2 ] && [ "$(ls "$(userdir "$home")/maps" | tr '\n' ' ')" = "active ridge.map " ]
		fact $? "deploy-map.sh on a file whose name is not a map's: exit $status, nothing copied ($(head -n 1 "$COPY/$part-deploy-name.log"))"

		# deploy-map.sh's other path, to a box: ssh, scp, sudo, systemctl and journalctl stood in for by
		# scripts that run the command on this machine and write down what they were asked. What is
		# held is the script's own commands, and their quoting: nothing here is a box, or its unit.
		shims="$dir/shims"
		box="$dir/box/var/lib/gdpyr/.local/share/godot/app_userdata/gdpyr/maps"
		mkdir -p "$shims"
		printf '%s\n' '#!/usr/bin/env bash' 'echo "ssh ${!#}" >> "$SHIM_LOG"' 'exec bash -c "${!#}"' > "$shims/ssh"
		printf '%s\n' '#!/usr/bin/env bash' 'echo "scp ${*: -2}" >> "$SHIM_LOG"' 'target="${!#}"' \
			'exec cp "${@: -2:1}" "${target#*:}"' > "$shims/scp"
		printf '%s\n' '#!/usr/bin/env bash' 'echo "sudo $*" >> "$SHIM_LOG"' '[ "$1" = "-u" ] && shift 2' \
			'args=()' 'while [ $# -gt 0 ]; do case "$1" in -o|-g) shift ;; *) args+=("$1") ;; esac; shift; done' \
			'exec "${args[@]}"' > "$shims/sudo"
		printf '%s\n' '#!/usr/bin/env bash' 'echo "systemctl $*" >> "$SHIM_LOG"' \
			'[ "$1" = "is-active" ] && [ -e "$SHIM_DOWN" ] && exit 3' 'exit 0' > "$shims/systemctl"
		printf '%s\n' '#!/usr/bin/env bash' 'echo "[map] the unit'"'"'s log, stood in for"' > "$shims/journalctl"
		chmod +x "$shims"/*
		PATH="$shims:$PATH" SHIM_LOG="$dir/shims.log" SHIM_DOWN="$dir/unit-down" GDPYR_HOST=ubuntu@box.example \
			GDPYR_SSH_KEY="$dir/no-key.pem" GDPYR_MAP_DIR="$box" "$PROJECT_DIR/scripts/deploy-map.sh" "$dir/ridge.map" --activate \
			> "$COPY/$part-deploy-box.log" 2>&1
		status=$?
		sed 's/^/[deploy] /' "$COPY/$part-deploy-box.log"
		[ "$status" -eq 0 ] && [ "$(sha256sum "$box/ridge.map" | cut -d' ' -f1)" = "$ridge" ] && [ "$(cat "$box/active")" = "$ridge" ] \
			&& [ "$(ls "$box" | tr '\n' ' ')" = "active ridge.map " ] \
			&& grep -q '^sudo -u gdpyr mkdir -p' "$dir/shims.log" && grep -q '^sudo install -m 644 -o gdpyr -g gdpyr' "$dir/shims.log" \
			&& grep -q '^sudo systemctl restart gdpyr-server' "$dir/shims.log" && grep -q '^systemctl is-active --quiet gdpyr-server' "$dir/shims.log"
		fact $? "deploy-map.sh --activate to a box, its commands stood in for: exit $status; the map and maps/active are in the unit's directory, made and written as its user, and the unit was restarted and asked whether it is up"

		touch "$dir/unit-down"
		PATH="$shims:$PATH" SHIM_LOG="$dir/shims.log" SHIM_DOWN="$dir/unit-down" GDPYR_HOST=ubuntu@box.example \
			GDPYR_SSH_KEY="$dir/no-key.pem" GDPYR_MAP_DIR="$box" "$PROJECT_DIR/scripts/deploy-map.sh" "$dir/ridge.map" --activate \
			> "$COPY/$part-deploy-box-down.log" 2>&1
		status=$?
		[ "$status" -eq 1 ] && grep -q 'is not running after the map was activated' "$COPY/$part-deploy-box-down.log"
		fact $? "and with the unit not up after the restart: exit $status, saying so"

		# ---- a server run by hand, with a token -----------------------------------------------------
		server_log=hand
		maps="$(userdir "$home")/maps" plays="$ridge" from="$(userdir "$home")/maps/ridge.map" takes=1 supervised="" \
			server_start "server, by hand" server "$home" "$dir/marks-hand" --no-bots --map-upload-token "$UPLOAD_TOKEN"
		if [ $? -eq 0 ]; then
			run_client answers answers "$dir/home-answers" --edit
			expect "offers=14 nexts=5 dropped=3 kept=0 refused=2 last_offer=busy last_next=bad_proof cache=none active=$ridge next=none uploading=nobody"

			client_start busy busy "$dir/home-busy" --edit
			busy=$client
			run_client slow slow "$dir/home-slow" --edit
			client_end busy "$busy"
			expect "offers=17 kept=1 refused=2 last_offer=have_it cache=$valley active=$ridge next=none uploading=nobody leaving=no"

			answer=ok_stays_down leaves=1 run_client next-by-hand next "$dir/home-next" --edit
		fi
		server_end "server, by hand" 0
		[ "$(cat "$(userdir "$home")/maps/active")" = "$valley" ]
		fact $? "after it left, the home's maps/active holds $(cat "$(userdir "$home")/maps/active")"
		grep -q 'nothing here starts this server again' "$COPY/$part-hand.log"
		fact $? "and it said that nothing there starts it again"

		# ---- a server with no token ------------------------------------------------------------------
		home="$dir/home-off"
		editor_home "$dir/home-editor"
		server_log=off
		maps="" plays="$ridge" from="$dir/ridge.map" takes=0 supervised="" \
			server_start "server, no token" server "$home" "$dir/marks-off" --no-bots --map "$dir/ridge.map"
		if [ $? -eq 0 ]; then
			answer="uploads_off:-" next=1 run_client publish-off publish "$dir/home-editor" --edit valley
			answer=uploads_off offer=uploads_off stream=1 run_client stream-off next "$dir/home-stream" --edit
			expect "offers=2 nexts=1 dropped=$valley_chunks kept=0 refused=0 last_offer=uploads_off last_next=uploads_off cache=none active=none next=none uploading=nobody"
			touch "$marks/stop"
		fi
		server_end "server, no token" 0

		# ---- a server started with --map, and a token -----------------------------------------------
		home="$dir/home-pinned"
		server_log=pinned
		maps="" plays="$ridge" from="$dir/ridge.map" takes=1 supervised="" \
			server_start "server, --map and a token" server "$home" "$dir/marks-pinned" --no-bots --map "$dir/ridge.map" \
			--map-upload-token "$UPLOAD_TOKEN"
		if [ $? -eq 0 ]; then
			answer="ok:pinned" next=1 run_client publish-pinned publish "$dir/home-editor" --edit valley
			expect "offers=1 nexts=1 kept=1 refused=0 last_next=pinned cache=$valley active=none next=none leaving=no"
			touch "$marks/stop"
		fi
		server_end "server, --map and a token" 0

		# ---- a server with a round, supervised: told its next map, it plays the round out ----------
		home="$dir/home-round"
		deploy "$home" "$dir/ridge.map" --activate > "$COPY/$part-deploy-round.log" 2>&1
		fact $? "deploy-map.sh --activate, for the server with a round"
		editor_home "$dir/home-editor-2"
		server_log=round
		maps="$(userdir "$home")/maps" plays="$ridge" from="$(userdir "$home")/maps/ridge.map" takes=1 supervised=1 \
			server_start "server, supervised" server-round "$home" "$dir/marks-round" --map-upload-token "$UPLOAD_TOKEN"
		if [ $? -eq 0 ]; then
			plays="$ridge" client_start player player "$dir/home-player" --client "127.0.0.1:$GAME_PORT"
			player=$client
			answer="ok:ok" next=1 again=1 wait_for=round-live run_client publish publish "$dir/home-editor-2" --edit valley
			client_end player "$player"
		fi
		server_end "server, supervised" 0
		grep -h '^\[match\] round' "$COPY/$part-round.log"

		# Its supervisor's part: the same command again. It plays the new map, from its cache.
		server_log=again
		maps="$(userdir "$home")/maps" plays="$valley" from=cache takes=1 supervised=1 \
			server_start "server, started again" server "$home" "$dir/marks-again" --map-upload-token "$UPLOAD_TOKEN"
		if [ $? -eq 0 ]; then
			# Everybody joins again: a client that has not got the map fetches it, as LE7's does.
			mkdir -p "$dir/home-rejoin"
			env -u XDG_DATA_HOME HOME="$dir/home-rejoin" LE7_CHECK=fetch LE7_DIR="$marks" LE7_HASH="$valley" timeout 300 \
				"$GODOT" --headless --path "$COPY" --max-fps 60 -- --client "127.0.0.1:$GAME_PORT" > "$COPY/$part-rejoin.log" 2>&1
			status=$?
			report "$COPY/$part-rejoin.log"
			[ "$status" -eq 0 ]
			fact $? "rejoin: a client that has not got the new map joined again: exit $status"
			expect "offers=0 nexts=0 kept=0 cache=$valley active=$valley next=none leaving=no"
			touch "$marks/stop"
		fi
		server_end "server, started again" 0
		;;

	map-shot)
		# Drawn, with the Compatibility renderer, in a virtual display: once as the file is, once
		# with the basin dug. For a person to look at; the facts are only that it drew.
		ground slab
		out="${SHOT_OUT:-$COPY/shots}"
		for basin in "" "${BASIN:--20,-75,12,3}"; do
			name="map-shot${basin:+-basin}"
			mkdir -p "$out/$name"
			LE3_CHECK=shot LE3_DIR="$out/$name" LE3_MAP="$MAP" LE3_BASIN="$basin" timeout 900 \
				xvfb-run -a -s "-screen 0 1280x720x24" "$GODOT" --path "$COPY" --rendering-driver opengl3 \
				--resolution 1280x720 -- --practice --map "$MAP" > "$COPY/$name.log" 2>&1
			status=$?
			report "$COPY/$name.log"
			grep -m 1 '^OpenGL API' "$COPY/$name.log"
			echo "$name, $MAP: exit $status (pictures: $out/$name)"
			[ "$status" -ne 0 ] && failed=1
		done
		;;

	*)
		echo "no such part: $mode" >&2
		failed=1
		;;
	esac
done

[ "${KEEP:-0}" = "1" ] && echo "kept $COPY"
exit "$failed"
