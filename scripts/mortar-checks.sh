#!/usr/bin/env bash
#
# The mortar launcher's engine check (Scripts/Fps/MortarLauncher.cs).
#
# A listen host on Test.tscn at --fixed-fps 60, its own player through the input map:
# the launcher carried, put down and mounted; a tank 60 m off destroyed by the third of
# three shells and not the second; the tube fed by a can within a metre of it and not by
# one 1.5 m off. Headless, on a copy of the tree with tools/EngineChecks/MortarChecks.cs
# added as an autoload: the game itself never compiles or loads it.
#
#   ./scripts/mortar-checks.sh
#
# Requires a Godot 4.6 .NET binary on PATH (or $GODOT) and the .NET SDK. One line per
# fact, PASS or FAIL; exits non-zero when any fails. KEEP=1 keeps the copy and its log.
set -uo pipefail

GODOT="${GODOT:-godot}"
GAME_PORT="${GAME_PORT:-7794}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-mortar-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/MortarChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nMortarChecks="*res://EngineChecks/MortarChecks.cs"#' \
	"$COPY/project.godot"

if ! (cd "$COPY" && dotnet build Gdpyr.csproj -nologo -v quiet > "$COPY/build.log" 2>&1); then
	cat "$COPY/build.log" >&2
	echo "error: the copy did not build" >&2
	exit 2
fi

if [ ! -d "$COPY/.godot/imported" ]; then
	"$GODOT" --headless --path "$COPY" --import > "$COPY/import.log" 2>&1
fi

MORTAR_CHECK=listen timeout 900 "$GODOT" --headless --path "$COPY" --fixed-fps 60 \
	-- --listen "$GAME_PORT" --no-bots > "$COPY/mortar.log" 2>&1
status=$?

grep '^\[checks\]' "$COPY/mortar.log"
if [ "$status" -ne 0 ]; then
	echo "mortar: exit $status (log: $COPY/mortar.log${KEEP:+, kept})"
fi
exit "$status"
