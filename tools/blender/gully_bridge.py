"""The Gully's greybox bridge, built in Blender and exported for Godot.

    blender --background --python tools/blender/gully_bridge.py
    python3 tools/blender/gully_bridge.py          # with the `bpy` module (pip install bpy)

Writes Models/Environment/GullyBridge.glb and tools/blender/GullyBridge.blend (the same scene,
to open and push around by hand). Every number is in PARAMS below; change one and run it again.

Blender axes: the span runs along X, the deck's width along Y, up is Z. The origin is the deck's
top surface at mid-span. glTF (and so Godot) is Y-up: the span stays on X, up becomes Y and the
width lies along Z.

Object names carry Godot's import hints: an object ending in `-col` becomes a MeshInstance3D
with a StaticBody3D and a trimesh shape beside it (layer 1, World), so the deck can be walked
and driven on and the railings stop people falling off. The cables are drawn only.

Materials are named `BridgeGreybox` and `BridgeCable`; the .glb's import settings swap them for
Levels/materials/BridgeGreybox.tres and BridgeCable.tres.

The shape follows a reference sketch: a deck on a Warren truss, chunky posts with chamfered
tops at every panel point, and at each end a chevron frame whose upper arm is a pylon leaning
back over the plateau, with stays to the deck, and whose lower arm is a strut down into the cliff.
"""

import math
import os
import sys

import bpy  # first: the pip `bpy` module puts bmesh and mathutils on the path

import bmesh
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
GLB_PATH = os.path.join(REPO, "Models", "Environment", "GullyBridge.glb")
BLEND_PATH = os.path.join(HERE, "GullyBridge.blend")

PARAMS = dict(
    # Deck, from end to end along X. The map puts mid-span at (-10, 70.15, 0), on the plateaus'
    # midline; across the deck's 16 m the rims are 55-60 m from mid-span. A whole number of half
    # panels, so the railing posts end at the deck's ends.
    deck_half_length=66.0,
    ramp_length=2.0,          # beyond each end: down to ground level
    ramp_drop=0.2,            # the deck's top stands this far above the plateau
    deck_half_width=8.0,
    deck_thickness=0.5,
    curb_height=0.25,
    curb_width=0.4,
    # Truss under the gap: 12 panels, ending at the cliffs.
    truss_half_length=52.8,
    panel=8.8,
    truss_depth=3.5,
    chord=0.6,
    diagonal=0.45,
    vertical=0.7,
    post_height=1.35,         # panel-point posts, above the deck
    # Railing between the posts.
    rail_height=1.05,
    rail=(0.22, 0.3),
    mid_rail_height=0.5,
    small_post=0.28,
    small_post_height=1.15,
    # End frames.
    knee=48.0,                # where the chevron meets the deck, from mid-span
    frame_offset=9.2,         # the frames stand this far out from the centre line
    pylon_top=(59.5, 26.0),   # (x from mid-span, height)
    pylon_section=(1.5, 1.0),
    pylon_top_section=(0.9, 0.8),
    pylon_tip=3.0,            # how much higher the outer face's top is than the inner's
    strut_foot=(57.0, -15.0),
    strut_section=(1.3, 0.9),
    # Stays: where each pylon's cables meet the deck, from mid-span (a metre past a panel point).
    stay_anchors=(36.2, 27.4, 18.6),
    cable_radius=0.07,
    bevel=0.07,
)


# --------------------------------------------------------------------------- geometry helpers


def frame_between(p0, p1, up=Vector((0, 0, 1))):
    """A matrix whose X runs p0→p1, centred between them, Z as near `up` as it can be."""
    p0, p1 = Vector(p0), Vector(p1)
    x = (p1 - p0).normalized()
    if abs(x.dot(up)) > 0.999:
        up = Vector((0, 1, 0))
    z = (up - x * up.dot(x)).normalized()
    y = z.cross(x)
    m = Matrix((x, y, z)).transposed().to_4x4()
    m.translation = (p0 + p1) / 2
    return m, (p1 - p0).length


def add_box(bm, size, matrix=Matrix.Identity(4)):
    sx, sy, sz = size
    verts = [
        bm.verts.new(matrix @ Vector((x * sx / 2, y * sy / 2, z * sz / 2)))
        for x in (-1, 1) for y in (-1, 1) for z in (-1, 1)
    ]
    faces = ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3))
    return [bm.faces.new([verts[i] for i in f]) for f in faces], verts


def box_at(bm, center, size):
    return add_box(bm, size, Matrix.Translation(Vector(center)))


def beam(bm, p0, p1, width, height, up=Vector((0, 0, 1))):
    m, length = frame_between(p0, p1, up)
    return add_box(bm, (length, width, height), m)


def tapered_beam(bm, p0, p1, base, top, tip=0.0, up=Vector((0, 0, 1))):
    """A beam p0→p1 whose section shrinks from `base` to `top` (each (w, h)).

    `tip` pulls the top end's -Z face back along the axis by that much, so the top is cut on a
    slant and the +Z face (the side `up` points to) ends in a point.
    """
    m, length = frame_between(p0, p1, up)
    verts = []
    for x in (-1, 1):
        w, h = base if x < 0 else top
        for y in (-1, 1):
            for z in (-1, 1):
                along = x * length / 2
                if x > 0 and z < 0:
                    along -= tip
                verts.append(bm.verts.new(m @ Vector((along, y * w / 2, z * h / 2))))
    faces = ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3))
    return [bm.faces.new([verts[i] for i in f]) for f in faces], verts


def chamfered_post(bm, x, y, bottom, top, size, cap=0.35):
    """A square post with its top cut to a shallow pyramid."""
    half = size / 2
    corners = [(-half, -half), (half, -half), (half, half), (-half, half)]
    lower = [bm.verts.new((x + cx, y + cy, bottom)) for cx, cy in corners]
    upper = [bm.verts.new((x + cx, y + cy, top - cap)) for cx, cy in corners]
    inset = half * 0.45
    crown = [bm.verts.new((x + cx * inset / half, y + cy * inset / half, top)) for cx, cy in corners]
    bm.faces.new(list(reversed(lower)))
    for i in range(4):
        j = (i + 1) % 4
        bm.faces.new((lower[i], lower[j], upper[j], upper[i]))
        bm.faces.new((upper[i], upper[j], crown[j], crown[i]))
    bm.faces.new(crown)


def cylinder(bm, p0, p1, radius, segments=8):
    m, length = frame_between(p0, p1)
    # create_cone builds along Z; turn it onto the frame's X.
    turn = Matrix.Rotation(math.radians(90), 4, "Y")
    bmesh.ops.create_cone(bm, cap_ends=True, segments=segments, radius1=radius, radius2=radius,
                          depth=length, matrix=m @ turn)


def sphere(bm, center, radius):
    bmesh.ops.create_icosphere(bm, subdivisions=1, radius=radius, matrix=Matrix.Translation(Vector(center)))


# --------------------------------------------------------------------------- the bridge


def build_deck(bm, p):
    L, W, T = p["deck_half_length"], p["deck_half_width"], p["deck_thickness"]
    # Faces that would lie in one plane are kept a few centimetres apart: coplanar faces
    # flicker in Godot (z-fighting) and render black in Cycles.
    slab = W - 0.1
    box_at(bm, (0, 0, -T / 2), (2 * L, 2 * slab, T))
    # Ramps down to the plateau at both ends.
    for s in (-1, 1):
        x0, x1 = s * L, s * (L + p["ramp_length"])
        top0, top1 = 0.0, -p["ramp_drop"]
        v = [bm.verts.new(c) for c in (
            (x0, -slab, -T), (x0, slab, -T), (x0, slab, top0), (x0, -slab, top0),
            (x1, -slab, top1 - 0.05), (x1, slab, top1 - 0.05), (x1, slab, top1), (x1, -slab, top1),
        )]
        faces = ((0, 1, 2, 3), (4, 7, 6, 5), (3, 2, 6, 7), (0, 4, 5, 1), (0, 3, 7, 4), (1, 5, 6, 2))
        for f in faces:
            bm.faces.new([v[i] for i in f])
    # Curbs along both edges, the whole length, standing 5 cm proud of the posts.
    for s in (-1, 1):
        y = s * (W + 0.05 - p["curb_width"] / 2)
        box_at(bm, (0, y, p["curb_height"] / 2 - 0.5), (2 * L, p["curb_width"], p["curb_height"] + 1.0))
    # Cross girders under the deck at each panel point.
    n = int(round(p["truss_half_length"] / p["panel"]))
    for i in range(-n, n + 1):
        box_at(bm, (i * p["panel"], 0, -T - 0.35), (0.45, 2 * slab, 0.7))


def panel_points(p):
    n = int(round(p["truss_half_length"] / p["panel"]))
    return [i * p["panel"] for i in range(-n, n + 1)]


def build_truss(bm, p):
    W, T, D = p["deck_half_width"], p["deck_thickness"], p["truss_depth"]
    xs = panel_points(p)
    bottom = -T - D
    for s in (-1, 1):
        y = s * (W - p["vertical"] / 2)
        # Bottom chord.
        box_at(bm, (0, y, bottom), (2 * p["truss_half_length"], p["chord"], p["chord"]))
        # Diagonals: a W under every panel.
        for a, b in zip(xs, xs[1:]):
            mid = (a + b) / 2
            beam(bm, (a, y, -T), (mid, y, bottom), p["diagonal"], p["diagonal"], up=Vector((0, s, 0)))
            beam(bm, (mid, y, bottom), (b, y, -T), p["diagonal"], p["diagonal"], up=Vector((0, s, 0)))
    # Bottom lateral bracing between the two planes, zig-zagging.
    for i, (a, b) in enumerate(zip(xs, xs[1:])):
        y0 = (W - 0.4) * (1 if i % 2 else -1)
        beam(bm, (a, y0, bottom), (b, -y0, bottom), 0.25, 0.25)


def build_posts(bm, p):
    """Panel-point posts (the truss verticals carried up through the deck) and the railing."""
    W, T, D = p["deck_half_width"], p["deck_thickness"], p["truss_depth"]
    xs = panel_points(p)
    for s in (-1, 1):
        y = s * (W - p["vertical"] / 2)
        for x in xs:
            chamfered_post(bm, x, y, -T - D - p["chord"] / 2, p["post_height"], p["vertical"])
        # Small posts every half panel, out to the deck's ends.
        L = p["deck_half_length"]
        x = -L
        while x <= L + 1e-6:
            if min(abs(x - px) for px in xs) > 1.0:
                chamfered_post(bm, x, s * (W - p["small_post"] / 2 - 0.06), 0.0,
                               p["small_post_height"], p["small_post"], cap=0.18)
            x += p["panel"] / 2
        ry = s * (W - 0.2)
        rw, rh = p["rail"]
        box_at(bm, (0, ry, p["rail_height"]), (2 * L, rw, rh))
        box_at(bm, (0, ry, p["mid_rail_height"]), (2 * L, rw * 0.7, rh * 0.6))


def build_frames(bm, p):
    T = p["deck_thickness"]
    for sx in (-1, 1):
        knee = Vector((sx * p["knee"], 0, -T / 2))
        top_x, top_z = p["pylon_top"]
        foot_x, foot_z = p["strut_foot"]
        for sy in (-1, 1):
            y = sy * p["frame_offset"]
            k = knee + Vector((0, y, 0))
            # Upper arm: a pylon leaning back over the plateau, its top slanted outward.
            tapered_beam(bm, k, (sx * top_x, y, top_z), p["pylon_section"], p["pylon_top_section"],
                         tip=p["pylon_tip"], up=Vector((sx, 0, 0)))
            # Lower arm: a strut down into the cliff.
            beam(bm, k, (sx * foot_x, y, foot_z), *p["strut_section"], up=Vector((-sx, 0, 0)))
            # The knuckle where they meet the deck.
            box_at(bm, (k.x, y, -0.1), (2.4, 1.6, 2.6))
        # A cross beam under the deck between the two knees.
        box_at(bm, (sx * p["knee"], 0, -T - 0.6), (1.2, 2 * p["frame_offset"] + 1.2, 1.0))


def stay_top(p, sx, sy):
    """Where a pylon's stays leave it: on its span-side face, a metre under that face's top."""
    top_x, top_z = p["pylon_top"]
    knee = Vector((sx * p["knee"], sy * p["frame_offset"], -p["deck_thickness"] / 2))
    m, length = frame_between(knee, (sx * top_x, sy * p["frame_offset"], top_z), Vector((sx, 0, 0)))
    along = length / 2 - p["pylon_tip"] - 1.0
    depth = (p["pylon_section"][1] + p["pylon_top_section"][1]) / 4
    return m @ Vector((along, 0, -depth))


def build_cables(bm, p):
    W = p["deck_half_width"]
    r = p["cable_radius"]
    for sx in (-1, 1):
        for sy in (-1, 1):
            top = stay_top(p, sx, sy)
            sphere(bm, top, 0.35)
            for ax in p["stay_anchors"]:
                anchor = Vector((sx * ax, sy * (W + 0.15), 0.1))
                cylinder(bm, top, anchor, r)
                # Turnbuckles a third and two thirds of the way down, and a shoe at the deck.
                for f in (0.33, 0.66):
                    a = top.lerp(anchor, f - 0.012)
                    b = top.lerp(anchor, f + 0.012)
                    cylinder(bm, a, b, r * 2.6)
                box_at(bm, anchor + Vector((0, 0, -0.05)), (0.7, 0.5, 0.4))


# --------------------------------------------------------------------------- scene


def material(name, color):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = color
        bsdf.inputs["Roughness"].default_value = 0.8
    mat.diffuse_color = color
    return mat


def make_object(name, build, mat, bevel=0.0):
    bm = bmesh.new()
    build(bm, PARAMS)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    for poly in mesh.polygons:
        poly.use_smooth = False
    mesh.materials.append(mat)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    if bevel > 0:
        mod = obj.modifiers.new("Chamfer", "BEVEL")
        mod.width = bevel
        mod.segments = 1
        mod.limit_method = "ANGLE"
        mod.angle_limit = math.radians(40)
        mod.harden_normals = False
    return obj


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"

    grey = material("BridgeGreybox", (0.62, 0.62, 0.6, 1.0))
    dark = material("BridgeCable", (0.12, 0.12, 0.13, 1.0))

    make_object("Deck-col", build_deck, grey)
    make_object("Truss-col", build_truss, grey, PARAMS["bevel"])
    make_object("Posts-col", build_posts, grey, PARAMS["bevel"])
    make_object("Frames-col", build_frames, grey, PARAMS["bevel"])
    make_object("Cables", build_cables, dark)

    os.makedirs(os.path.dirname(GLB_PATH), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH, compress=True)
    bpy.ops.export_scene.gltf(
        filepath=GLB_PATH,
        export_format="GLB",
        export_apply=True,
        export_yup=True,
        export_materials="EXPORT",
        export_normals=True,
        export_texcoords=False,
        export_cameras=False,
        export_lights=False,
    )

    tris = 0
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for obj in scene.objects:
        mesh = obj.evaluated_get(depsgraph).to_mesh()
        mesh.calc_loop_triangles()
        tris += len(mesh.loop_triangles)
        print(f"  {obj.name:12s} {len(mesh.loop_triangles):6d} tris  dims {tuple(round(d, 2) for d in obj.dimensions)}")
    print(f"wrote {GLB_PATH} ({tris} tris) and {BLEND_PATH}")


if __name__ == "__main__":
    main()
    sys.stdout.flush()
