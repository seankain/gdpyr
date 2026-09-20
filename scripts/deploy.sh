#!/usr/bin/env bash
#
# Upload build/server/ to the EC2 box and restart the dedicated server.
#
#   export GDPYR_HOST=ubuntu@<elastic-ip>
#   export GDPYR_SSH_KEY=~/.ssh/gdpyr.pem
#   ./scripts/export-server.sh && ./scripts/deploy.sh
#
# See docs/DEPLOYMENT.md for the one-time box setup and the systemd unit.
#
set -euo pipefail

: "${GDPYR_HOST:?set GDPYR_HOST=ubuntu@<elastic-ip>}"
SSH_KEY="${GDPYR_SSH_KEY:-$HOME/.ssh/gdpyr.pem}"
REMOTE_DIR="${GDPYR_REMOTE_DIR:-/opt/gdpyr}"
SERVICE="${GDPYR_SERVICE:-gdpyr-server}"
OUT_DIR="${OUT_DIR:-build/server}"

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_DIR"

if [ ! -d "$OUT_DIR" ]; then
	echo "error: $OUT_DIR does not exist. Run ./scripts/export-server.sh first." >&2
	exit 1
fi

SSH=(ssh -i "$SSH_KEY" -o StrictHostKeyChecking=accept-new "$GDPYR_HOST")

echo ">> uploading $OUT_DIR -> $GDPYR_HOST:$REMOTE_DIR"
# --delete keeps stale .NET assemblies from a previous build out of the way.
# REMOTE_DIR is expected to hold nothing but this build.
rsync -az --delete \
	-e "ssh -i $SSH_KEY -o StrictHostKeyChecking=accept-new" \
	"$OUT_DIR/" "$GDPYR_HOST:$REMOTE_DIR/"

echo ">> restarting $SERVICE"
"${SSH[@]}" "sudo systemctl restart $SERVICE"

# systemd reports the restart as successful before the process has had a chance
# to fail on a bad build, so give it a moment and check again.
sleep 3
if ! "${SSH[@]}" "systemctl is-active --quiet $SERVICE"; then
	echo "error: $SERVICE is not running after deploy. Last 40 log lines:" >&2
	"${SSH[@]}" "journalctl -u $SERVICE -n 40 --no-pager" >&2
	exit 1
fi

echo ">> $SERVICE is up"
"${SSH[@]}" "journalctl -u $SERVICE -n 15 --no-pager"
