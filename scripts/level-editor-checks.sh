#!/usr/bin/env bash
#
# The level editor's engine checks (docs/LEVEL_EDITOR.md §11). LE0: the baseline and the spike; LE3: the world from a file.
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
LE3_SECONDS="${LE3_SECONDS:-600}"
FRAME_SECONDS="${FRAME_SECONDS:-300}"
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
	"$PROJECT_DIR/tools/EngineChecks/LevelMapChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nLevelChecks="*res://EngineChecks/LevelChecks.cs"\nLevelMapChecks="*res://EngineChecks/LevelMapChecks.cs"#' \
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

	*)
		echo "no such part: $mode" >&2
		failed=1
		;;
	esac
done

[ "${KEEP:-0}" = "1" ] && echo "kept $COPY"
exit "$failed"
