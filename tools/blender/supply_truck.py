"""The supply truck: a low-poly, faceted six-wheeled cargo truck with a canvas tilt over its bed,
built in Blender and exported for Godot.

    python3 tools/blender/supply_truck.py          # with the `bpy` module (pip install bpy)
    blender --background --python tools/blender/supply_truck.py

Writes two files, both from what is below and nothing else:

    Models/Vehicles/supply_truck/supply_truck.glb    the model Units/supply_truck.tres draws it with
    tools/blender/SupplyTruck.blend                  the same scene, to open by hand

Every dimension is in PARAMS; change one and run it again. `--render DIR` also writes preview
renders (front three-quarter, side, top, rear three-quarter) into DIR.

The shape follows a concept rendering: a bonneted cab-behind-engine truck on three axles, single
big-lugged tyres all round; a long crowned bonnet with a four-slot grille, angular front wings
over the front wheels, a heavy bumper with round lamps on it, two rectangular recesses, three round
holes and a towing eye; a square cab with a two-pane windscreen under a peaked roof, doors with
windows, and tall mirrors on braced arms; a fuel tank behind the cab; and a dropside bed of louvred
panels between stakes, with an olive canvas tilt over bows, scalloped where it hangs between them.
Sand-coloured, with dark running gear. No markings.

Axes. Blender: +Y is the front, +Z is up, +X is the truck's right. glTF (and so Godot) is Y-up and
looks down -Z, so the exporter's (x, y, z) -> (x, z, -y) turns the bumper to Godot's forward, which
is where a unit at yaw 0 faces (Aim.Angles). The origin is on the ground under the middle of the
truck's length and width: a CharacterBody3D's position is its feet.

One mesh, `Truck`, at the origin: nothing on it turns on its own in the game.
"""

import math
import os
import sys

import bpy  # first: the pip `bpy` module puts bmesh and mathutils on the path

import bmesh
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT_DIR = os.path.join(REPO, "Models", "Vehicles", "supply_truck")
GLB_PATH = os.path.join(OUT_DIR, "supply_truck.glb")
BLEND_PATH = os.path.join(HERE, "SupplyTruck.blend")

PARAMS = dict(
    # Running gear. Axles along Y; wheels' middles at +-track.
    axles=(2.52, -1.48, -2.88),
    track=1.00,
    wheel_radius=0.575,        # to the tops of the lugs
    lug_depth=0.045,
    tyre_half_width=0.20,
    lugs=18,                   # round each tyre, alternately either side of its middle
    frame=(0.49, 0.80, 1.02),  # the chassis rails: x of their middles, bottom, top
    frame_ends=(-3.62, 3.40),
    # The bumper: y0, y1, bottom, top, half-width.
    bumper=(3.34, 3.62, 0.68, 0.96, 1.18),
    headlight=(0.86, 3.44, 1.08, 0.11),   # x, y, z, radius
    # The bonnet: the cowl's y and the front's; its sections are in build_bonnet.
    bonnet=(1.45, 3.34),
    grille=(3.30, 0.74, 0.90, 1.50),      # y of its face, half-width, bottom, top
    # The front wings: one loft round each front wheel, (angle from the front, radius of the top).
    wing=(0.70, 0.74, 1.27),              # radius of the wheel arch, x inside and outside
    wing_stations=((12, 0.80), (40, 1.00), (68, 0.88), (112, 0.86), (140, 0.96), (172, 1.10)),
    # The cab: y0, y1, bottom, roof, half-width.
    cab=(-0.18, 1.45, 0.92, 2.58, 1.08),
    windscreen=(1.90, 2.50, 0.07),        # bottom, top, how far the top stands back
    # The bed: y0, y1, underside, floor, half-width.
    bed=(-3.72, -0.32, 1.22, 1.34, 1.24),
    side_top=1.94,             # the top of the drop sides
    rail_top=2.22,             # the rail the tilt is tied down to
    bays=4,                    # between stakes, along each side
    slats=4,                   # louvres in each bay
    # The tilt: ridge over a bow and between bows; its hem at a bow and between bows.
    tilt=(2.80, 2.74, 2.10, 2.00),
    tilt_half_width=1.29,
)


# --------------------------------------------------------------------------------------------
# Materials


def srgb(r, g, b):
    """A colour as an artist picks it (sRGB, 0-255), as Blender stores it (linear)."""

    def lin(c):
        c /= 255.0
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

    return (lin(r), lin(g), lin(b), 1.0)


def material(name, rgba, metallic=0.0, roughness=0.75):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = rgba
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    mat.diffuse_color = rgba
    return mat


MATS = {}


def make_materials():
    # Matte paint, as the tank's is.
    MATS["sand"] = material("TruckSand", srgb(196, 172, 130))
    MATS["canvas"] = material("TruckCanvas", srgb(82, 84, 64), roughness=0.95)
    MATS["dark"] = material("TruckDark", srgb(50, 50, 48), roughness=0.8)
    MATS["tyre"] = material("TruckTyre", srgb(46, 46, 46), roughness=0.95)
    MATS["hub"] = material("TruckHub", srgb(72, 72, 70), metallic=0.2, roughness=0.6)
    MATS["glass"] = material("TruckGlass", srgb(58, 70, 82), metallic=0.2, roughness=0.25)
    MATS["black"] = material("TruckBlack", srgb(14, 14, 14), roughness=1.0)
    MATS["lens"] = material("TruckLens", srgb(214, 214, 200), roughness=0.3)
    MATS["tail"] = material("TruckTailLamp", srgb(150, 30, 24), roughness=0.4)


# --------------------------------------------------------------------------------------------
# Geometry (the same helpers as tools/blender/tank.py)


X, Y, Z = Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))


def ring(center, u, v, ru, rv, n, phase=0.0):
    """n points round `center` in the plane of u and v, an ellipse of radii ru and rv."""
    return [center + u * (ru * math.cos(phase + 2 * math.pi * i / n)) + v * (rv * math.sin(phase + 2 * math.pi * i / n))
            for i in range(n)]


class Part:
    """One mesh under construction: a bmesh and its faces' materials, by key."""

    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.slots = []

    def slot(self, key):
        if key not in self.slots:
            self.slots.append(key)
        return self.slots.index(key)

    def loft(self, rings, mat, cap_start=True, cap_end=True, closed=True):
        """Quads between consecutive rings, all of one length; caps across the first and last."""
        bm = self.bm
        idx = self.slot(mat)
        verts = [[bm.verts.new(p) for p in r] for r in rings]
        n = len(rings[0])
        faces = []
        for a, b in zip(verts, verts[1:]):
            for i in range(n if closed else n - 1):
                j = (i + 1) % n
                faces.append(bm.faces.new((a[i], a[j], b[j], b[i])))
        if cap_start:
            faces.append(bm.faces.new(list(reversed(verts[0]))))
        if cap_end:
            faces.append(bm.faces.new(verts[-1]))
        for f in faces:
            f.material_index = idx
        return faces

    def hull(self, points, mat):
        """The convex hull of `points`: how a faceted lump is made from its corners."""
        bm = self.bm
        idx = self.slot(mat)
        verts = [bm.verts.new(p) for p in points]
        result = bmesh.ops.convex_hull(bm, input=verts)
        # A point can be both inside and unused (a lug's corner on another face's plane): once.
        loose = {g: None for g in result["geom_interior"] + result["geom_unused"] if isinstance(g, bmesh.types.BMVert)}
        bmesh.ops.delete(bm, geom=list(loose), context="VERTS")
        faces = [g for g in result["geom"] if isinstance(g, bmesh.types.BMFace)]
        for f in faces:
            f.material_index = idx
        # Coplanar triangles back into one face: flat shading shows every seam otherwise.
        bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(1.0), verts=list({v for f in faces for v in f.verts}),
                                 edges=list({e for f in faces for e in f.edges}))
        return faces

    def box(self, center, half, mat):
        cx, cy, cz = center
        hx, hy, hz = half
        return self.hull([Vector((cx + sx * hx, cy + sy * hy, cz + sz * hz))
                          for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)], mat)

    def span(self, lo, hi, mat):
        """A box from one corner to the other."""
        lo, hi = Vector(lo), Vector(hi)
        return self.box((lo + hi) / 2, (hi - lo) / 2, mat)

    def cylinder(self, a, b, r, n, mat, phase=None, r_end=None):
        """A closed prism (or frustum, with `r_end`) of n sides from point a to point b."""
        a, b = Vector(a), Vector(b)
        axis = (b - a).normalized()
        u = (Z if abs(axis.dot(Z)) < 0.9 else X).cross(axis).normalized()
        v = axis.cross(u)
        if phase is None:
            phase = math.pi / n
        return self.loft([ring(a, u, v, r, r, n, phase), ring(b, u, v, r_end or r, r_end or r, n, phase)], mat)

    def slab(self, corners, normal, out, depth, mat):
        """A plate on a surface: `corners` on it, standing `out` proud along `normal`, `depth` into it."""
        n = Vector(normal).normalized()
        return self.hull([Vector(c) + n * out for c in corners] + [Vector(c) - n * depth for c in corners], mat)

    def finish(self, parent=None, location=(0, 0, 0)):
        bm = self.bm
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        mesh = bpy.data.meshes.new(self.name)
        bm.to_mesh(mesh)
        bm.free()
        for key in self.slots:
            mesh.materials.append(MATS[key])
        for poly in mesh.polygons:
            poly.use_smooth = False
        obj = bpy.data.objects.new(self.name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        obj.parent = parent
        obj.location = location
        return obj


def mirrored(points):
    """Points given for the right side (+X), and the same for the left."""
    return [Vector(q) for q in points] + [Vector((-q[0], q[1], q[2])) for q in points]


# --------------------------------------------------------------------------------------------
# Running gear: chassis rails, axles, and six wheels


LUG_SPAN = 0.68   # of a lug's pitch round the tyre, the lug's; the rest is the gap to the next


def axle_height(p):
    """Where a wheel's axle stands so that the lug under it rests on the ground."""
    return p["wheel_radius"] * math.cos(LUG_SPAN * math.pi / p["lugs"])


def build_wheel(part, cx, cy, side, p):
    """One wheel, its outside towards `side`: a lugged tyre round a dished rim and a hub."""
    R, hw, n = p["wheel_radius"], p["tyre_half_width"], p["lugs"]
    t = R - p["lug_depth"]               # the carcass, under the lugs
    cz = axle_height(p)
    pitch = 2 * math.pi / n
    first = -math.pi / 2                 # a lug square under the axle

    def at(dx, r, a):
        return Vector((cx + side * dx, cy + r * math.cos(a), cz + r * math.sin(a)))

    def rings(profile):
        # Vertices half a pitch off the lugs, so every lug sits on a flat.
        return [[at(dx, r, first + pitch * (i + 0.5)) for i in range(n)] for dx, r in profile]

    # The carcass, inside sidewall to outside sidewall, shouldered at the tread.
    part.loft(rings([(-hw, 0.34), (-hw, t - 0.07), (-hw + 0.05, t), (hw - 0.05, t), (hw, t - 0.07), (hw, 0.34)]),
              "tyre", cap_start=False, cap_end=False)
    # The rim, dished in on both faces.
    part.loft(rings([(hw, 0.34), (hw - 0.06, 0.31)]), "hub", cap_start=False)
    part.loft(rings([(-hw, 0.34), (-hw + 0.06, 0.31)]), "hub", cap_start=False)
    # The hub: a boss and an eight-sided cap on it.
    part.cylinder(at(hw - 0.07, 0, 0), at(hw - 0.01, 0, 0), 0.17, 8, "hub")
    part.cylinder(at(hw - 0.02, 0, 0), at(hw + 0.035, 0, 0), 0.095, 8, "dark")

    # Lugs: a block over half the tread, then the other half, round and round; each wraps over
    # its shoulder and stands a little proud of the sidewall, as a mud tyre's do.
    half = LUG_SPAN * pitch / 2
    for k in range(n):
        s = 1 if k % 2 == 0 else -1
        a = first + pitch * k
        pts = []
        for b in (a - half, a + half):
            for dx, r in ((-0.03, t - 0.02), (-0.03, R), (hw - 0.05, R), (hw + 0.01, R - 0.045),
                          (hw + 0.01, t - 0.10), (hw - 0.04, t - 0.02)):
                pts.append(at(s * dx, r, b))
        part.hull(pts, "tyre")


def build_running_gear(part, p):
    fx, fz0, fz1 = p["frame"]
    y0, y1 = p["frame_ends"]
    cz = axle_height(p)
    for side in (-1, 1):
        part.span((side * fx - 0.07, y0, fz0), (side * fx + 0.07, y1, fz1), "dark")
    # Cross members between the rails.
    for y in (y1 - 0.10, 1.60, -0.25, -2.18, y0 + 0.10):
        part.span((-fx, y - 0.05, fz0 + 0.03), (fx, y + 0.05, fz1 - 0.03), "dark")

    for i, y in enumerate(p["axles"]):
        # The axle, its differential, and its springs up to the rails.
        part.cylinder((-p["track"] + 0.18, y, cz), (p["track"] - 0.18, y, cz), 0.085, 8, "dark")
        part.hull([Vector((sx * 0.20, y + sy * 0.20, cz + sz)) for sx in (-1, 1) for sy in (-1, 1)
                   for sz in (-0.17, 0.12)] + [Vector((0, y + 0.30, cz)), Vector((0, y - 0.24, cz + 0.05))], "dark")
        for side in (-1, 1):
            build_wheel(part, side * p["track"], y, side, p)
    # The rear bogie's balance beams, axle to axle under each rail, and their trunnion brackets.
    ya, yb = p["axles"][1], p["axles"][2]
    for side in (-1, 1):
        part.span((side * fx - 0.06, yb - 0.10, cz - 0.02), (side * fx + 0.06, ya + 0.10, cz + 0.14), "dark")
        part.span((side * fx - 0.08, (ya + yb) / 2 - 0.16, cz + 0.10), (side * fx + 0.08, (ya + yb) / 2 + 0.16, fz0), "dark")
    # Front leaf springs.
    yf = p["axles"][0]
    for side in (-1, 1):
        part.hull([Vector((side * fx + dx, y, z)) for dx in (-0.06, 0.06) for y, z in (
            (yf - 0.65, fz0 - 0.02), (yf + 0.60, fz0 - 0.02), (yf - 0.25, cz + 0.08), (yf + 0.20, cz + 0.08),
            (yf - 0.65, fz0 + 0.04), (yf + 0.60, fz0 + 0.04))], "dark")


# --------------------------------------------------------------------------------------------
# The front: bumper, lamps, grille, bonnet, wings


def build_bumper(part, p):
    y0, y1, z0, z1, hw = p["bumper"]
    # A deep beam, its top edge chamfered back and its ends swept back.
    part.hull(mirrored([
        (hw, y0, z0), (hw, y0, z1), (hw, y1 - 0.12, z0 + 0.01), (hw, y1 - 0.14, z1),
        (hw - 0.12, y1, z0), (hw - 0.12, y1, z1 - 0.06), (hw - 0.15, y1 - 0.06, z1)]), "sand")
    # Brackets back to the rails.
    for side in (-1, 1):
        part.span((side * p["frame"][0] - 0.06, 3.30, 0.78), (side * p["frame"][0] + 0.06, y0 + 0.01, 0.94), "dark")

    face = y1
    zc = (z0 + z1 - 0.06) / 2
    # Right of the middle: two rectangular recesses.
    for x in (0.40, 0.76):
        part.span((x - 0.15, face - 0.01, zc - 0.055), (x + 0.15, face + 0.004, zc + 0.055), "black")
    # Left of it: three round holes in raised collars.
    for x in (-0.30, -0.56, -0.82):
        part.cylinder((x, face - 0.01, zc), (x, face + 0.02, zc), 0.085, 8, "sand")
        part.cylinder((x, face + 0.02, zc), (x, face + 0.026, zc), 0.055, 8, "black")
    # A towing eye at the right-hand end: two cheeks and a pin.
    for x in (0.99, 1.09):
        part.span((x - 0.012, face - 0.12, zc - 0.13), (x + 0.012, face + 0.06, zc + 0.11), "dark")
    part.cylinder((0.97, face + 0.01, zc - 0.02), (1.11, face + 0.01, zc - 0.02), 0.028, 6, "dark")

    # Round lamps standing on the bumper's top.
    lx, ly, lz, lr = p["headlight"]
    for side in (-1, 1):
        x = side * lx
        part.span((x - 0.035, ly - 0.05, z1 - 0.01), (x + 0.035, ly + 0.03, lz - lr + 0.02), "dark")
        part.cylinder((x, ly - 0.09, lz), (x, ly + 0.07, lz), lr, 8, "dark", r_end=lr * 0.96)
        part.cylinder((x, ly + 0.07, lz), (x, ly + 0.085, lz), lr * 0.78, 8, "lens")


def build_grille(part, p):
    y, hw, z0, z1 = p["grille"]
    d = 0.05  # how far the bars stand out of the dark behind them
    # The dark of the radiator behind, and an engine block so nothing shows through.
    part.span((-hw + 0.02, y - 0.06, z0 + 0.02), (hw - 0.02, y - 0.02, z1 - 0.02), "black")
    part.span((-0.70, 1.55, 0.72), (0.70, y - 0.06, 1.34), "dark")
    # The frame and three dividers: four tall slots.
    bar = 0.07
    part.span((-hw, y - 0.04, z1 - bar), (hw, y + d, z1), "sand")
    part.span((-hw, y - 0.04, z0), (hw, y + d, z0 + bar), "sand")
    inner = hw - bar
    slot = (2 * inner - 3 * bar) / 4
    for i in range(5):
        x = -hw + i * (slot + bar) if i < 4 else inner
        part.span((x, y - 0.04, z0 + bar), (x + bar, y + d, z1 - bar), "sand")


def bonnet_section(y, side, shoulder, crown_half, top, crown):
    """A bonnet cross-section at `y`: (half-width, z) of its side's top, its shoulder and the edge of
    its crown, mirrored, with the crown's ridge between; closed underneath at the wings' height."""
    bottom = 1.28
    right = [(side, bottom), (side, shoulder[1]), (shoulder[0], top - 0.10), (crown_half, top)]
    pts = right + [(0.0, top + crown)] + [(-x, z) for x, z in reversed(right)] + [(0.0, bottom)]
    return [Vector((x, y, z)) for x, z in pts]


def build_bonnet(part, p):
    cowl, front = p["bonnet"]
    part.loft([
        bonnet_section(cowl, 0.82, (0.68, 1.64), 0.38, 1.94, 0.05),
        bonnet_section(2.25, 0.82, (0.67, 1.62), 0.37, 1.91, 0.05),
        bonnet_section(2.98, 0.78, (0.62, 1.55), 0.33, 1.81, 0.04),
        bonnet_section(front, 0.68, (0.52, 1.46), 0.28, 1.66, 0.02),
    ], "sand")


def build_wings(part, p):
    r_in, x_in, x_out = p["wing"]
    cy, cz = p["axles"][0], axle_height(p)
    for side in (-1, 1):
        rings = []
        for k, (deg, r_top) in enumerate(p["wing_stations"]):
            a = math.radians(deg)
            flare = 0.06 if deg < 60 else 0.0          # the front corners stand out a little

            def at(x, r):
                return Vector((side * x, cy + r * math.cos(a), cz + r * math.sin(a)))

            # Inside the arch, out to the edge; the top slopes down to the outside.
            rings.append([at(x_in, r_in), at(x_out + flare, r_in), at(x_out + flare, r_top - 0.07), at(x_in, r_top)])
        part.loft(rings, "sand")


# --------------------------------------------------------------------------------------------
# The cab


def build_cab(part, p):
    y0, y1, z0, roof, hw = p["cab"]
    wz0, wz1, back = p["windscreen"]
    c = 0.08
    part.hull(mirrored([
        (hw, y0, z0), (hw, y0, roof - c), (hw - c, y0, roof),
        (hw, y1, z0), (hw, y1, wz0), (hw, y1 - back, wz1), (hw - c, y1 - back - 0.03, roof)]), "sand")
    # The roof, peaked forward over the windscreen.
    part.hull(mirrored([
        (hw + 0.04, y0 - 0.04, roof - 0.03), (hw + 0.04, y0 - 0.04, roof + 0.03), (hw - 0.04, y0 - 0.04, roof + 0.07),
        (hw + 0.04, y1 + 0.05, roof - 0.03), (hw + 0.04, y1 + 0.05, roof + 0.03), (hw - 0.04, y1 + 0.01, roof + 0.07)]),
        "sand")

    # The windscreen: two panes in the raked front, a post between.
    tangent = Vector((0, -back, wz1 - wz0)).normalized()
    normal = Vector((0, tangent.z, -tangent.y))

    def on_front(x, z):
        return (x, y1 - back * (z - wz0) / (wz1 - wz0), z)

    for side in (-1, 1):
        xa, xb = side * 0.07, side * (hw - 0.10)
        part.slab([on_front(xa, wz0 + 0.07), on_front(xb, wz0 + 0.07), on_front(xa, wz1 - 0.06), on_front(xb, wz1 - 0.06)],
                  normal, 0.006, 0.02, "glass")

    # Doors: a panel standing proud of each side, a window in it, a handle, a step under it.
    for side in (-1, 1):
        x = side * hw
        n = Vector((side, 0, 0))
        part.slab([(x, 0.28, z0 + 0.06), (x, 1.40, z0 + 0.06), (x, 0.28, 2.46), (x, 1.36, 2.46)], n, 0.012, 0.02, "sand")
        part.slab([(x, 0.40, 1.86), (x, 1.28, 1.86), (x, 0.40, 2.38), (x, 1.26, 2.38)], n, 0.018, 0.02, "glass")
        part.span((x + side * 0.01 - 0.025, 0.36, 1.74), (x + side * 0.01 + 0.025, 0.52, 1.78), "dark")
        part.span((side * (hw - 0.22), 0.36, 0.76), (side * (hw + 0.08), 1.34, 0.80), "dark")
        part.span((side * (hw - 0.04), 0.40, 0.78), (side * (hw - 0.01), 0.48, z0 + 0.02), "dark")
        part.span((side * (hw - 0.04), 1.22, 0.78), (side * (hw - 0.01), 1.30, z0 + 0.02), "dark")
    # A small window in the back.
    part.slab([(-0.42, y0, 1.98), (0.42, y0, 1.98), (-0.42, y0, 2.36), (0.42, y0, 2.36)], (0, -1, 0), 0.006, 0.02, "glass")

    # Mirrors: tall heads on arms out from the windscreen's corners, braced down to the door.
    for side in (-1, 1):
        head = Vector((side * 1.43, y1 - 0.08, 2.20))
        part.span(head - Vector((0.08, 0.03, 0.17)), head + Vector((0.08, 0.03, 0.17)), "dark")
        part.cylinder((side * (hw - 0.02), y1 - 0.06, 2.34), (side * 1.38, y1 - 0.08, 2.32), 0.018, 6, "dark")
        part.cylinder((side * (hw + 0.01), y1 - 0.20, 1.72), (side * 1.38, y1 - 0.08, 2.06), 0.016, 6, "dark")


# --------------------------------------------------------------------------------------------
# Behind the cab: tanks, and the bed with its tilt


def build_tanks(part, p):
    # The fuel tank on the left, a battery box on the right, both between the cab and the bogie.
    part.hull([Vector((x, y, z)) for x, y, z in (
        (-0.66, -0.26, 0.62), (-0.66, -0.86, 0.62), (-0.66, -0.26, 1.10), (-0.66, -0.86, 1.10),
        (-1.08, -0.30, 0.66), (-1.08, -0.82, 0.66), (-1.08, -0.30, 1.06), (-1.08, -0.82, 1.06),
        (-1.12, -0.34, 0.70), (-1.12, -0.78, 0.70), (-1.12, -0.34, 1.02), (-1.12, -0.78, 1.02))], "dark")
    for y in (-0.40, -0.72):
        part.span((-1.135, y - 0.02, 0.64), (-0.66, y + 0.02, 1.08), "sand")
    part.cylinder((-0.90, -0.56, 1.10), (-0.90, -0.56, 1.14), 0.05, 8, "sand")
    part.span((0.66, -0.80, 0.68), (1.06, -0.30, 1.08), "dark")
    part.span((1.06, -0.74, 0.74), (1.08, -0.36, 1.02), "sand")


def stake_stations(p):
    y0, y1 = p["bed"][0], p["bed"][1]
    n = p["bays"]
    return [y1 - 0.04 - (y1 - y0 - 0.08) * k / n for k in range(n + 1)]


def build_bed(part, p):
    y0, y1, under, floor, hw = p["bed"]
    top, rail = p["side_top"], p["rail_top"]
    mid = (y0 + y1) / 2
    stakes = stake_stations(p)

    # Runners on the rails, cross bearers, and the floor.
    fx = p["frame"][0]
    for side in (-1, 1):
        part.span((side * fx - 0.07, y0 + 0.05, p["frame"][2]), (side * fx + 0.07, y1 - 0.05, under), "dark")
    for y in stakes:
        part.span((-hw + 0.04, y - 0.05, under - 0.08), (hw - 0.04, y + 0.05, under), "dark")
    part.span((-hw, y0, under), (hw, y1, floor), "sand")

    for side in (-1, 1):
        x = side * (hw - 0.03)
        # The drop side, its top and bottom rails, the stakes, and the rail the tilt is tied to.
        part.span((x - 0.02, y0 + 0.04, floor), (x + 0.02, y1 - 0.04, top), "sand")
        part.span((x - 0.035, y0 + 0.02, top - 0.07), (x + 0.035, y1 - 0.02, top), "sand")
        part.span((side * hw - 0.02, y0, under - 0.02), (side * hw + 0.02, y1, floor + 0.02), "sand")
        for y in stakes:
            part.span((x - 0.035 + side * 0.025, y - 0.045, under), (x + 0.035 + side * 0.025, y + 0.045, rail), "sand")
        part.span((x - 0.03, y0 + 0.02, rail - 0.06), (x + 0.03, y1 - 0.02, rail), "sand")
        # Louvres in each bay: planks hung on the side, their lower edges out.
        n = p["slats"]
        step = (top - 0.07 - floor) / n
        for a, b in zip(stakes, stakes[1:]):
            for k in range(n):
                z = floor + step * (k + 0.5)
                pts = []
                for y in (a - 0.07, b + 0.07):
                    pts += [(x + side * 0.02, y, z + step * 0.40), (x + side * 0.02, y, z - step * 0.40),
                            (x + side * 0.055, y, z - step * 0.34), (x + side * 0.03, y, z + step * 0.32)]
                part.hull([Vector(q) for q in pts], "sand")
        # A brace across the first bay's lower corner, as in the painting.
        a, b = stakes[0], stakes[1]
        part.hull([Vector((x + side * dx, y, z)) for dx in (0.02, 0.05) for y, z in (
            (a - 0.06, floor + 0.04), (a - 0.06, floor + 0.36), (a - 0.40, floor + 0.04))], "sand")

    # The headboard: a wall to the sides' height, posts above it to the tilt rail.
    part.span((-hw + 0.03, y1 - 0.05, floor), (hw - 0.03, y1 - 0.01, top), "sand")
    part.span((-hw + 0.03, y1 - 0.06, top - 0.07), (hw - 0.03, y1, top), "sand")
    for x in (-0.62, 0.0, 0.62):
        part.span((x - 0.04, y1 - 0.06, top - 0.07), (x + 0.04, y1, rail), "sand")
    part.span((-hw + 0.03, y1 - 0.06, rail - 0.06), (hw - 0.03, y1, rail), "sand")
    # The tailboard: the same height as the sides, louvred, on hinges, with tail lamps under it.
    part.span((-hw + 0.03, y0 + 0.01, floor), (hw - 0.03, y0 + 0.05, top), "sand")
    part.span((-hw + 0.03, y0, top - 0.07), (hw - 0.03, y0 + 0.06, top), "sand")
    for x in (-0.62, 0.0, 0.62):
        part.span((x - 0.04, y0, top - 0.07), (x + 0.04, y0 + 0.06, rail), "sand")
    part.span((-hw + 0.03, y0, rail - 0.06), (hw - 0.03, y0 + 0.06, rail), "sand")
    step = (top - 0.07 - floor) / p["slats"]
    for k in range(p["slats"]):
        z = floor + step * (k + 0.5)
        pts = []
        for x in (-hw + 0.10, hw - 0.10):
            pts += [(x, y0 + 0.01, z + step * 0.40), (x, y0 + 0.01, z - step * 0.40),
                    (x, y0 - 0.025, z - step * 0.34), (x, y0 - 0.005, z + step * 0.32)]
        part.hull([Vector(q) for q in pts], "sand")
    for x in (-0.80, 0.80):
        part.span((x - 0.08, y0 - 0.03, floor - 0.02), (x + 0.08, y0 + 0.01, floor + 0.04), "dark")
        part.span((x * 1.22 - 0.07, y0 - 0.02, under - 0.12), (x * 1.22 + 0.07, y0 + 0.08, under - 0.03), "tail")
    # Mud flaps behind the rear wheels.
    for side in (-1, 1):
        part.span((side * p["track"] - 0.18, p["axles"][2] - 0.72, 0.30), (side * p["track"] + 0.18, p["axles"][2] - 0.69, under),
                  "dark")


def tilt_section(y, top, hem, sag, wobble, p):
    """The tilt across the bed at `y`: square-shouldered over the bows, closed along its hem."""
    w = p["tilt_half_width"] + sag
    right = [(w, hem), (w, 2.46 + wobble), (w - 0.16, top - 0.11 - wobble), (0.58, top - 0.03 + wobble * 0.5)]
    pts = right + [(0.0, top)] + [(-x, z) for x, z in reversed(right)]
    return [Vector((x, y, z)) for x, z in pts]


def build_tilt(part, p):
    y0, y1 = p["bed"][0], p["bed"][1]
    ridge_bow, ridge_sag, hem_bow, hem_sag = p["tilt"]
    stakes = stake_stations(p)
    # Over each bow the canvas is tight and tied down; between them it sags and hangs lower, a
    # little differently in each bay, so it reads as cloth.
    wobbles = (0.025, -0.015, 0.02, -0.02)
    sections = [tilt_section(y1 + 0.03, ridge_bow - 0.02, hem_bow, 0.0, 0.0, p)]
    for k, (a, b) in enumerate(zip(stakes, stakes[1:])):
        sections.append(tilt_section(a if k else a - 0.01, ridge_bow, hem_bow, 0.0, 0.0, p))
        sections.append(tilt_section((a + b) / 2, ridge_sag, hem_sag, 0.025, wobbles[k], p))
    sections.append(tilt_section(stakes[-1], ridge_bow, hem_bow, 0.0, 0.0, p))
    sections.append(tilt_section(y0 - 0.03, ridge_bow - 0.02, hem_bow, 0.0, 0.0, p))
    part.loft(sections, "canvas")


# --------------------------------------------------------------------------------------------
# Assembly


def build():
    p = PARAMS
    truck = Part("Truck")
    build_running_gear(truck, p)
    build_bumper(truck, p)
    build_grille(truck, p)
    build_bonnet(truck, p)
    build_wings(truck, p)
    build_cab(truck, p)
    build_tanks(truck, p)
    build_bed(truck, p)
    build_tilt(truck, p)
    return [truck.finish()]


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    bpy.context.preferences.filepaths.save_version = 0  # no .blend1 beside the .blend


def export(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    # The whole scene: at export it holds the truck and nothing else (previews come after).
    bpy.ops.export_scene.gltf(
        filepath=path, export_format="GLB", export_yup=True, export_apply=True,
        export_materials="EXPORT", export_normals=True, export_texcoords=False, export_cameras=False,
        export_lights=False, export_animations=False, export_extras=False)
    print("wrote", os.path.relpath(path, REPO))


def islands(mesh):
    """The mesh's pieces, as sets of polygon indices joined across shared edges."""
    by_edge = {}
    for poly in mesh.polygons:
        for key in poly.edge_keys:
            by_edge.setdefault(key, []).append(poly.index)
    seen, out = set(), []
    for start in range(len(mesh.polygons)):
        if start in seen:
            continue
        stack, piece = [start], set()
        seen.add(start)
        while stack:
            i = stack.pop()
            piece.add(i)
            for key in mesh.polygons[i].edge_keys:
                for j in by_edge[key]:
                    if j not in seen:
                        seen.add(j)
                        stack.append(j)
        out.append(piece)
    return out, by_edge


def report(objs):
    """Triangles and extents, in Godot's axes, so a change shows in numbers as well as pictures;
    and whether every piece is closed and faces outward, which Godot's back-face culling needs."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for obj in objs:
        mesh = obj.evaluated_get(depsgraph).to_mesh()
        mesh.calc_loop_triangles()
        world = [obj.matrix_world @ v.co for v in mesh.vertices]
        lo = Vector((min(v.x for v in world), min(v.y for v in world), min(v.z for v in world)))
        hi = Vector((max(v.x for v in world), max(v.y for v in world), max(v.z for v in world)))
        print(f"  {obj.name:7s} {len(mesh.loop_triangles):5d} tris  x {lo.x:+.2f}..{hi.x:+.2f}  "
              f"y(up) {lo.z:+.3f}..{hi.z:+.2f}  z(back) {-hi.y:+.2f}..{-lo.y:+.2f}")
        pieces, by_edge = islands(mesh)
        open_edges = sum(1 for faces in by_edge.values() if len(faces) == 1)
        inward = 0
        for piece in pieces:
            # Twice the signed volume, by the divergence theorem over the piece's triangles.
            volume = 0.0
            for tri in mesh.loop_triangles:
                if tri.polygon_index in piece:
                    a, b, c = (mesh.vertices[i].co for i in tri.vertices)
                    volume += a.dot(b.cross(c))
            inward += volume < 0
        print(f"  {len(pieces)} pieces, {inward} facing inward, {open_edges} open edges; "
              f"{len(mesh.materials)} materials: {', '.join(m.name for m in mesh.materials)}")


# --------------------------------------------------------------------------------------------
# Previews


def render_previews(out_dir, objs):
    """A sky, a ground, a sun; a few cameras; Cycles on the CPU, which needs no display. Only for
    looking at: none of it is exported or saved."""
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 24
    scene.cycles.use_denoising = False
    scene.render.resolution_x, scene.render.resolution_y = 1280, 720
    scene.render.film_transparent = False
    scene.view_settings.view_transform = "Standard"

    world = bpy.data.worlds.new("Sky")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.42, 0.55, 0.72, 1.0)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.9
    scene.world = world

    ground_mesh = bpy.data.meshes.new("Ground")
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=40)
    bm.to_mesh(ground_mesh)
    bm.free()
    ground_mesh.materials.append(material("PreviewGround", srgb(96, 98, 96), roughness=1.0))
    ground = bpy.data.objects.new("PreviewGround", ground_mesh)
    scene.collection.objects.link(ground)

    sun_data = bpy.data.lights.new("Sun", "SUN")
    sun_data.energy = 3.5
    sun = bpy.data.objects.new("Sun", sun_data)
    sun.rotation_euler = (math.radians(50), 0, math.radians(-35))
    scene.collection.objects.link(sun)

    cam_data = bpy.data.cameras.new("Preview")
    cam_data.lens = 50
    cam = bpy.data.objects.new("Preview", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam

    def shoot(name, location, target, ortho=None, roll=None):
        cam.location = Vector(location)
        direction = Vector(target) - cam.location
        # Straight down, the camera's roll is set outright: the bumper to the right.
        cam.rotation_euler = (0.0, 0.0, roll) if roll is not None else direction.to_track_quat("-Z", "Y").to_euler()
        cam_data.type = "ORTHO" if ortho else "PERSP"
        if ortho:
            cam_data.ortho_scale = ortho
        scene.render.filepath = os.path.join(out_dir, name + ".png")
        bpy.ops.render.render(write_still=True)
        print("rendered", scene.render.filepath)

    os.makedirs(out_dir, exist_ok=True)
    shoot("front_three_quarter", (-8.5, 10.5, 3.6), (0.0, 0.2, 1.3))
    shoot("side", (-16.0, 0.0, 1.4), (0.0, 0.0, 1.4), ortho=9.2)
    shoot("top", (0.0, 0.0, 20.0), (0.0, 0.0, 0.0), ortho=9.2, roll=math.radians(90))
    shoot("rear_three_quarter", (8.0, -10.5, 4.2), (0.0, -0.5, 1.2))
    for o in (ground, sun, cam):
        bpy.data.objects.remove(o)
    scene.world = None


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    render_dir = argv[argv.index("--render") + 1] if "--render" in argv else None

    reset_scene()
    make_materials()
    objs = build()
    export(GLB_PATH)
    report(objs)
    if render_dir:
        render_previews(os.path.abspath(render_dir), objs)
    bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH, compress=True)
    print("wrote", os.path.relpath(BLEND_PATH, REPO))


if __name__ == "__main__" or "--background" in sys.argv:
    main()
    sys.stdout.flush()
