#!/usr/bin/env python3
"""LE1's "done when", runnable: every claim of docs/LEVEL_EDITOR.md §6.1-6.4 is a test that fails
when its rule is broken.

    python3 tools/level_editor_checks/le1_mutations.py              # every rule, three at a time
    python3 tools/level_editor_checks/le1_mutations.py rim skirt    # the ones whose name holds a word
    python3 tools/level_editor_checks/le1_mutations.py --jobs 1

It copies Scripts/, Tests/, tools/ and ThirdParty/FluidHTN to a scratch directory, and there, for
each rule below, breaks the rule in Scripts/Sim/Level -- one edit to one file -- and runs the five
test classes LE1 added. One line a rule: PASS with the tests that failed, or FAIL when every test
still passed, which is a claim nothing holds. The tree it was started from is not written to.

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
CLASSES = ("HeightFieldTests", "TerrainBrushTests", "TerrainConformTests", "TerrainBandsTests", "MapLimitsTests")
FILTER = "|".join(f"FullyQualifiedName~Gdpyr.Tests.{name}" for name in CLASSES)

# (name, file, the rule as broken, the source as it stands, what it is replaced with)
RULES = [
    # ---- section 6 and 6.1: the heightfield -----------------------------------------------------
    ("grid-transposed", "HeightField.cs", "sample (x, z) read from index x * width + z",
     "get => Heights[(z * Width) + x];", "get => Heights[(x * Width) + z];"),
    ("grid-not-centred", "HeightField.cs", "sample 0 at x = 0, not at -120",
     "public float OriginX => -(Width - 1) * 0.5f;", "public float OriginX => 0f;"),
    ("cell-other-diagonal", "HeightField.cs", "a cell split along its other diagonal",
     "return u + v <= 1d\n\t\t\t? h00 + (u * (h10 - h00)) + (v * (h01 - h00))\n\t\t\t: h11 + ((1d - u) * (h01 - h11)) + ((1d - v) * (h10 - h11));",
     "return u >= v\n\t\t\t? h00 + (u * (h10 - h00)) + (v * (h11 - h10))\n\t\t\t: h00 + (v * (h01 - h00)) + (u * (h11 - h01));"),
    ("step-128", "MapLimits.cs", "a step of 1/128 m",
     "public const int HeightStepsPerMeter = 256;", "public const int HeightStepsPerMeter = 128;"),
    ("round-down", "HeightField.cs", "a stored height rounded down, not to the nearest step",
     "float steps = MathF.Round(meters * MapLimits.HeightStepsPerMeter);",
     "float steps = MathF.Floor(meters * MapLimits.HeightStepsPerMeter);"),
    ("round-no-limit", "HeightField.cs", "rounding that does not hold a height to +64 m",
     "return steps >= MapLimits.MaxHeightSample\n\t\t\t? MapLimits.MaxHeightSample",
     "return steps >= short.MaxValue\n\t\t\t? short.MaxValue"),
    ("round-everything", "HeightField.cs", "rounding that reaches past its rectangle",
     "SampleRect inside = samples.Intersect(Bounds);\n\t\tfor (int z = inside.MinZ; z <= inside.MaxZ; z++)\n\t\t{\n\t\t\tfor (int x = inside.MinX; x <= inside.MaxX; x++)\n\t\t\t{\n\t\t\t\tint i = (z * Width) + x;\n\t\t\t\tHeights[i] = ToMeters",
     "SampleRect inside = samples.Grown(1).Intersect(Bounds);\n\t\tfor (int z = inside.MinZ; z <= inside.MaxZ; z++)\n\t\t{\n\t\t\tfor (int x = inside.MinX; x <= inside.MaxX; x++)\n\t\t\t{\n\t\t\t\tint i = (z * Width) + x;\n\t\t\t\tHeights[i] = ToMeters"),
    ("edge-diagonal-run", "HeightField.cs", "a diagonal edge's grade taken over 1 m, not over root 2",
     "_ => MathF.Abs(Heights[i + 1] - Heights[i + Width]) / RootTwo,",
     "_ => MathF.Abs(Heights[i + 1] - Heights[i + Width]),"),
    ("face-is-an-edge", "HeightField.cs", "a face's grade taken as its steeper axis edge, not its gradient's length",
     "return MathF.Sqrt((alongX * alongX) + (alongZ * alongZ));\n\t}\n\n\t/// <summary>The steepest edge with both",
     "return MathF.Max(MathF.Abs(alongX), MathF.Abs(alongZ));\n\t}\n\n\t/// <summary>The steepest edge with both"),
    ("face-second-triangle", "HeightField.cs", "a cell's second triangle given its first one's gradient",
     "float alongX = second ? d - c : b - a;", "float alongX = b - a;"),
    ("normal-one-sided", "HeightField.cs", "a normal by one-sided differences",
     "int west = Math.Max(x - 1, 0);", "int west = Math.Min(x, Width - 2);"),
    ("ray-no-diagonal", "HeightField.cs", "a ray that takes a cell for one plane, not two triangles",
     "double next = Math.Max(Math.Min(Math.Min(laneX.T, laneZ.T), Math.Min(diagonal.T, end)), t);",
     "double next = Math.Max(Math.Min(Math.Min(laneX.T, laneZ.T), end), t);"),
    ("ray-one-sided", "HeightField.cs", "a ray stopped from above only",
     "if (overNext == 0d || (over > 0d) != (overNext > 0d))", "if (overNext == 0d || (over > 0d && overNext < 0d))"),
    ("ray-past-the-edge", "HeightField.cs", "a ray that finds ground past the field's edge",
     "if (!Clip(ox, dx, Width - 1, ref start, ref end) || !Clip(oz, dz, Depth - 1, ref start, ref end))",
     "if (!Clip(ox, dx, Width - 1, ref start, ref end) && !Clip(oz, dz, Depth - 1, ref start, ref end))"),
    ("ray-no-reach", "HeightField.cs", "a ray that goes on past the distance it was given",
     "double end = maxDistance;", "double end = 1e9;"),
    ("off-the-field", "HeightField.cs", "a point off the field's east edge given ground carried on past it",
     "gx = gx > 0d ? Math.Min(gx, Width - 1) : 0d;", "gx = gx > 0d ? Math.Min(gx, Width + 50) : 0d;"),
    ("limit-low", "MapLimits.cs", "ground allowed down to -32 m",
     "public const float MinHeightMeters = -16f;", "public const float MinHeightMeters = -32f;"),
    ("limit-high", "MapLimits.cs", "ground allowed up to +96 m",
     "public const float MaxHeightMeters = 64f;", "public const float MaxHeightMeters = 96f;"),
    ("grid-257", "MapLimits.cs", "a terrain of 257 samples a side",
     "public const int TerrainSamples = 241;", "public const int TerrainSamples = 257;"),
    ("rect-one-short", "HeightField.cs", "an edit's rectangle one sample short of what it touched",
     ": new SampleRect(Math.Min(MinX, x), Math.Min(MinZ, z), Math.Max(MaxX, x), Math.Max(MaxZ, z));",
     ": new SampleRect(Math.Min(MinX, x), Math.Min(MinZ, z), Math.Max(MaxX, x - 1), Math.Max(MaxZ, z));"),

    # ---- section 6.2: brushes --------------------------------------------------------------------
    ("falloff-linear", "TerrainBrush.cs", "a falloff that is a straight line, not a smoothstep",
     "return 1f - (s * s * (3f - (2f * s)));", "return 1f - s;"),
    ("falloff-no-core", "TerrainBrush.cs", "a falloff that ignores the core",
     "core = core > 0f ? MathF.Min(core, 0.999f) : 0f;", "core = 0f;"),
    ("sculpt-formula", "TerrainBrush.cs", "the sculpt grade given as 1.0 * H over the falloff, not 1.5",
     "1.5f * MathF.Abs(rise) / ((1f - core) * radius);", "1f * MathF.Abs(rise) / ((1f - core) * radius);"),
    ("sculpt-no-limit", "TerrainBrush.cs", "a sculpt dab that does not hold to the limits",
     "float after = Math.Clamp(before + (amount * weight), MapLimits.MinHeightMeters, MapLimits.MaxHeightMeters);",
     "float after = before + (amount * weight);"),
    ("rate-per-dab", "TerrainBrush.cs", "a brush that rises by a sixtieth of its rate a dab, whatever the time step",
     "float rise = brush.RateMetersPerSecond * (dt > 0f ? dt : 0f);",
     "float rise = brush.RateMetersPerSecond * (dt > 0f ? 1f / 60f : 0f);"),
    ("sculpt-never-lowers", "TerrainBrush.cs", "a sculpt stroke that raises on either button",
     "stroke.Lower ? -rise : rise),", "rise),"),
    ("locked-never-lowers", "TerrainBrush.cs", "a grade-locked stroke that raises on either button",
     "rise, stroke.Lower),", "rise, false),"),
    ("stroke-unclamped", "TerrainBrush.cs", "a stroke begun with its settings as they were handed over",
     "new(brush.Clamped(), lower, field.Sample(x, z));", "new(brush, lower, field.Sample(x, z));"),
    ("rim-removed", "TerrainBrush.cs", "the rim rule removed: the apex not held by the rim's samples",
     "if (distance > inner && distance <= radius)", "if (distance > inner && distance <= radius && lower == !lower)"),
    ("rim-thin", "TerrainBrush.cs", "a rim thinner than a cell's diagonal",
     "public const float RimMeters = 1.5f;", "public const float RimMeters = 0.5f;"),
    ("tip-sharp", "TerrainBrush.cs", "a cone with a sharp tip",
     "public const float TipRadiusMeters = 2f;", "public const float TipRadiusMeters = 0f;"),
    ("cone-steeper", "TerrainBrush.cs", "a cone steeper than its setting",
     "MathF.Sqrt((distance * distance) + (tip * tip)) - tip;", "(MathF.Sqrt((distance * distance) + (tip * tip)) - tip) * 1.2f;"),
    ("held-is-the-cursor", "TerrainBrush.cs", "a grade-locked brush rising from the height under the cursor",
     "apex = MathF.Min(apex, (sign * field[sx, sz]) + (grade * Cone(Distance(field, sx, sz, x, z), tipRadius)));\n\t\t}",
     "apex = sign * field.Sample(x, z);\n\t\t}"),
    ("lower-limit", "TerrainBrush.cs", "a grade-locked brush lowering past -16 m",
     "float limit = lower ? -MapLimits.MinHeightMeters : MapLimits.MaxHeightMeters;",
     "float limit = MapLimits.MaxHeightMeters;"),
    ("smooth-too-far", "TerrainBrush.cs", "smooth moving a sample twice its weight towards the mean",
     "float after = Towards(was, (float)(sum / neighbours), weight);",
     "float after = Towards(was, (float)(sum / neighbours), weight * 2f);"),
    ("smooth-not-convex", "TerrainBrush.cs", "smooth and level allowed past where they are going",
     "return from <= to ? Math.Clamp(moved, from, to) : Math.Clamp(moved, to, from);",
     "return from + ((to - from) * share * 2.5f);"),
    ("smooth-in-place", "TerrainBrush.cs", "smooth reading neighbours the dab has already written",
     "sum += (double)before[i - 1] + before[i + 1];", "sum += (double)field[sx - 1, sz] + field[sx + 1, sz];"),
    ("smooth-edge", "TerrainBrush.cs", "smooth pulling a sample at the field's edge towards the neighbours across it only",
     "if (sx > 0 && sx < field.Width - 1)\n\t\t\t\t\t{\n\t\t\t\t\t\tsum += (double)before[i - 1] + before[i + 1];\n\t\t\t\t\t\tneighbours += 2;\n\t\t\t\t\t}",
     "if (sx > 0 && sx < field.Width - 1)\n\t\t\t\t\t{\n\t\t\t\t\t\tsum += (double)before[i - 1] + before[i + 1];\n\t\t\t\t\t\tneighbours += 2;\n\t\t\t\t\t}\n\t\t\t\t\telse if (sx > 0)\n\t\t\t\t\t{\n\t\t\t\t\t\tsum += before[i - 1];\n\t\t\t\t\t\tneighbours += 1;\n\t\t\t\t\t}"),
    ("level-follows", "TerrainBrush.cs", "level moving towards the height under the cursor now, not when the stroke began",
     "brush.Strength, stroke.LevelMeters),", "brush.Strength, field.Sample(x, z)),"),
    ("level-half-share", "TerrainBrush.cs", "level moving a sample half its weight towards its height",
     "float after = Towards(before, target, weight);", "float after = Towards(before, target, weight * 0.5f);"),
    ("ramp-level", "TerrainBrush.cs", "a ramp anchored level at its first click's height, not to the line",
     "from + ((to - from) * (along / length))", "from"),
    ("ramp-no-ends", "TerrainBrush.cs", "a ramp's strip run on past its two clicks",
     "if (along >= 0f && along <= length && MathF.Abs(across) <= half)",
     "if (along >= -3f && along <= length + 3f && MathF.Abs(across) <= half)"),
    ("preset-bluff", "TerrainBrush.cs", "the Bluff preset with a core of 0.5",
     'new("Bluff", Sculpt(16f, 0.8f, 2f)),', 'new("Bluff", Sculpt(16f, 0.5f, 2f)),'),
    ("range-radius", "TerrainBrush.cs", "a radius allowed to 128 m",
     "public const float MaxRadiusMeters = 64f;", "public const float MaxRadiusMeters = 128f;"),
    ("range-grade", "TerrainBrush.cs", "a grade allowed to 85 degrees",
     "public const float MaxGradeDegrees = 70f;", "public const float MaxGradeDegrees = 85f;"),

    # ---- section 6.3: levelling ground to a shape ------------------------------------------------
    ("skirt-from-samples", "TerrainConform.cs", "a pad's skirt measured from its levelled samples, not from its rectangle",
     "public static SampleRect Pad(HeightField field, in Footprint footprint, float level, float margin, float grade) =>\n\t\tLevelUnder(field, new PadShape(footprint), level, margin, grade);",
     "public static SampleRect Pad(HeightField field, in Footprint footprint, float level, float margin, float grade)\n\t{\n\t\tvar shape = new PadShape(footprint);\n\t\tvar anchors = new System.Collections.Generic.List<ConformAnchor>();\n\t\tfor (int z = 0; z < field.Depth; z++)\n\t\t{\n\t\t\tfor (int x = 0; x < field.Width; x++)\n\t\t\t{\n\t\t\t\tif (shape.DistanceTo(field.WorldX(x), field.WorldZ(z)) <= margin)\n\t\t\t\t{\n\t\t\t\t\tanchors.Add(new ConformAnchor(x, z, level));\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\n\t\tApply(field, anchors.ToArray(), grade, out SampleRect touched);\n\t\treturn touched;\n\t}"),
    ("skirt-35", "TerrainConform.cs", "a 35 degree skirt",
     "public const float SkirtDegrees = 30f;", "public const float SkirtDegrees = 35f;"),
    ("pad-no-margin", "TerrainConform.cs", "a pad level only under its footprint, with no margin",
     "public const float PadMarginMeters = 1.5f;", "public const float PadMarginMeters = 0f;"),
    ("steep-targets-taken", "TerrainConform.cs", "targets steeper than the skirt taken",
     "if (lower > target + Allowed(target) || upper < target - Allowed(target))",
     "if (lower > target + 100f || upper < target - 100f)"),
    ("two-targets-taken", "TerrainConform.cs", "one sample given two targets, and the later taken",
     "if (!float.IsNaN(targets[i]) && targets[i] != target)", "if (!float.IsNaN(targets[i]) && targets[i] != targets[i])"),
    ("cuts-only", "TerrainConform.cs", "ground cut down to the upper cone and never filled up to the lower",
     "after = MathF.Max(MathF.Min(before, upper), lower);", "after = MathF.Min(before, upper);"),
    ("pad-cuts-only", "TerrainConform.cs", "a pad that cuts and never fills",
     "float after = Math.Clamp(before, level - skirt, level + skirt);", "float after = MathF.Min(before, level + skirt);"),
    ("reach-short", "TerrainConform.cs", "levelling that stops two samples from its shape",
     "int reach = (int)MathF.Ceiling(MathF.Max(ceiling - lowest, highest - floor) / grade) + 1;", "int reach = 2;"),
    ("pad-reach-short", "TerrainConform.cs", "a pad's skirt that stops two metres past its margin",
     "float reach = shape.Radius + margin + (MathF.Max(ceiling - level, level - floor) / grade) + 1f;",
     "float reach = shape.Radius + margin + 2f;"),
    ("skip-too-much", "TerrainConform.cs", "samples left alone that an anchor could still move",
     "if (most <= grade * Distance(shape, x, z))", "if (most <= 3f * grade * Distance(shape, x, z))"),
    ("anchors-near-only", "TerrainConform.cs", "only the anchors within two samples asked",
     "int span = (int)MathF.Ceiling(reach) + 1;", "int span = 2;"),
    ("distance-square", "TerrainConform.cs", "distance to an anchor measured along the longer axis, not flat",
     "float skirt = grade * MathF.Sqrt((dx * dx) + (dz * dz));\n\t\t\t\tlower",
     "float skirt = grade * MathF.Max(MathF.Abs(dx), MathF.Abs(dz));\n\t\t\t\tlower"),
    ("target-no-limit", "TerrainConform.cs", "a target outside the limits taken as it is",
     "float target = Math.Clamp(anchor.Target, MapLimits.MinHeightMeters, MapLimits.MaxHeightMeters);",
     "float target = anchor.Target;"),
    ("level-no-limit", "TerrainConform.cs", "a pad's level outside the limits taken as it is",
     "level = Math.Clamp(level, MapLimits.MinHeightMeters, MapLimits.MaxHeightMeters);", "level += 0f;"),
    ("disc-is-a-point", "TerrainConform.cs", "a disc's pad measured from its centre, not its edge",
     "return MathF.Max(MathF.Sqrt((dx * dx) + (dz * dz)) - Radius, 0f);", "return MathF.Sqrt((dx * dx) + (dz * dz));"),
    ("walkable-45", "MapLimits.cs", "walkable taken as 45 degrees",
     "public const float WalkableSlopeDegrees = 40f;", "public const float WalkableSlopeDegrees = 45f;"),

    # ---- section 6.4: texture bands ----------------------------------------------------------------
    ("band-reordered", "TerrainBands.cs", "a band edge reordered: grass before mud",
     "{ TerrainBand.Sand, TerrainBand.Mud, TerrainBand.Grass, TerrainBand.Stone };",
     "{ TerrainBand.Sand, TerrainBand.Grass, TerrainBand.Mud, TerrainBand.Stone };"),
    ("band-edge-moved", "TerrainBands.cs", "mud giving way to grass at 5 m of u, not 4",
     "MudTopMeters = 4f,", "MudTopMeters = 5f,"),
    ("band-edge-open", "TerrainBands.cs", "an edge passed only once u is over it",
     "(u >= settings.SandTopMeters ? 1 : 0)", "(u > settings.SandTopMeters ? 1 : 0)"),
    ("slope-from-zero", "TerrainBands.cs", "slope under 15 degrees counted against u",
     "MathF.Max(0f, slopeDegrees - settings.SlopeFreeDegrees)", "(slopeDegrees - settings.SlopeFreeDegrees)"),
    ("slope-backwards", "TerrainBands.cs", "steeper giving an earlier band",
     "+ (settings.SlopeGainMetersPerDegree * MathF.Max", "- (settings.SlopeGainMetersPerDegree * MathF.Max"),
    ("slope-gain", "TerrainBands.cs", "a degree of slope worth 0.5 m, not 0.6",
     "SlopeGainMetersPerDegree = 0.6f,", "SlopeGainMetersPerDegree = 0.5f,"),
    ("water-ignored", "TerrainBands.cs", "height taken from 0 m, not from the map's water",
     "(heightMeters - settings.WaterLevelMeters)\n", "heightMeters\n"),
    ("underwater-is-sand", "TerrainBands.cs", "ground under the water given a land band",
     "if (heightMeters < settings.WaterLevelMeters)\n\t\t{\n\t\t\treturn TerrainBand.Water;",
     "if (heightMeters < settings.WaterLevelMeters - 100f)\n\t\t{\n\t\t\treturn TerrainBand.Water;"),
    ("blend-wide", "TerrainBands.cs", "bands blended across 2 m of u, not 1",
     "float half = settings.BlendMeters * 0.5f;", "float half = settings.BlendMeters;"),
    ("blend-slope", "TerrainBands.cs", "the upper band's weight run from 0 to 1 across twice the blend",
     "Math.Clamp(((u - edge) / settings.BlendMeters) + 0.5f, 0f, 1f)",
     "Math.Clamp(((u - edge) / (2f * settings.BlendMeters)) + 0.5f, 0f, 1f)"),
    ("blend-weight", "TerrainBands.cs", "the upper band given no weight until u is on the edge",
     "Math.Clamp(((u - edge) / settings.BlendMeters) + 0.5f, 0f, 1f)", "Math.Clamp((u - edge) / settings.BlendMeters, 0f, 1f)"),
    ("blend-not-neighbours", "TerrainBands.cs", "the drawn pair taken as the band and the one two after it",
     "TerrainBand upper = Land[Passed(settings, edge)];", "TerrainBand upper = Land[Math.Min(Passed(settings, edge) + 1, 3)];"),
    ("numbers-any-size", "TerrainBands.cs", "a band number over what the file holds taken as valid",
     "private static bool InRange(float number) => number >= 0f && number <= MaxNumber;",
     "private static bool InRange(float number) => number >= 0f;"),
    ("edges-any-order", "TerrainBands.cs", "a map's edges taken as valid in any order",
     "&& SandTopMeters <= MudTopMeters && MudTopMeters <= GrassTopMeters;", ";"),
]


def run(command, cwd):
    result = subprocess.run(command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=1800)
    return result.stdout


def test(copy):
    """The five classes, in a copy: (compiled, the tests that failed, how many ran)."""
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

    scratch = tempfile.mkdtemp(prefix="le1-mutations-")
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
