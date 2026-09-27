#!/usr/bin/env bash
#
# Check what FluidHTN does, and measure what it costs on the server tick
# (docs/HTN_BOTS.md §3.4, §3.5).
#
#   ./scripts/htn-bench.sh --probe             # the planner behaviours the plan relies on; exit 1 on a failure
#   ./scripts/htn-bench.sh                     # cost with the pooled factory: must allocate 0 bytes
#   ./scripts/htn-bench.sh --default-factory   # FluidHTN's own factory, for comparison
#
# Uses the vendored copy at ThirdParty/FluidHTN when it exists. Until it does, the
# pinned commit is fetched into the user's cache so the numbers the plan quotes can
# be re-taken without anything being vendored first. Not into build/: Gdpyr.csproj
# compiles every .cs file under the project directory, ignored or not, and a
# checkout there would be built into the game assembly.
#
# Requires the .NET 8 SDK. No Godot.
set -euo pipefail

FLUID_REPO="https://github.com/ptrefall/fluid-hierarchical-task-network.git"
FLUID_COMMIT="e67af26"

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_DIR"

SOURCE="$PROJECT_DIR/ThirdParty/FluidHTN"
if [ ! -d "$SOURCE" ]; then
	CHECKOUT="${XDG_CACHE_HOME:-$HOME/.cache}/gdpyr/fluid-htn"
	if [ ! -d "$CHECKOUT/.git" ]; then
		git clone --quiet "$FLUID_REPO" "$CHECKOUT"
	fi
	git -C "$CHECKOUT" checkout --quiet "$FLUID_COMMIT"
	SOURCE="$CHECKOUT/Fluid-HTN"
fi

dotnet run -c Release --project tools/Gdpyr.HtnBench -p:FluidHtnSource="$SOURCE" -- "$@"
