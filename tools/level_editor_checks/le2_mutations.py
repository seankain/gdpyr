#!/usr/bin/env python3
"""LE2's "done when", runnable: every rule of docs/LEVEL_EDITOR.md §6.5, 6.8, 6.9 and 8.6 that LE2
wrote is held by a test that fails when the rule is broken.

    python3 tools/level_editor_checks/le2_mutations.py              # every rule, three at a time
    python3 tools/level_editor_checks/le2_mutations.py reader door  # the ones whose name holds a word
    python3 tools/level_editor_checks/le2_mutations.py --jobs 1

le1_mutations.py's way, over LE2's files: it copies Scripts/, Tests/, tools/ and ThirdParty/FluidHTN to
a scratch directory, and there, for each rule below, breaks the rule -- one edit to one file -- and runs
the six test classes LE2 added. One line a rule: PASS with the tests that failed, or FAIL when every
test still passed, which is a claim nothing holds. The tree it was started from is not written to.

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
LEVEL = "Scripts/Sim/Level/"
CLASSES = ("MapFileTests", "MapDocumentTests", "PrefabSpecsTests", "PrefabPlacementTests", "EditHistoryTests",
           "MapValidatorTests")
FILTER = "|".join(f"FullyQualifiedName~Gdpyr.Tests.{name}" for name in CLASSES)

# (name, file under Scripts/Sim/Level, the rule as broken, the source as it stands, what it is replaced with)
RULES = [
    # ---- section 6.8: the reader is total -------------------------------------------------------
    ("reader-magic", "MapFile.cs", "a file that does not start GDPYRMAP read as a map",
     "if (file.Length < MagicBytes || !file[..MagicBytes].SequenceEqual(Magic))", "if (file.Length < MagicBytes)"),
    ("reader-version", "MapFile.cs", "another format version read as this one",
     "if (file[MagicBytes] != Version)", "if (false)"),
    ("reader-chunk-count", "MapFile.cs", "a file with another number of chunks read",
     "if (file[MagicBytes + 1] != ChunkCount)", "if (false)"),
    ("reader-file-size", "MapFile.cs", "a file over 1 MiB read",
     "if (file.Length > MapLimits.MaxFileBytes)", "if (false)"),
    ("reader-tag", "MapFile.cs", "a chunk taken whatever its tag",
     "if (!found.SequenceEqual(tag))", "if (false)"),
    ("reader-chunk-limit", "MapFile.cs", "a chunk longer than its limit read",
     "if (length > (uint)limit)", "if (false)"),
    ("reader-past-the-end", "MapFile.cs", "a chunk's length not held to the file",
     "if (length > (uint)(file.Length - at))", "if (false)"),
    ("reader-trailing", "MapFile.cs", "bytes after the last chunk taken",
     "if (at != file.Length)", "if (false)"),
    ("reader-meta-left-over", "MapFile.cs", "bytes left in the META chunk taken",
     "if (at != payload.Length)\n\t\t{\n\t\t\terror = $\"the META chunk has", "if (false)\n\t\t{\n\t\t\terror = $\"the META chunk has"),
    ("reader-size", "MapFile.cs", "a terrain of another size or spacing read",
     "if (width != MapLimits.TerrainSamples || depth != MapLimits.TerrainSamples\n\t\t\t|| spacing != MapLimits.TerrainSpacingMillimeters)",
     "if (width < 2 || depth < 2)"),
    ("reader-height-limits", "MapFile.cs", "a height outside -16 ... +64 m read",
     "if (sample < MapLimits.MinHeightSample || sample > MapLimits.MaxHeightSample)", "if (false)"),
    ("reader-inflates-short", "MapFile.cs", "heights that inflate to fewer bytes than the grid's taken",
     "if (read != into.Length)", "if (false)"),
    ("reader-inflates-long", "MapFile.cs", "heights that inflate to more bytes than the grid's taken",
     "if (more)", "if (false)"),
    ("reader-inflate-throws", "MapFile.cs", "a stream that does not inflate let throw",
     "catch (InvalidDataException exception)", "catch (ArgumentNullException exception)"),
    ("reader-text-limit", "MapFile.cs", "a name longer than its limit read",
     "if (length > limit)", "if (false)"),
    ("reader-utf8", "MapFile.cs", "text that is not UTF-8 decoded anyway",
     "if (!Utf8.IsValid(bytes))", "if (false)"),
    ("reader-control", "MapFile.cs", "a control character taken in a name",
     "if (char.IsControl(c) && !(lines && c == '\\n'))", "if (false)"),
    ("reader-objects-limit", "MapFile.cs", "more than 512 objects read",
     "if (count > MapLimits.MaxObjects)", "if (false)"),
    ("reader-parameters-limit", "MapFile.cs", "more than 8 parameters read",
     "if (parameterCount > MapLimits.MaxObjectParameters)", "if (false)"),
    ("reader-parameter-twice", "MapFile.cs", "a parameter given twice passed to the document",
     "if (parameters[q].Key == key)", "if (false)"),
    ("reader-roads-limit", "MapFile.cs", "more than 32 roads read",
     "if (count > MapLimits.MaxRoads)", "if (false)"),
    ("reader-points-limit", "MapFile.cs", "more than 64 points to a road read",
     "if (pointCount > MapLimits.MaxRoadPoints)", "if (false)"),

    # ---- section 6.8: what is written ------------------------------------------------------------
    ("writer-predictor", "MapFile.cs", "each sample predicted from the one above only",
     "return samples[i - 1] + samples[i - width] - samples[i - width - 1];", "return samples[i - width];"),
    ("writer-revision", "MapFile.cs", "the revision not written",
     "BinaryPrimitives.WriteUInt32LittleEndian(fixedPart[16..], document.Revision);",
     "BinaryPrimitives.WriteUInt32LittleEndian(fixedPart[16..], 0u);"),
    ("writer-band-order", "MapFile.cs", "the slope's gain written where the free slope is read",
     "BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[8..], bands.SlopeGain);",
     "BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[8..], bands.SlopeFree);"),
    ("writer-name-limit", "MapFile.cs", "a name over 48 bytes written",
     "else if (MapDocument.Utf8Bytes(document.Name) > MapDocument.MaxNameBytes)", "else if (false)"),
    ("writer-limits", "MapFile.cs", "the limits of a chunk taken from another's",
     "public const int MaxObjectBytes = 2 + 6 + 2 + 1 + 1 + (MapLimits.MaxObjectParameters * 5);",
     "public const int MaxObjectBytes = 2 + 6 + 2 + 1 + 1 + (MapLimits.MaxObjectParameters * 4);"),

    # ---- the document ----------------------------------------------------------------------------
    ("document-thousandths", "MapDocument.cs", "a band number read as 1/1024",
     "SlopeGainMetersPerDegree = SlopeGain / 1000f,", "SlopeGainMetersPerDegree = SlopeGain / 1024f,"),
    ("document-new-water", "MapDocument.cs", "a new map's water at 0 m, where level ground is sand",
     "public const float NewMapWaterLevelMeters = -5f;", "public const float NewMapWaterLevelMeters = 0f;"),
    ("document-clone-shares", "MapDocument.cs", "a clone that shares its ground",
     "var copy = new MapDocument(Terrain.Clone())", "var copy = new MapDocument(Terrain)"),
    ("document-unsorted", "MapDocument.cs", "an object's parameters kept in the order given",
     "\t\tArray.Sort(_parameters, (a, b) => a.Key.CompareTo(b.Key));\n", ""),
    ("document-same-ignores-objects", "MapDocument.cs", "two documents the same whatever their objects",
     "difference = FirstDifference(Objects, other.Objects, \"object\")\n\t\t\t\t\t?? FirstDifference(Roads, other.Roads, \"road\");",
     "difference = FirstDifference(Roads, other.Roads, \"road\");"),

    # ---- section 6.5: the catalog ----------------------------------------------------------------
    ("specs-door", "PrefabSpecs.cs", "a barracks' door at its back",
     "Door = new Vector3(0f, 0f, -6f),", "Door = new Vector3(0f, 0f, 6f),"),
    ("specs-no-allowance", "PrefabSpecs.cs", "two boxes in each other's way when they only touch",
     "Plan.Overlaps(other.Plan, PrefabSpecs.OverlapAllowanceMeters)", "Plan.Overlaps(other.Plan, -0.01f)"),
    ("specs-flat-way", "PrefabSpecs.cs", "\"in the way\" asked in plan only, so no bridge passes over anything",
     "Bottom < other.Top && other.Bottom < Top && ", ""),
    ("specs-piers-always", "PrefabSpecs.cs", "piers stood up with supports off",
     "if (spec.Kind != PrefabKind.Bridge || spec.ValueOf(placed, SupportsKey) == 0)", "if (spec.Kind != PrefabKind.Bridge)"),
    ("specs-pier-at-the-middle", "PrefabSpecs.cs", "a pier under the section's middle, not its end",
     "float along = end * ((DeckLengthMeters - PierSideMeters) * 0.5f);", "float along = 0f;"),
    ("specs-ring", "PrefabSpecs.cs", "a barracks' ring without its guns' 7.1 m off its centre",
     "MathF.Sqrt((5.5f * 5.5f) + (4.5f * 4.5f)) + 100f;", "100f;"),
    ("specs-structures-all", "PrefabSpecs.cs", "a map's structures allowed every slot, leaving builders none",
     "PrefabGroup.Structure => SimConfig.MaxStructures / 2,", "PrefabGroup.Structure => SimConfig.MaxStructures,"),
    ("specs-two-kiosks", "PrefabSpecs.cs", "a map allowed two kiosks",
     "PrefabGroup.VehicleKiosk => 1,", "PrefabGroup.VehicleKiosk => 2,"),
    ("specs-flat-ramp", "PrefabSpecs.cs", "a ramp whose high end is level with its low one",
     "new PrefabConnector(new Vector3(0f, RampRiseMeters, DeckLengthMeters * 0.5f), 0f),",
     "new PrefabConnector(new Vector3(0f, 0f, DeckLengthMeters * 0.5f), 0f),"),
    ("specs-renamed", "PrefabSpecs.cs", "an id given another name",
     "new PrefabSpec(Bunker, \"bunker\",", "new PrefabSpec(Bunker, \"pillbox\","),
    ("specs-tower-no-top", "PrefabSpecs.cs", "the sniper tower without its top",
     "new[] { StructureBox.Standing(2f, 2f, 7f), StructureBox.Standing(3.2f, 3.2f, 0.9f, 7f) }),",
     "new[] { StructureBox.Standing(2f, 2f, 7f) }),"),

    # ---- section 6.5: placement ------------------------------------------------------------------
    ("place-limit", "PrefabPlacement.cs", "a prefab placed past its group's limit",
     "if (document.Objects.Count >= MapLimits.MaxObjects || document.CountOf(spec.Group) >= PrefabSpecs.MaxOf(spec.Group))",
     "if (false)"),
    ("place-bounds", "PrefabPlacement.cs", "a prefab placed off the map",
     "if (IsOutOfBounds(placed))", "if (false)"),
    ("place-115", "PrefabPlacement.cs", "a round's piece held to +-120 m, not +-115 m",
     "if (spec.IsRoundPiece && (MathF.Abs(at.X) > MapLimits.PlayHalfMeters", "if (false && (MathF.Abs(at.X) > MapLimits.PlayHalfMeters"),
    ("place-way", "PrefabPlacement.cs", "a prefab placed in another's way",
     "if (PrefabSpecs.InTheWay(boxes, others))", "if (false)"),
    ("place-grade", "PrefabPlacement.cs", "a prefab placed on ground steeper than it takes",
     "if (spec.HasGradeLimit && check.GradeDegrees > spec.SteepestDegrees)", "if (false)"),
    ("place-water", "PrefabPlacement.cs", "a pad placed under the water",
     "if ((spec.MakesPad || spec.IsRoundPiece) && check.Level < document.Bands.WaterLevelMeters)", "if (false)"),
    ("place-neighbour", "PrefabPlacement.cs", "a pad placed that moves another's",
     "if (disturbed >= 0)", "if (false)"),
    ("place-neighbour-skirt", "PrefabPlacement.cs", "another's pad taken as its footprint alone, not 1.5 m past it",
     "&& area.DistanceTo(before.WorldX(x), before.WorldZ(z)) <= TerrainConform.PadMarginMeters)",
     "&& area.DistanceTo(before.WorldX(x), before.WorldZ(z)) <= 0f)"),
    ("place-door", "PrefabPlacement.cs", "a structure placed at a barracks door",
     "if (door >= 0)", "if (false)"),
    ("place-door-one-way", "PrefabPlacement.cs", "a barracks placed with its door at a structure",
     "if (barracks && otherSpec.Kind == PrefabKind.Structure", "if (false && otherSpec.Kind == PrefabKind.Structure"),
    ("place-order", "PrefabPlacement.cs", "the door checked before the neighbours",
     "int door = BlockedDoor(document, placed, -1);", "int door = -1;\n\t\tif (BlockedDoor(document, placed, -1) >= 0)\n\t\t{\n\t\t\tcheck.Result = PrefabPlacementResult.BlocksDoor;\n\t\t\treturn check;\n\t\t}"),
    ("place-level-off", "PrefabPlacement.cs", "a pad levelled 10 cm over the mean",
     "short y = Quantize.MetersToI16(spec.MakesPad\n\t\t\t? mean", "short y = Quantize.MetersToI16(spec.MakesPad\n\t\t\t? mean + 0.1f"),
    ("place-level-floor", "PrefabPlacement.cs", "a pad's step taken by flooring the height, not rounding it",
     "int steps = (int)Math.Floor(((yCentimeters * 256) + 50) / 100.0);", "int steps = (int)Math.Floor((yCentimeters * 256) / 100.0);"),
    ("place-mean-centre", "PrefabPlacement.cs", "the level taken from the ground at the centre, not the mean",
     "mean = (float)mh;", "mean = field.Sample(area.X, area.Z);"),
    ("place-fit-swapped", "PrefabPlacement.cs", "the fitted grade's x taken from z's normal equation",
     "gradeX = (float)(((sxh * szz) - (szh * sxz)) / determinant);", "gradeX = (float)(((szh * sxx) - (sxh * sxz)) / determinant);"),
    ("place-no-triangle", "PrefabPlacement.cs", "a footprint over too few samples given no grade",
     "\t\tTriangle(field, area.X, area.Z, out gradeX, out gradeZ);\n\t\tmean = field.Sample(area.X, area.Z);",
     "\t\tgradeX = 0f;\n\t\tgradeZ = 0f;\n\t\tmean = field.Sample(area.X, area.Z);"),
    ("place-triangle-diagonal", "PrefabPlacement.cs", "the triangle under a point taken across the other diagonal",
     "if ((gx - cx) + (gz - cz) <= 1f)", "if (gx - cx >= gz - cz)"),
    ("place-unrounded", "PrefabPlacement.cs", "a pad's skirt left between steps",
     "\t\t\tafter.Round(touched);\n\t\t\tint disturbed", "\t\t\tint disturbed"),
    ("place-standing-height", "PrefabPlacement.cs", "a padded piece under the water by its centimetre, not its pad",
     "\t\t\t? PadLevel(placed)\n", "\t\t\t? Quantize.I16ToMeters(placed.Y)\n"),
    ("keep-reached-only", "PrefabPlacement.cs", "only the pads an edit reaches levelled again, not those after them",
     "if (PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec) && spec.MakesPad\n\t\t\t\t&& PrefabSpecs.TryGetPadArea(placed, out PadArea area))\n\t\t\t{",
     "if (PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec) && spec.MakesPad\n\t\t\t\t&& PrefabSpecs.TryGetPadArea(placed, out PadArea area)\n\t\t\t\t&& !area.Bounds(field, TerrainConform.PadMarginMeters).Intersect(reached).IsEmpty)\n\t\t\t{"),
    ("keep-nothing", "PrefabPlacement.cs", "a pad not kept",
     "if (!any)", "if (true)"),
    ("keep-reports-touched", "PrefabPlacement.cs", "what the operator touched reported as moved",
     "if (field[x, z] != before[x, z])", "if (true)"),

    # ---- section 6.9: the validator --------------------------------------------------------------
    ("validate-spawn", "MapValidator.cs", "a map with no spawn line passed",
     "if (document.CountOf(PrefabGroup.SpawnLine) == 0)", "if (false)"),
    ("validate-locker", "MapValidator.cs", "a map with no locker passed",
     "if (document.CountOf(PrefabGroup.WeaponLocker) == 0)", "if (false)"),
    ("validate-barracks-max", "MapValidator.cs", "five barracks passed",
     " || barracks > PrefabSpecs.MaxOf(PrefabGroup.Barracks)", ""),
    ("validate-nodes-min", "MapValidator.cs", "a map with no node passed",
     "if (nodes < PrefabSpecs.MinOf(PrefabGroup.ResourceNode) || ", "if ("),
    ("validate-too-many", "MapValidator.cs", "more than a group may hold passed",
     "if (count > PrefabSpecs.MaxOf(group))", "if (false)"),
    ("validate-unknown", "MapValidator.cs", "a prefab past the catalog only warned of",
     "report.Error(MapIssueCode.UnknownPrefab,", "report.Warn(MapIssueCode.UnknownPrefab,"),
    ("validate-bounds", "MapValidator.cs", "an object off the map passed",
     "if (PrefabPlacement.IsOutOfBounds(placed))", "if (false)"),
    ("validate-way", "MapValidator.cs", "two objects in each other's way passed",
     "if (boxes[j] != null && PrefabSpecs.InTheWay(boxes[i], boxes[j]))", "if (false)"),
    ("validate-pad-spread", "MapValidator.cs", "a pad not level passed",
     "if (highest - lowest > PadToleranceMeters)", "if (false)"),
    ("validate-pad-height", "MapValidator.cs", "an object off its pad's height passed",
     "else if (MathF.Abs(mean - at.Y) > PadToleranceMeters)", "else if (false)"),
    ("validate-pad-tolerance", "MapValidator.cs", "a pad held to 5 mm, under what a centimetre and a step are apart",
     "public const float PadToleranceMeters = 0.02f;", "public const float PadToleranceMeters = 0.005f;"),
    ("validate-pad-margin", "MapValidator.cs", "a pad's ground taken as its footprint, not 1.5 m past it",
     "if (area.DistanceTo(field.WorldX(x), field.WorldZ(z)) > TerrainConform.PadMarginMeters)",
     "if (area.DistanceTo(field.WorldX(x), field.WorldZ(z)) > 0f)"),
    ("validate-water", "MapValidator.cs", "a round's piece under the water passed",
     "if (spec.IsRoundPiece && PrefabPlacement.StandingHeight(placed) < document.Bands.WaterLevelMeters)", "if (false)"),
    ("validate-heights", "MapValidator.cs", "a height over +64 m passed",
     "if (!(height >= MapLimits.MinHeightMeters && height <= MapLimits.MaxHeightMeters))",
     "if (!(height >= MapLimits.MinHeightMeters))"),
    ("validate-size", "MapValidator.cs", "a terrain of another size passed",
     "if (field.Width != MapLimits.TerrainSamples || field.Depth != MapLimits.TerrainSamples)", "if (false)"),
    ("validate-bands", "MapValidator.cs", "water and bands out of their ranges passed",
     "if (!bands.IsValid)", "if (false)"),
    ("validate-flags", "MapValidator.cs", "an object with flags this build does not define passed",
     "if (placed.Flags != 0)", "if (false)"),
    ("validate-parameter-key", "MapValidator.cs", "a parameter a prefab has not passed",
     "if (!spec.TryGetParameter(parameter.Key, out PrefabParameter defined))\n\t\t\t{\n\t\t\t\treport.Error(",
     "if (!spec.TryGetParameter(parameter.Key, out PrefabParameter defined))\n\t\t\t{\n\t\t\t\treport.Warn("),
    ("validate-parameter-range", "MapValidator.cs", "a parameter outside its range passed",
     "else if (parameter.Value < defined.Min || parameter.Value > defined.Max)", "else if (false)"),
    ("validate-road-width", "MapValidator.cs", "a road 5 m wide passed",
     "else if (MapRoad.Widths.IndexOf(road.WidthDecimeters) < 0)", "else if (false)"),
    ("validate-road-grade", "MapValidator.cs", "a road graded to 20 % passed",
     "|| road.GradeLimitPermille > MapRoad.MaxGradeLimitPermille)", ")"),
    ("validate-road-field", "MapValidator.cs", "a road off the field passed",
     "if (MathF.Abs(point.XMeters) > MapLimits.FieldHalfMeters || MathF.Abs(point.ZMeters) > MapLimits.FieldHalfMeters)",
     "if (false)"),
    ("validate-ring", "MapValidator.cs", "a node inside a barracks' ring not warned of",
     "if (distance < PrefabSpecs.BarracksDefendedRadiusMeters)", "if (false)"),
    ("validate-relief", "MapValidator.cs", "relief inside a node's radius not warned of",
     "if (highest - lowest > NodeReliefMeters)", "if (false)"),
    ("validate-bridge-landed", "MapValidator.cs", "every bridge end taken as landed",
     "if (MathF.Abs(end.Y - document.Terrain.Sample(end.X, end.Z)) <= LandedEndMeters)", "if (true)"),
    ("validate-bridge-self-joined", "MapValidator.cs", "a bridge end taken as joined to itself",
     "joined = owners[m] != owners[k] && ", "joined = "),
    ("validate-bridge-road", "MapValidator.cs", "a road's end not joining a bridge's",
     "joined = Near(road.Points[0], end) || Near(road.Points[^1], end);", "joined = false;"),

    # ---- section 8.6: undo -----------------------------------------------------------------------
    ("history-unbounded", "EditHistory.cs", "every step kept, past two hundred",
     "if (_done.Count > MaxSteps)", "if (false)"),
    ("history-redo-kept", "EditHistory.cs", "a new step that keeps what could have been redone",
     "\t\t}\n\n\t\t_undone.Clear();\n\t}", "\t\t}\n\t}"),
    ("history-empty-step", "EditHistory.cs", "an edit that changes nothing kept as a step",
     "if (edit == null || edit.IsEmpty)", "if (edit == null)"),
    ("history-revert-forwards", "EditHistory.cs", "an edit's objects reverted in the order they were made",
     "for (int i = _objects.Count - 1; i >= 0; i--)", "for (int i = 0; i < _objects.Count; i++)"),
    ("history-revert-ground", "EditHistory.cs", "an edit reverted to the ground after it",
     "Put(document.Terrain, _before);", "Put(document.Terrain, _after);"),
    ("history-remove-last", "EditHistory.cs", "an object taken out from the end, not its index",
     "objects.RemoveAt(index);", "objects.RemoveAt(objects.Count - 1);"),
    ("history-unrounded", "EditHistory.cs", "a stored sample taken down to the step below",
     "samples[k++] = HeightField.ToSample(field[x, z]);", "samples[k++] = (short)MathF.Floor(field[x, z] * 256f - 0.5f);"),
]


def run(command, cwd):
    result = subprocess.run(command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=1800)
    return result.stdout


def test(copy):
    """The six classes, in a copy: (compiled, the tests that failed, how many ran)."""
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
    # The tests read the scenes and Structures/*.tres the catalog is held to, and find the root by project.godot.
    for folder in (os.path.join("Scenes", "Environment"), "Structures"):
        shutil.copytree(os.path.join(ROOT, folder), os.path.join(copy, folder), ignore=skip)
    shutil.copy(os.path.join(ROOT, "project.godot"), copy)
    return copy


def break_rule(copy, rule):
    name, file, what, old, new = rule
    path = os.path.join(copy, LEVEL, file)
    with open(os.path.join(ROOT, LEVEL, file), encoding="utf-8") as source:
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

    scratch = tempfile.mkdtemp(prefix="le2-mutations-")
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
