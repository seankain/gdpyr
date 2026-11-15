#!/usr/bin/env bash
#
# Copy a map to the dedicated server's own maps, and with --activate make it the map the server
# plays (docs/LEVEL_EDITOR.md §9.5).
#
#   export GDPYR_HOST=ubuntu@<elastic-ip>
#   export GDPYR_SSH_KEY=~/.ssh/gdpyr.pem
#   ./scripts/deploy-map.sh ridge.map                # copy it: the server has it, and plays what it played
#   ./scripts/deploy-map.sh ridge.map --activate     # copy it, name it in maps/active, restart the unit
#   ./scripts/deploy-map.sh "My Map.map" --name my-map --activate
#
# The file is checked first, by tools/Gdpyr.MapTool: §6.8's reader and §6.9's validator. A file
# that is not a map, or does not play, is not copied. It goes to the server's user://maps under
# its own name less .map, or --name: a-z, 0-9, _ and -, at most 48, as a map's name in the library
# is (§9.1). A map is the same map under any name: the server finds it by its hash.
#
# --activate writes the map's SHA-256 to maps/active, which a server started with no --map plays
# (the deploy unit is), and restarts the unit: everybody on the server is dropped, and joins again.
# The script fails loudly if the service does not come back: a maps/active that names a map the
# server cannot play is a fatal start-up error there, as a bad --map is.
#
#   GDPYR_MAP_DIR     the server's user://maps. The unit runs with HOME=/var/lib/gdpyr, so by
#                     Godot's default: /var/lib/gdpyr/.local/share/godot/app_userdata/gdpyr/maps
#                     The server prints the one it resolved when it starts: "[map] this server's maps are in".
#   GDPYR_MAP_USER    who owns it: the unit's user, gdpyr
#   GDPYR_SERVICE     the unit, gdpyr-server
#   GDPYR_MAPTOOL     the command that validates, in place of `dotnet run --project tools/Gdpyr.MapTool`
#   GDPYR_LOCAL=1     the server is run by hand on this machine: no ssh, no sudo, and no restart — with
#                     --activate, the server plays the map the next time somebody starts it
#
# See docs/DEPLOYMENT.md for the box and its unit.
#
set -euo pipefail

usage() {
	echo "usage: $0 <file.map> [--activate] [--name <name>]" >&2
	exit 2
}

FILE=""
NAME=""
ACTIVATE=0
while [ $# -gt 0 ]; do
	case "$1" in
	--activate) ACTIVATE=1 ;;
	--name)
		[ $# -ge 2 ] || usage
		NAME="$2"
		shift
		;;
	-*) usage ;;
	*)
		[ -z "$FILE" ] || usage
		FILE="$1"
		;;
	esac
	shift
done
[ -n "$FILE" ] || usage

if [ ! -f "$FILE" ]; then
	echo "error: there is no file $FILE" >&2
	exit 2
fi

LOCAL="${GDPYR_LOCAL:-0}"
MAP_DIR="${GDPYR_MAP_DIR:-/var/lib/gdpyr/.local/share/godot/app_userdata/gdpyr/maps}"
MAP_USER="${GDPYR_MAP_USER:-gdpyr}"
SERVICE="${GDPYR_SERVICE:-gdpyr-server}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# The name it is kept under: the library's rule (MapNames.TryParseName), so that the server lists it.
if [ -z "$NAME" ]; then
	NAME="$(basename "$FILE")"
	NAME="${NAME%.map}"
fi
if ! [[ "$NAME" =~ ^[a-z0-9_][a-z0-9_-]{0,47}$ ]]; then
	echo "error: '$NAME' is not a map's name: a-z, 0-9, _ and -, at most 48, not starting with '-'." >&2
	echo "       Give one with --name." >&2
	exit 2
fi

# §6.8 and §6.9, before anything is copied.
echo ">> validating $FILE"
if [ -n "${GDPYR_MAPTOOL:-}" ]; then
	# shellcheck disable=SC2086
	$GDPYR_MAPTOOL validate "$FILE" || {
		echo "error: $FILE is not a map that plays: nothing was copied" >&2
		exit 1
	}
else
	(cd "$PROJECT_DIR" && dotnet run --project tools/Gdpyr.MapTool -c Release -- validate "$(realpath "$FILE")") || {
		echo "error: $FILE is not a map that plays: nothing was copied" >&2
		exit 1
	}
fi

HASH="$(sha256sum "$FILE" | cut -d' ' -f1)"
BYTES="$(wc -c < "$FILE" | tr -d ' ')"
TARGET="$MAP_DIR/$NAME.map"

if [ "$LOCAL" = "1" ]; then
	on_box() { bash -c "$1"; }
	AS_OWNER=""
	AS_ROOT=""
	STAGED="$FILE"
else
	: "${GDPYR_HOST:?set GDPYR_HOST=ubuntu@<elastic-ip>, or GDPYR_LOCAL=1 for a server on this machine}"
	SSH_KEY="${GDPYR_SSH_KEY:-$HOME/.ssh/gdpyr.pem}"
	SSH=(ssh -i "$SSH_KEY" -o StrictHostKeyChecking=accept-new "$GDPYR_HOST")
	on_box() { "${SSH[@]}" "$1"; }
	AS_OWNER="sudo -u $MAP_USER "
	AS_ROOT="sudo "
	STAGED="/tmp/gdpyr-map-$HASH.map"
	echo ">> uploading $FILE ($BYTES bytes) -> $GDPYR_HOST"
	scp -q -i "$SSH_KEY" -o StrictHostKeyChecking=accept-new "$FILE" "$GDPYR_HOST:$STAGED"
fi

# Through a file beside it and a rename, so the name is only ever the whole map: a .tmp is not
# listed. The directory is the unit's user's, made as that user where the server has not made it.
echo ">> $TARGET"
on_box "set -e
${AS_OWNER}mkdir -p '$MAP_DIR'
${AS_ROOT}install -m 644 ${AS_ROOT:+-o $MAP_USER -g $MAP_USER }'$STAGED' '$TARGET.tmp'
${AS_ROOT}mv -f '$TARGET.tmp' '$TARGET'"
if [ "$LOCAL" != "1" ]; then
	on_box "rm -f '$STAGED'"
fi

THERE="$(on_box "${AS_ROOT}sha256sum '$TARGET'" | cut -d' ' -f1)"
if [ "$THERE" != "$HASH" ]; then
	echo "error: $TARGET hashes to $THERE there, not $HASH: the copy is not the file" >&2
	exit 1
fi
echo ">> copied: $NAME.map, $BYTES bytes, sha-256 $HASH"

if [ "$ACTIVATE" != "1" ]; then
	echo ">> not activated: the server plays what it played. To play this map: $0 $FILE --activate"
	exit 0
fi

echo ">> $MAP_DIR/active names $HASH"
on_box "set -e
printf '%s\n' '$HASH' | ${AS_OWNER}tee '$MAP_DIR/active.tmp' > /dev/null
${AS_ROOT}mv -f '$MAP_DIR/active.tmp' '$MAP_DIR/active'"

if [ "$LOCAL" = "1" ]; then
	echo ">> a server started on this machine with no --map plays it; nothing here was restarted"
	exit 0
fi

echo ">> restarting $SERVICE"
on_box "sudo systemctl restart $SERVICE"

# systemd reports the restart as successful before the process has had a chance to refuse the
# map, so give it a moment and check again, as deploy.sh does.
sleep 3
if ! on_box "systemctl is-active --quiet $SERVICE"; then
	echo "error: $SERVICE is not running after the map was activated. Last 40 log lines:" >&2
	on_box "journalctl -u $SERVICE -n 40 --no-pager" >&2
	exit 1
fi

echo ">> $SERVICE is up"
on_box "journalctl -u $SERVICE -n 40 --no-pager | grep -E '\[map\]|\[boot\]|\[net\] listening' | tail -n 6" || true
