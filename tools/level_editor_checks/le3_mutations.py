#!/usr/bin/env python3
"""LE3's engine-free rules, each held by a test that fails when the rule is broken: LE-D21's ground
under a structure, LE-D25's threshold, --map and --practice, a map's name and what a reference names
(docs/LEVEL_EDITOR.md §7.5, §9.1, §9.5, §9.6), the bundled map's hash, and a scenario's map.

    python3 tools/level_editor_checks/le3_mutations.py              # every rule, three at a time
    python3 tools/level_editor_checks/le3_mutations.py relief map   # the ones whose name holds a word
    python3 tools/level_editor_checks/le3_mutations.py --jobs 1

le2_mutations.py's way: it copies what the tests compile and read to a scratch directory, and there,
for each rule below, breaks the rule -- one edit to one file -- and runs the test classes LE3 added or
changed. One line a rule: PASS with the tests that failed, or FAIL when every test still passed, which
is a claim nothing holds. The tree it was started from is not written to.

A rule's edit is a piece of the source as it stands and what replaces it. A piece that is no longer
in the file once, exactly, stops the run: the source has moved and the list wants bringing along.

The standard library and `dotnet`; nothing else.
"""
import concurrent.futures
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CLASSES = ("StructureGroundTests", "PredictionLedgerTests", "MapLaunchTests", "BundledMapTests", "MapCatalogTests",
           "ScenarioTests", "LaunchOptionsTests", "StructureTests")
FILTER = "|".join(f"FullyQualifiedName~Gdpyr.Tests.{name}" for name in CLASSES)

STRUCTURES = "Scripts/Sim/Structures.cs"
LAUNCH = "Scripts/Core/LaunchOptions.cs"
NAMES = "Scripts/Sim/Level/MapNames.cs"

# (name, file, the rule as broken, the source as it stands, what it is replaced with)
RULES = [
    # ---- LE-D21: the ground under a structure (§7.5) ---------------------------------------------
    ("relief-limit", STRUCTURES, "a metre of relief taken where half a metre is the limit",
     "if (highest - lowest > MaxGroundReliefMeters)", "if (highest - lowest > MaxGroundReliefMeters * 2f)"),
    ("relief-not-found", STRUCTURES, "a corner whose ground was not found taken as no relief",
     "if (!float.IsFinite(height))\n\t\t\t{\n\t\t\t\treturn PlacementResult.TooSteep;",
     "if (!float.IsFinite(height))\n\t\t\t{\n\t\t\t\tcontinue;"),
    ("relief-nothing", STRUCTURES, "no heights at all taken",
     "if (heights.Length == 0)\n\t\t{\n\t\t\treturn PlacementResult.TooSteep;",
     "if (heights.Length == 0)\n\t\t{\n\t\t\treturn PlacementResult.Ok;"),
    ("relief-base", STRUCTURES, "a structure stood on the lowest of its ground, not the highest",
     "baseHeight = highest;", "baseHeight = lowest;"),
    ("relief-centre", STRUCTURES, "the centre's ground left out of the relief",
     "foreach (float height in heights)", "foreach (float height in heights[1..])"),
    ("relief-corner", STRUCTURES, "one corner sampled twice and another not at all",
     "points[3] = footprint.Center + across + along;", "points[3] = footprint.Center + across - along;"),
    ("relief-window", STRUCTURES, "corners looked for only as far as the limit, where a corner on it is not found",
     "public const float GroundWindowMeters = MaxGroundReliefMeters + 0.05f;",
     "public const float GroundWindowMeters = MaxGroundReliefMeters;"),
    ("plinth-depth", STRUCTURES, "a plinth half as deep",
     "new StructureBox(new Vector3(body.Center.X, -MaxGroundReliefMeters * 0.5f, body.Center.Z),\n\t\t\t\tnew Vector3(body.HalfExtents.X, MaxGroundReliefMeters * 0.5f, body.HalfExtents.Z));",
     "new StructureBox(new Vector3(body.Center.X, -MaxGroundReliefMeters * 0.25f, body.Center.Z),\n\t\t\t\tnew Vector3(body.HalfExtents.X, MaxGroundReliefMeters * 0.25f, body.HalfExtents.Z));"),
    ("too-steep-number", STRUCTURES, "TooSteep put where another result's number is",
     "TooSteep = 11,", "TooSteep = 12,"),
    ("too-steep-words", STRUCTURES, "TooSteep described as something in the way",
     "PlacementResult.TooSteep => \"the ground is too steep\",", "PlacementResult.TooSteep => \"something is in the way\","),

    # ---- LE-D25 ----------------------------------------------------------------------------------
    ("threshold", "Scripts/Sim/SimConfig.cs", "a correction applied from 1 cm",
     "public const float MispredictionThreshold = 0.02f;", "public const float MispredictionThreshold = 0.01f;"),

    # ---- --practice and --map (§8.7, §9.5) -------------------------------------------------------
    ("practice-flag", LAUNCH, "--practice parsed and forgotten",
     "\t\t\tPractice = practice,\n", "\t\t\tPractice = false,\n"),
    ("practice-mode", LAUNCH, "--practice taken beside a mode flag",
     "if (mode is { } other)\n\t\t\t{\n\t\t\t\treturn Failure($\"conflicting mode flags: --practice and {other}\");",
     "if (false && mode is { } other)\n\t\t\t{\n\t\t\t\treturn Failure($\"conflicting mode flags: --practice and {other}\");"),
    ("practice-demo", LAUNCH, "--practice taken beside --playdemo",
     "return Failure(\"--practice and --playdemo: a demo is watched, not played\");",
     "playDemo = playDemo;"),
    ("practice-dedicated", LAUNCH, "--practice taken by a dedicated server",
     "return Failure(\"--practice needs somebody to play: a dedicated server has no window; use --server\");",
     "dedicatedServer = false;"),
    ("map-client", LAUNCH, "--map taken by a client",
     "if (mode == LaunchMode.Client)\n\t\t\t{\n\t\t\t\treturn Failure(\"--map: a client",
     "if (false)\n\t\t\t{\n\t\t\t\treturn Failure(\"--map: a client"),
    ("map-no-mode", LAUNCH, "--map taken with no mode to play it in",
     "if (mode == LaunchMode.Offline && !practice)", "if (false)"),
    ("map-twice", LAUNCH, "--map given twice, the second taken",
     "\t\t\t\t\tif (mapReference != null)\n\t\t\t\t\t{\n\t\t\t\t\t\treturn Failure(\"--map given twice\");",
     "\t\t\t\t\tif (false)\n\t\t\t\t\t{\n\t\t\t\t\t\treturn Failure(\"--map given twice\");"),
    ("map-syntax", LAUNCH, "a reference that is no reference taken as no --map",
     "if (!Gdpyr.Sim.Level.MapNames.TryParseReference(value, out mapReference, out _, out string error))",
     "if (!Gdpyr.Sim.Level.MapNames.TryParseReference(value, out mapReference, out _, out string error) && false)"),

    # ---- names and references (§9.1, §9.5) -------------------------------------------------------
    ("name-case", NAMES, "upper-case letters taken in a name",
     "IsNameChar(char c) => c is (>= 'a' and <= 'z')", "IsNameChar(char c) => c is (>= 'A' and <= 'z')"),
    ("name-length", NAMES, "a name of 49 characters taken",
     "if (text.Length > MaxNameLength)", "if (text.Length > MaxNameLength + 1)"),
    ("name-flag", NAMES, "a name that starts as a flag does taken",
     "if (text[0] == '-')", "if (false)"),
    ("hash-short", NAMES, "seven hex digits taken as a hash's start",
     "if (text == null || text.Length < MinHashPrefix", "if (text == null || text.Length < MinHashPrefix - 1"),
    ("resolve-path", NAMES, "a path ending .map looked for as a name first",
     "\t\tif (isPath)\n\t\t{\n\t\t\tkey = reference;", "\t\tif (isPath && false)\n\t\t{\n\t\t\tkey = reference;"),
    ("resolve-catalog-first", NAMES, "the library asked before the catalog",
     "if (catalogHas != null && catalogHas(reference))", "if (false && catalogHas != null && catalogHas(reference))"),
    ("resolve-ambiguous", NAMES, "a hash's start that two maps share taken as the last of them",
     "if (matches == 1)", "if (matches >= 1)"),
    ("resolve-same-hash", NAMES, "one map counted twice where two places hold it",
     "if (hash != null && seen.Add(hash) && hash.StartsWith(prefix, StringComparison.Ordinal))",
     "if (hash != null && hash.StartsWith(prefix, StringComparison.Ordinal))"),

    # ---- the bundled map and a scenario's (§9.1, §9.6) -------------------------------------------
    ("bundled-hash", "Scripts/Core/MapCatalog.cs", "the catalog's hash not the bundled file's",
     "\"6d0aa3ff6ebe3487e53d4f4488318b2ebe47ada2e27fb3ae568320a4a8450958\"",
     "\"6d0aa3ff6ebe3487e53d4f4488318b2ebe47ada2e27fb3ae568320a4a8450959\""),
    ("bundled-order", "Scripts/Core/MapCatalog.cs", "the bundled map listed as a scene map",
     "MapInfo.Data(\"hills\", \"Hills\", BundledDirectory + \"hills.map\", BundledHillsHash,",
     "new MapInfo(\"hills\", \"Hills\", DataMapScenePath,"),
    ("scenario-map", "tools/Gdpyr.Playtest/Scenario.cs", "a scenario's map taken in any case",
     "if (c is not ((>= 'a' and <= 'z')", "if (c is not ((>= 'A' and <= 'z')"),
    ("scenario-map-kept", "tools/Gdpyr.Playtest/Scenario.cs", "a scenario's map parsed and forgotten",
     "\t\t\t\tscenario.Map = named;", "\t\t\t\tscenario.Map = null;"),
]


def run(command, cwd):
    result = subprocess.run(command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    return result.stdout


def test(copy):
    """The classes, in a copy: (compiled, the tests that failed, how many ran)."""
    out = run(["dotnet", "test", "Tests/Gdpyr.Tests.csproj", "--nologo", "-v", "q", "--filter", FILTER,
               "--logger", "console;verbosity=normal"], copy)
    if re.search(r"error CS\d+|error MSB\d+", out):
        return False, [m.group(0) for m in re.finditer(r"error CS\d+: [^\[]*", out)][:1], 0
    failed = sorted({re.sub(r"\(.*", "", name).replace("Gdpyr.Tests.", "") for name in re.findall(r"^\s+Failed (\S+)", out, re.M)})
    total = re.search(r"Total:\s+(\d+)", out) or re.search(r"Total tests: (\d+)", out)
    return True, failed, int(total.group(1)) if total else 0


def make_copy(scratch, index):
    copy = os.path.join(scratch, f"tree{index}")
    skip = shutil.ignore_patterns("bin", "obj", "__pycache__", "*.uid", "*.import")
    for folder in ("Scripts", "Tests", "tools", os.path.join("ThirdParty", "FluidHTN")):
        shutil.copytree(os.path.join(ROOT, folder), os.path.join(copy, folder), ignore=skip)
    # The tests read the scenes the catalog names, Structures/*.tres, and the bundled maps, and find
    # the root by project.godot.
    for folder in ("Scenes", "Structures", "Maps"):
        shutil.copytree(os.path.join(ROOT, folder), os.path.join(copy, folder), ignore=skip)
    shutil.copy(os.path.join(ROOT, "project.godot"), copy)
    return copy


def break_rule(copy, rule):
    name, file, what, old, new = rule
    path = os.path.join(copy, file)
    with open(os.path.join(ROOT, file), encoding="utf-8") as source:
        text = source.read()
    if text.count(old) != 1:
        return name, what, None, [f"its piece of {file} is in the file {text.count(old)} times, not once"]
    with open(path, "w", encoding="utf-8") as target:
        target.write(text.replace(old, new))
    try:
        compiled, failed, _ = test(copy)
    finally:
        with open(path, "w", encoding="utf-8") as target:
            target.write(text)
    return name, what, compiled, failed


def main():
    arguments = sys.argv[1:]
    jobs = 3
    if "--jobs" in arguments:
        at = arguments.index("--jobs")
        jobs = max(1, int(arguments[at + 1]))
        del arguments[at:at + 2]
    rules = [rule for rule in RULES if not arguments or any(word in rule[0] for word in arguments)]
    if not rules:
        sys.exit(f"no rule's name holds any of: {' '.join(arguments)}")
    names = [rule[0] for rule in RULES]
    assert len(set(names)) == len(names), "two rules share a name"

    scratch = tempfile.mkdtemp(prefix="le3-mutations-")
    try:
        copies = [make_copy(scratch, index) for index in range(min(jobs, len(rules)))]
        compiled, failed, total = test(copies[0])
        if not compiled or failed or total == 0:
            print(f"FAIL the tree as it stands: {total} tests ran, and these did not pass: {', '.join(failed) or 'none'}")
            return 1
        print(f"PASS the tree as it stands: {total} of {total} tests of {', '.join(CLASSES)}")
        for copy in copies[1:]:
            test(copy)

        caught = 0
        free = list(copies)

        def work(rule):
            copy = free.pop()
            try:
                return break_rule(copy, rule)
            finally:
                free.append(copy)

        with concurrent.futures.ThreadPoolExecutor(max_workers=len(copies)) as pool:
            for name, what, compiled, failed in pool.map(work, rules):
                if compiled is None:
                    print(f"FAIL {name}: {failed[0]}")
                elif not compiled:
                    print(f"FAIL {name}: {what} -- does not compile: {failed[0] if failed else '?'}")
                elif not failed:
                    print(f"FAIL {name}: {what} -- and every test still passed")
                else:
                    caught += 1
                    shown = ", ".join(failed[:4]) + (f", and {len(failed) - 4} more" if len(failed) > 4 else "")
                    print(f"PASS {name}: {what} -- {len(failed)} fail: {shown}")
                sys.stdout.flush()
        print(f"\n{caught} of {len(rules)} broken rules were caught by a test")
        return 0 if caught == len(rules) else 1
    finally:
        shutil.rmtree(scratch, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
