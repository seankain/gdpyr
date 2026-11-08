#!/usr/bin/env bash
#
# The weapon audio engine check (Scripts/Effects/WeaponAudio.cs, docs/AUDIO.md §5).
#
# A listen host on Test.tscn at --fixed-fps 60, its own player through the input map: their
# own rifle heard once a round at the ear and never going past them; rounds fired in nobody's
# name heard at the muzzle, and heard going past once where they came closest — or not, by
# how near they came, how far they had flown and what they were; a hand grenade not heard as
# a shot; every player taken back; and the console's `sound`. Headless, on a copy of the tree
# with tools/EngineChecks/WeaponAudioChecks.cs added as an autoload: the game itself never
# compiles or loads it. Headless audio mixes into nothing, so this says when sounds are
# played and where, not what they sound like.
#
#   ./scripts/weapon-audio-checks.sh
#
# Requires a Godot 4.6 .NET binary on PATH (or $GODOT) and the .NET SDK. One line per
# fact, PASS or FAIL; exits non-zero when any fails. KEEP=1 keeps the copy and its log.
set -uo pipefail

GODOT="${GODOT:-godot}"
GAME_PORT="${GAME_PORT:-7795}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-weapon-audio-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/WeaponAudioChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nWeaponAudioChecks="*res://EngineChecks/WeaponAudioChecks.cs"#' \
	"$COPY/project.godot"

if ! (cd "$COPY" && dotnet build Gdpyr.csproj -nologo -v quiet > "$COPY/build.log" 2>&1); then
	cat "$COPY/build.log" >&2
	echo "error: the copy did not build" >&2
	exit 2
fi

if [ ! -d "$COPY/.godot/imported" ]; then
	"$GODOT" --headless --path "$COPY" --import > "$COPY/import.log" 2>&1
fi

WEAPON_AUDIO_CHECK=listen timeout 900 "$GODOT" --headless --path "$COPY" --fixed-fps 60 \
	-- --listen "$GAME_PORT" --no-bots > "$COPY/weapon-audio.log" 2>&1
status=$?

grep '^\[checks\]' "$COPY/weapon-audio.log"
if [ "$status" -ne 0 ]; then
	echo "weapon audio: exit $status (log: $COPY/weapon-audio.log${KEEP:+, kept})"
fi
exit "$status"
