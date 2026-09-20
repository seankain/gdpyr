#!/usr/bin/env bash
#
# Export the headless dedicated-server build to build/server/.
#
# Requires a Godot .NET editor binary on PATH (or $GODOT) and an export preset
# named "Linux Server" configured as described in docs/DEPLOYMENT.md.
#
#   GODOT=/opt/godot/Godot_mono ./scripts/export-server.sh
#
set -euo pipefail

GODOT="${GODOT:-godot}"
PRESET="${PRESET:-Linux Server}"
OUT_DIR="${OUT_DIR:-build/server}"
BIN_NAME="${BIN_NAME:-gdpyr-server}"

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_DIR"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 1
fi

# A fresh checkout has no .godot/ import cache; exporting without it fails on
# missing resources. Importing is idempotent and cheap once warm.
echo ">> importing assets"
"$GODOT" --headless --path "$PROJECT_DIR" --import

echo ">> exporting preset '$PRESET'"
rm -rf "${OUT_DIR:?}"
mkdir -p "$OUT_DIR"
"$GODOT" --headless --path "$PROJECT_DIR" \
	--export-release "$PRESET" "$PROJECT_DIR/$OUT_DIR/$BIN_NAME"

chmod +x "$OUT_DIR/$BIN_NAME"

# A C# export is the executable plus a data_* directory holding the .NET
# assemblies. Both have to ship; catch a partial export here rather than on the
# server.
if ! compgen -G "$OUT_DIR/data_*" >/dev/null; then
	echo "error: no data_* directory in $OUT_DIR — the .NET assemblies are missing." >&2
	echo "       Check that the export preset targets the .NET build of Godot." >&2
	exit 1
fi

echo ">> done:"
du -sh "$OUT_DIR"
ls -la "$OUT_DIR"
