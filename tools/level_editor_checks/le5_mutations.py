#!/usr/bin/env python3
"""LE5's engine-free rules, each held by a test that fails when the rule is broken: placing, turning and
taking up a prefab through the editor's commands, picking what is under a click, the deep check's
walks, the buildable rule, a structure prefab's kind, and the prefab tool's palette, counts, turns,
grid and ghost (docs/LEVEL_EDITOR.md §6.5, §6.9, §7.5, §8.4).

    python3 tools/level_editor_checks/le5_mutations.py              # every rule, three at a time
    python3 tools/level_editor_checks/le5_mutations.py replace pick # the ones whose name holds a word
    python3 tools/level_editor_checks/le5_mutations.py --jobs 1

le4_mutations.py's way: it copies what the tests compile and read to a scratch directory, and there,
for each rule below, breaks the rule -- one edit to one file -- and runs the test classes LE5 added or
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
CLASSES = ("MapEditPrefabTests", "MapReachTests", "BuildableGroundTests", "MapEditorControlsTests", "PrefabSpecsTests",
           "PrefabPlacementTests", "MapEditSessionTests")
FILTER = "|".join(f"FullyQualifiedName~Gdpyr.Tests.{name}" for name in CLASSES)

PLACEMENT = "Scripts/Sim/Level/PrefabPlacement.cs"
SESSION = "Scripts/Sim/Level/MapEditSession.cs"
VALIDATOR = "Scripts/Sim/Level/MapValidator.cs"
BUILDABLE = "Scripts/Sim/Level/BuildableGround.cs"
SPECS = "Scripts/Sim/Level/PrefabSpecs.cs"
CONTROLS = "Scripts/MapEditor/MapEditorControls.cs"

# (name, file, the rule as broken, the source as it stands, what it is replaced with)
RULES = [
    # ---- an object placed again where it is (§8.4) ---------------------------------------------------
    ("replace-count", PLACEMENT, "an object turned counted against its own group's limit",
     "\t\t\talreadyOfGroup--;\n", ""),
    ("replace-objects", PLACEMENT, "an object turned on a full map counted as one more",
     "if ((!moving && document.Objects.Count >= MapLimits.MaxObjects)", "if ((document.Objects.Count >= MapLimits.MaxObjects)"),
    ("replace-in-the-way", PLACEMENT, "an object turned in its own way",
     "\t\t\tif (i == replacing)\n\t\t\t{\n\t\t\t\tcontinue;\n\t\t\t}\n\n\t\t\tothers.Clear();", "\t\t\tothers.Clear();"),
    ("replace-neighbour", PLACEMENT, "an object turned a neighbour to its own old pad",
     "int disturbed = Disturbed(document, field, after, touched, replacing);",
     "int disturbed = Disturbed(document, field, after, touched, -1);"),
    ("replace-edit", PLACEMENT, "an object turned moved to the end of the file",
     "\t\t\t? edit.Replace(replacing, document.Objects[replacing], placed)\n",
     "\t\t\t? edit.Insert(document.Objects.Count, placed)\n"),

    # ---- the session's prefab commands ---------------------------------------------------------------
    ("commit-refused", SESSION, "a refused placement applied", "if (!check.IsOk || Stroking)", "if (Stroking)"),
    ("commit-objects", SESSION, "a placement not saying it changed the objects",
     "outcome = new EditOutcome(check.Edit.Rect, check.Edit.Rect, true, true);",
     "outcome = new EditOutcome(check.Edit.Rect, check.Edit.Rect, true, false);"),
    ("take-up-objects", SESSION, "a take-up not saying it changed the objects",
     "outcome = new EditOutcome(SampleRect.Empty, SampleRect.Empty, true, true);",
     "outcome = new EditOutcome(SampleRect.Empty, SampleRect.Empty, true, false);"),
    ("request-parameters", SESSION, "an object's parameters left behind when it is placed again",
     "parameters.Length > 0 ? parameters : null", "null"),
    ("pick-disc", SESSION, "a node's disc not picked by a click on it",
     "if (area == float.MaxValue && PrefabSpecs.TryGetPadArea(placed, out PadArea pad) && pad.Covers(x, z))",
     "if (false && PrefabSpecs.TryGetPadArea(placed, out PadArea pad) && pad.Covers(x, z))"),
    ("pick-smallest", SESSION, "the first thing under a click picked, not the smallest",
     "\t\t\tif (area < bestArea)\n", "\t\t\tif (area < float.MaxValue && best < 0)\n"),
    ("pick-marker", SESSION, "a marker picked only a hand's breadth from it",
     "public const float PickMarkerMeters = 2f;", "public const float PickMarkerMeters = 0.5f;"),

    # ---- reach (§6.9, §8.7) ---------------------------------------------------------------------------
    ("reach-spawn", VALIDATOR, "the spawn line not asked about", "else if (spec.Group == PrefabGroup.SpawnLine)",
     "else if (spec.Group == PrefabGroup.WeaponLocker)"),
    ("reach-door", VALIDATOR, "a barracks' middle asked about, not its door",
     "starts.Add((i, door, $\"the door of barracks {i}\"));", "starts.Add((i, at, $\"the door of barracks {i}\"));"),
    ("reach-height", VALIDATOR, "a node asked about at its stored height, not where it stands",
     "\t\t\tat.Y = PrefabPlacement.StandingHeight(placed);\n", ""),
    ("reach-answer", VALIDATOR, "a walk found warned of", "if (!reaches(start, node))", "if (reaches(start, node))"),
    ("reach-error", VALIDATOR, "a walk not found an error, not a warning",
     "report.Warn(MapIssueCode.Unreachable,", "report.Error(MapIssueCode.Unreachable,"),

    # ---- buildable (§7.5) and structures (§6.5) -------------------------------------------------------
    ("buildable-turn", BUILDABLE, "a site asked square to the field only",
     "public static readonly float[] Turns = { 0f, MathF.PI / 4f };", "public static readonly float[] Turns = { 0f };"),
    ("buildable-corners", BUILDABLE, "the ground under the centre taken for the corners'",
     "heights[i] = field.Sample(points[i].X, points[i].Z);", "heights[i] = field.Sample(x, z);"),
    ("structure-kind", SPECS, "a pillbox prefab put up as a sniper tower",
     "StructurePillbox => StructureKinds.Pillbox,", "StructurePillbox => StructureKinds.SniperTower,"),

    # ---- the prefab tool's panel and keys (§8.2, §8.4) ------------------------------------------------
    ("tool-built", CONTROLS, "the prefab tool left off the rail",
     "public static bool IsBuilt(EditorTool tool) => tool is EditorTool.Terrain or EditorTool.Prefabs;",
     "public static bool IsBuilt(EditorTool tool) => tool == EditorTool.Terrain;"),
    ("palette-bridges", CONTROLS, "bridge sections on the prefab palette",
     "if (spec.Kind == kind && kind != PrefabKind.Bridge)", "if (spec.Kind == kind)"),
    ("count-limit", CONTROLS, "a group with no limit counted against int.MaxValue",
     "string label = max == int.MaxValue ? $\"{spec.Name} {count}\" : $\"{spec.Name} {count}/{max}\";",
     "string label = $\"{spec.Name} {count}/{max}\";"),
    ("count-needs", CONTROLS, "a group a map must hold not said to be missing",
     "return count < PrefabSpecs.MinOf(spec.Group) ? label + \", needs one\" : label;", "return label;"),
    ("turn-direction", CONTROLS, "Shift+R turning the way R does",
     "float turned = (steps + Math.Sign(direction)) * step;", "float turned = (steps + 1) * step;"),
    ("turn-wrap", CONTROLS, "a turn below 0 left negative",
     "return turned < 0f ? turned + tau : turned;", "return turned;"),
    ("snap-round", CONTROLS, "the grid floored, not rounded",
     "public static float Snap(float meters) => MathF.Round(meters);", "public static float Snap(float meters) => MathF.Floor(meters);"),
    ("ghost-grade", CONTROLS, "a grade under a prefab with no footprint",
     "string grade = spec.HasFootprint ?", "string grade = true ?"),
    ("parameter-step", CONTROLS, "a capture radius stepped a centimetre at a time",
     "parameter.Max - parameter.Min > 100 ? 50f : 1f", "1f"),
    ("overlay-buildable", CONTROLS, "O skipping the buildable overlay",
     "TerrainOverlay.Slope => TerrainOverlay.Buildable,", "TerrainOverlay.Slope => TerrainOverlay.Contours,"),
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

    scratch = tempfile.mkdtemp(prefix="le5-mutations-")
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
