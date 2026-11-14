#!/usr/bin/env python3
"""LE6's engine-free rules, each held by a test that fails when the rule is broken: a road's centreline,
profile, pinned ends, ribbon and coverage; its grading on a map, the pads it may not move and the steps it
makes; the bridge rules' connectors, snapping, piers, landings, and the piers kept after an edit of the
ground; the validator's road warnings and its road in the way; and the road and bridge tools' settings and
keys (docs/LEVEL_EDITOR.md §6.6, §6.7, §6.9, §8.5).

    python3 tools/level_editor_checks/le6_mutations.py              # every rule, three at a time
    python3 tools/level_editor_checks/le6_mutations.py pier land    # the ones whose name holds a word
    python3 tools/level_editor_checks/le6_mutations.py --jobs 1

le5_mutations.py's way: it copies what the tests compile and read to a scratch directory, and there,
for each rule below, breaks the rule -- one edit to one file -- and runs the test classes LE6 added or
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
CLASSES = ("RoadPathTests", "BridgeRulesTests", "BridgePierKeepingTests", "MapEditorControlsTests", "MapValidatorTests",
           "PrefabPlacementTests", "MapEditSessionTests", "EditHistoryTests", "MapEditPrefabTests")
FILTER = "|".join(f"FullyQualifiedName~Gdpyr.Tests.{name}" for name in CLASSES)

ROAD = "Scripts/Sim/Level/RoadPath.cs"
BRIDGE = "Scripts/Sim/Level/BridgeRules.cs"
VALIDATOR = "Scripts/Sim/Level/MapValidator.cs"
PLACEMENT = "Scripts/Sim/Level/PrefabPlacement.cs"
SESSION = "Scripts/Sim/Level/MapEditSession.cs"
HISTORY = "Scripts/Sim/Level/EditHistory.cs"
CONTROLS = "Scripts/MapEditor/MapEditorControls.cs"

# (name, file, the rule as broken, the source as it stands, what it is replaced with)
RULES = [
    # ---- a road's line, profile and ribbon (§6.6) -------------------------------------------------------
    ("envelope-one-pass", ROAD, "m and M passed forwards only, not back",
     "for (int i = n - 2; i >= 0; i--)", "for (int i = -1; i >= 0; i--)"),
    ("profile-lower", ROAD, "the profile the highest under the ground, not the mean of the two",
     "_profile[i] = (lower[i] + upper[i]) * 0.5f;", "_profile[i] = lower[i];"),
    ("pin-from-above", ROAD, "an end pinned over the ground held down to it, never lifted",
     "\t\t\t\tlow = MathF.Max(low, z0 - (Grade * s));\n", ""),
    ("join-unchecked", ROAD, "two ends further apart in height than the limit joined",
     "if (MathF.Abs(a - b) > (Grade * Length) + 1e-4f)", "if (false)"),
    ("corner-radius", ROAD, "a corner rounded to half the road's width",
     "public float CornerRadius => Width;", "public float CornerRadius => HalfWidth;"),
    ("corner-room", ROAD, "a corner taken without the room its arc needs",
     "if (tangent[j] + tangent[j + 1] > lengths[j] + 1e-3)", "if (false)"),
    ("sample-metre", ROAD, "the centreline sampled every metre",
     "public const float SampleMeters = 0.5f;", "public const float SampleMeters = 1f;"),
    ("ribbon-round-ends", ROAD, "the ribbon rounded past its ends, not square",
     "\t\t\tif (!outside)\n", "\t\t\tif (true)\n"),
    ("ribbon-ground", ROAD, "the ribbon anchored to the ground under it, not the profile",
     "anchors.Add(new ConformAnchor(x, z, ProfileAt(along)));", "anchors.Add(new ConformAnchor(x, z, field[x, z]));"),
    ("ribbon-narrow", ROAD, "the ribbon a metre narrower than the road",
     "if (distance <= HalfWidth)\n\t\t\t{\n\t\t\t\tanchors.Add", "if (distance <= HalfWidth - 1f)\n\t\t\t{\n\t\t\t\tanchors.Add"),
    ("coverage-hard", ROAD, "a road drawn hard-edged, with no fade",
     "float value = Math.Clamp((HalfWidth + fade - distance) / CoverageFadeMeters, 0f, 1f);",
     "float value = distance <= HalfWidth ? 1f : 0f;"),
    ("crossing-unseen", ROAD, "a centreline that crosses itself not seen to",
     "if (i - j >= 2 && Meet(Points[j], Points[j + 1], a, b))", "if (false)"),
    ("check-condition", ROAD, "a road whose ribbon is steeper than the skirt kept, ungraded",
     "if (TerrainConform.Apply(trial.Terrain, anchors, out SampleRect touched) != ConformResult.Ok)",
     "if (TerrainConform.Apply(trial.Terrain, anchors, out SampleRect touched) == (ConformResult)99)"),
    ("check-ribbon-kept", ROAD, "a road kept whose ribbon the pads kept after it moved",
     "if (trial.Terrain[anchors[i].X, anchors[i].Z] != graded[i])", "if (false)"),
    ("check-joins", ROAD, "a road's ends never joined to a connector",
     "if (BridgeRules.Nearest(connectors, start.X, start.Z, BridgeRules.RoadJointMeters, out Connector first))",
     "if (BridgeRules.Nearest(connectors, start.X, start.Z, 0f, out Connector first))"),
    ("check-limit", ROAD, "a 33rd road taken",
     "if (!moving && document.Roads.Count >= MapLimits.MaxRoads)", "if (false)"),
    ("road-under-pad", ROAD, "a pad's footprint over a road's ribbon not seen",
     "area.DistanceTo(point.X, point.Y) < path.HalfWidth", "area.DistanceTo(point.X, point.Y) < 0f"),

    # ---- bridges (§6.7) ------------------------------------------------------------------------------------
    ("attach-heading", BRIDGE, "a section put on a connector heading the way it does, not back",
     "float yaw = Wrap(target.Heading + MathF.PI - own.Heading);", "float yaw = Wrap(target.Heading - own.Heading);"),
    ("snap-near", BRIDGE, "a free connector taken only within a metre of the cursor",
     "public const float SnapMeters = 3f;", "public const float SnapMeters = 1f;"),
    ("joint-plan-only", BRIDGE, "two sections' ends at one point in plan joined whatever their heights",
     ": plan <= JointMeters && MathF.Abs(connector.Point.Y - other.Point.Y) <= JointMeters;", ": plan <= JointMeters;"),
    ("pier-foot", BRIDGE, "a pier's foot at the ground, not half a metre under it",
     "public const float PierFootMeters = 0.5f;", "public const float PierFootMeters = 0f;"),
    ("pier-at-joints", BRIDGE, "a pier stood at a joint a section's pier stands at already",
     "&& otherSpec.ValueOf(other, key) > 0)", "&& false)"),
    ("pier-before-landing", BRIDGE, "a section's piers worked out on the ground before its ends are landed",
     "bool fits = WithPiers(document, after ?? field, ref request, replacing, out int piers);",
     "bool fits = WithPiers(document, field, ref request, replacing, out int piers);"),
    ("pier-too-long", BRIDGE, "a pier longer than a file holds taken, shortened",
     "if (!fits && check.Result != PrefabPlacementResult.LimitReached)", "if (false)"),
    ("land-never", BRIDGE, "no end landed",
     "if (IsJoined(own, others) || MathF.Abs(own.Point.Y - field.Sample(own.Point.X, own.Point.Z)) > LandingReachMeters)",
     "if (true)"),
    ("land-joined", BRIDGE, "a joined end landed",
     "if (IsJoined(own, others) || MathF.Abs(", "if (MathF.Abs("),
    ("land-reach", BRIDGE, "an end landed 30 m over the ground",
     "public const float LandingReachMeters = 3f;", "public const float LandingReachMeters = 30f;"),
    ("land-disturbs", BRIDGE, "a landing taken that moves a pad",
     "\t\t\tint disturbed = PrefabPlacement.Disturbed(document, field, after, touched, replacing);\n\t\t\tif (disturbed >= 0)",
     "\t\t\tint disturbed = PrefabPlacement.Disturbed(document, field, after, touched, replacing);\n\t\t\tif (false)"),
    ("keep-piers", BRIDGE, "a pier not given its length again after the ground under it moved",
     "if (length != section.Parameter(key, 0))", "if (false)"),
    ("take-up-repier", BRIDGE, "a section's neighbour left on nothing when it is taken up",
     "\t\t\t\t\tedit.Replace(i, other, repiered);\n", ""),

    # ---- the validator (§6.9) -------------------------------------------------------------------------------
    ("validate-road-grade", VALIDATOR, "a road edited off its grade not warned of",
     "if (excess > RoadToleranceMeters)", "if (false)"),
    ("validate-road-tolerance", VALIDATOR, "a road held to its grade to a millimetre, under what its triangles bend by",
     "public const float RoadToleranceMeters = 0.05f;", "public const float RoadToleranceMeters = 0.001f;"),
    ("validate-on-road", VALIDATOR, "a pad's object standing on a road passed",
     "int road = RoadPath.RoadUnder(document, area);\n\t\t\tif (road >= 0)",
     "int road = RoadPath.RoadUnder(document, area);\n\t\t\tif (false)"),
    ("validate-road-undrawable", VALIDATOR, "a road whose line cannot be drawn passed",
     "if (path.Shape != RoadResult.Malformed)", "if (false)"),

    # ---- placing, the session and undo ------------------------------------------------------------------------
    ("place-on-road", PLACEMENT, "a pad's prefab placed on a road",
     "RoadPath.RoadUnder(document, area) is int road && road >= 0", "RoadPath.RoadUnder(document, area) is int road && road >= 99"),
    ("place-keeps-piers", PLACEMENT, "a pier not given its length again after a pad moved the ground under it",
     "\t\t\tedit.Replace(pier.Index, pier.Before, pier.After);\n", ""),
    ("stroke-keeps-piers", SESSION, "a pier not given its length again after a stroke moved the ground under it",
     "\t\t\t\tDocument.Objects[pier.Index] = pier.After;\n", ""),
    ("replace-section", SESSION, "a section turned where it is keeping the piers it had",
     "? BridgeRules.Check(Document, request, index).Placement", "? PrefabPlacement.Check(Document, request, index)"),
    ("apply-road-outcome", SESSION, "a road graded not saying the roads changed",
     "outcome = new EditOutcome(check.Area, check.Changed, true, check.PiersKept > 0, true);",
     "outcome = new EditOutcome(check.Area, check.Changed, true, check.PiersKept > 0, false);"),
    ("undo-road-outcome", SESSION, "a road undone not saying the roads changed",
     "edit.Roads.Count > 0);\n\t\tTouch();\n\t\treturn true;\n\t}\n\n\t/// <summary>Makes the step last undone again.",
     "false);\n\t\tTouch();\n\t\treturn true;\n\t}\n\n\t/// <summary>Makes the step last undone again."),
    ("history-road-revert", HISTORY, "a road change reverted by making it again",
     "Change(document.Roads, change.Index, change.After, change.Before);", "Change(document.Roads, change.Index, change.Before, change.After);"),
    ("history-road-empty", HISTORY, "an edit of roads alone taken for no step",
     "public bool IsEmpty => Rect.IsEmpty && _objects.Count == 0 && _roads.Count == 0;",
     "public bool IsEmpty => Rect.IsEmpty && _objects.Count == 0;"),

    # ---- the road and bridge tools (§8.2, §8.5) -----------------------------------------------------------------
    ("road-width-step", CONTROLS, "[ and ] both widening a road",
     "at = at < 0 ? Array.IndexOf(RoadWidths, DefaultRoadWidthMeters) : at + Math.Sign(direction);",
     "at = at < 0 ? Array.IndexOf(RoadWidths, DefaultRoadWidthMeters) : at + 1;"),
    ("road-grade-held", CONTROLS, "a road's grade limit stepped past 6-15 %",
     "return Math.Clamp(permille, MapRoad.MinGradeLimitPermille, MapRoad.MaxGradeLimitPermille) / 1000f;",
     "return permille / 1000f;"),
    ("nudge-step", BRIDGE, "PgUp and PgDn moving a section half a metre",
     "public const float NudgeMeters = 0.25f;", "public const float NudgeMeters = 0.5f;"),
    ("key-enter", CONTROLS, "Enter not grading the road",
     "new(Key.Enter, false, false, EditorAction.Apply),", "new(Key.Enter, false, false, EditorAction.Cancel),"),
    ("ghost-joins", CONTROLS, "the road preview not saying how many ends are joined",
     "((check.StartJoin != null ? 1 : 0) + (check.EndJoin != null ? 1 : 0))", "(0)"),
    ("tools-built", CONTROLS, "the bridge tool left off the rail",
     "tool is EditorTool.Terrain or EditorTool.Prefabs or EditorTool.Roads or EditorTool.Bridges;",
     "tool is EditorTool.Terrain or EditorTool.Prefabs or EditorTool.Roads;"),
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

    scratch = tempfile.mkdtemp(prefix="le6-mutations-")
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
