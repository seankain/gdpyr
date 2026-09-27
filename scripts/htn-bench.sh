#!/usr/bin/env bash
#
# Check what FluidHTN does, and measure what it costs on the server tick
# (docs/HTN_BOTS.md §3.4, §3.5).
#
#   ./scripts/htn-bench.sh --probe             # the planner behaviours the plan relies on; exit 1 on a failure
#   ./scripts/htn-bench.sh                     # cost with the pooled factory: must allocate 0 bytes
#   ./scripts/htn-bench.sh --default-factory   # FluidHTN's own factory, for comparison
#
# Runs against the vendored copy at ThirdParty/FluidHTN and the game's own
# PooledHtnFactory. Bumping the pinned commit is: replace that folder, then run
# `dotnet test` in Tests/ (HtnPlannerTests holds P1-P8) and this script.
#
# Requires the .NET 8 SDK. No Godot.
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_DIR"

dotnet run -c Release --project tools/Gdpyr.HtnBench -- "$@"
