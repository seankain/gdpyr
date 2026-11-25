#!/usr/bin/env bash
#
# The viewmodel engine check (Scripts/Fps/AnimatedViewmodel.cs, docs/VIEWMODELS.md §5).
#
# A listen host on Test.tscn at --fixed-fps 60, its own player given the launcher and driven
# through the input map: the launcher is drawn by its glTF viewmodel and not its mesh, at its
# own field of view and without a shadow; it idles, kicks when a round leaves and settles; the
# next pull reloads it, with the barrel open part way through and shut on a round at the end;
# and the muzzle flash sits on the barrel as it is drawn. Headless, on a copy of the tree with
# tools/EngineChecks/ViewmodelChecks.cs added as an autoload: the game itself never compiles or
# loads it.
#
#   ./scripts/viewmodel-checks.sh
#   SHOTS=/some/dir ./scripts/viewmodel-checks.sh   # on a display: also save screenshots
#
# With SHOTS, the copy is run on a display rather than headless — $DISPLAY, or xvfb-run when
# there is none — with the Compatibility renderer, and photographs the hip, the shot, six points
# of the reload, the sights, and the body the camera looks out of drawn from the eye (the
# comparison in docs/VIEWMODELS.md §2). Requires a Godot 4.6 .NET binary on PATH (or $GODOT) and
# the .NET SDK. One line per fact, PASS or FAIL; exits non-zero when any fails. KEEP=1 keeps the
# copy and its log.
set -uo pipefail

GODOT="${GODOT:-godot}"
GAME_PORT="${GAME_PORT:-7797}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v "$GODOT" >/dev/null 2>&1 && [ ! -x "$GODOT" ]; then
	echo "error: Godot not found. Set GODOT to your Godot .NET binary." >&2
	exit 2
fi

COPY="$(mktemp -d "${TMPDIR:-/tmp}/gdpyr-viewmodel-checks.XXXXXX")"
if [ "${KEEP:-0}" != "1" ]; then
	trap 'rm -rf "$COPY"' EXIT
fi

(cd "$PROJECT_DIR" && tar --exclude=./.git --exclude=./build --exclude='./*/bin' --exclude='./*/obj' \
	--exclude='./tools/*/bin' --exclude='./tools/*/obj' -cf - .) | (cd "$COPY" && tar -xf -)

mkdir -p "$COPY/EngineChecks"
cp "$PROJECT_DIR/tools/EngineChecks/ViewmodelChecks.cs" "$COPY/EngineChecks/"
sed -i 's#^GameConsole="\*res://Scripts/Ui/GameConsole.cs"#&\nViewmodelChecks="*res://EngineChecks/ViewmodelChecks.cs"#' \
	"$COPY/project.godot"

if ! (cd "$COPY" && dotnet build Gdpyr.csproj -nologo -v quiet > "$COPY/build.log" 2>&1); then
	cat "$COPY/build.log" >&2
	echo "error: the copy did not build" >&2
	exit 2
fi

if [ ! -d "$COPY/.godot/imported" ]; then
	"$GODOT" --headless --path "$COPY" --import > "$COPY/import.log" 2>&1
fi

RUN=("$GODOT" --headless)
if [ -n "${SHOTS:-}" ]; then
	mkdir -p "$SHOTS"
	SHOTS="$(cd "$SHOTS" && pwd)"
	RUN=("$GODOT" --rendering-method gl_compatibility --rendering-driver opengl3 --resolution 1280x720)
	if [ -z "${DISPLAY:-}" ]; then
		RUN=(xvfb-run -a -s "-screen 0 1280x720x24" "${RUN[@]}")
	fi
fi

VIEWMODEL_CHECK=listen VIEWMODEL_SHOTS="${SHOTS:-}" timeout 900 "${RUN[@]}" --path "$COPY" --fixed-fps 60 \
	-- --listen "$GAME_PORT" --no-bots > "$COPY/viewmodel.log" 2>&1
status=$?

grep '^\[checks\]' "$COPY/viewmodel.log"
if [ "$status" -ne 0 ]; then
	echo "viewmodel: exit $status (log: $COPY/viewmodel.log${KEEP:+, kept})"
fi
exit "$status"
