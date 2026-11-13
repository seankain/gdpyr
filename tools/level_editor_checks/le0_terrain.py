#!/usr/bin/env python3
"""LE0's ground and LE0's scene, for scripts/level-editor-checks.sh (docs/LEVEL_EDITOR.md §11).

    python3 tools/level_editor_checks/le0_terrain.py hills <out.i16>
    python3 tools/level_editor_checks/le0_terrain.py scene <Test.tscn>

`hills` writes §10's rolling hills -- design_checks.height_chunks()'s second terrain, the one the
plan's table is computed over -- as the file's samples would be: 241 x 241 int16, little-endian, in
1/256 m, row by row (index z * 241 + x). numpy only, seeded.

`scene` edits a copy of the greybox in place: the 300 m slab and the CSG floor under it come out,
and a StaticBody3D in group `navmesh` with tools/EngineChecks/LevelTerrain.cs on it goes in. It
asserts each thing it removes is there exactly once, so a greybox that has moved fails here.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def hills(path):
    import numpy as np
    from design_checks import noise_terrain, quantize

    n = 241
    h = noise_terrain(n, 12.4, seed=1, lattices=(4, 8, 16, 32, 64, 128)) + 6
    q = quantize(h)
    assert q.shape == (n, n) and q.dtype == np.dtype('<i2')
    with open(path, 'wb') as out:
        out.write(q.tobytes())
    print(f"hills: {q.size} samples, {q.min() / 256:.2f} .. {q.max() / 256:.2f} m, {os.path.getsize(path)} B")


def remove_node(text, header_pattern):
    """Cut one [node ...] block: its header line and everything up to the next section."""
    found = list(re.finditer(header_pattern, text, flags=re.M))
    assert len(found) == 1, f"{header_pattern!r} matches {len(found)} nodes, not 1"
    start = found[0].start()
    following = re.search(r'^\[', text[found[0].end():], flags=re.M)
    end = found[0].end() + following.start() if following else len(text)
    return text[:start] + text[end:]


def scene(path):
    text = open(path).read()
    # The slab: a 300 x 1 x 300 box whose top is y = 0.5, and the mesh drawn for it.
    text = remove_node(text, r'^\[node name="CollisionShape3D" type="CollisionShape3D" parent="StaticBody3D".*\]$')
    text = remove_node(text, r'^\[node name="MeshInstance3D" type="MeshInstance3D" parent="StaticBody3D".*\]$')
    text = remove_node(text, r'^\[node name="StaticBody3D" type="StaticBody3D" parent="\.".*\]$')
    # The CSG floor, 240 x 1 x 240, top at y = 0.499997.
    text = remove_node(text, r'^\[node name="Floor" type="CSGBox3D" parent="CSGCombiner3D".*\]$')

    resource = '[ext_resource type="Script" path="res://EngineChecks/LevelTerrain.cs" id="le0_terrain"]\n'
    first = re.search(r'^\[ext_resource ', text, flags=re.M)
    assert first, "no ext_resource to put the terrain's script beside"
    text = text[:first.start()] + resource + text[first.start():]
    text = text.rstrip('\n') + (
        '\n\n[node name="Terrain" type="StaticBody3D" parent="." groups=["navmesh"]]\n'
        'script = ExtResource("le0_terrain")\n')
    open(path, 'w').write(text)
    print("scene: the slab and the CSG floor are out, Terrain is in")


if __name__ == "__main__":
    if len(sys.argv) != 3 or sys.argv[1] not in ("hills", "scene"):
        sys.exit(__doc__)
    {"hills": hills, "scene": scene}[sys.argv[1]](sys.argv[2])
