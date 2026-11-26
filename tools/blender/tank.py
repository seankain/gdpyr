"""The tank: a low-poly, faceted main battle tank whose turret turns free of its hull, built in
Blender and exported for Godot.

    python3 tools/blender/tank.py          # with the `bpy` module (pip install bpy)
    blender --background --python tools/blender/tank.py

Writes two files, both from what is below and nothing else:

    Models/Vehicles/tank/tank.glb    the model Units/tank.tres draws the tank with
    tools/blender/Tank.blend         the same scene, to open and push around by hand

Every dimension is in PARAMS; change one and run it again. `--render DIR` also writes preview
renders (front three-quarter, side, top, and the turret turned) into DIR.

The shape follows a concept painting: a long hull with a wedge nose and angular fenders over the
front of the tracks, thick segmented skirts, seven road wheels a side with a sprocket behind and
an idler in front; a wide, low turret with arrowhead cheeks, a bustle, smoke dischargers on its
flanks, a commander's sight, a gunner's sight, a remote weapon station and two whip antennas; a
long gun in a faceted thermal sleeve ending in a dark muzzle. Desert tan, with dark camouflage
bands cut into the faces (bisected, so their edges are crisp at any polygon count).

Axes. Blender: +Y is the front, +Z is up, +X is right. glTF (and so Godot) is Y-up and looks down
-Z, so the exporter's (x, y, z) -> (x, z, -y) turns the nose to Godot's forward, which is where a
unit at yaw 0 faces (Aim.Angles). The hull's origin is on the ground under the middle of the hull:
a CharacterBody3D's position is its feet.

Hierarchy, which is what lets the turret turn free of the body:

    Hull            the hull, skirts, wheels and tracks: one mesh, origin on the ground
      Turret        origin on the turret ring's axis, on the hull roof; turns about its own Z
                    (Godot: Y). Its rotation is the turret's yaw relative to the hull.
        Gun         origin on the trunnions; elevates about its own X
          Muzzle    an empty at the muzzle face, on the bore axis: where a flash is drawn

In the .blend the turret's X and Y rotations and the gun's Y and Z rotations are locked, so they
can only be turned the ways the game turns them. Rest pose: everything at zero, gun level, turret
square to the hull.
"""

import math
import os
import sys

import bpy  # first: the pip `bpy` module puts bmesh and mathutils on the path

import bmesh
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT_DIR = os.path.join(REPO, "Models", "Vehicles", "tank")
GLB_PATH = os.path.join(OUT_DIR, "tank.glb")
BLEND_PATH = os.path.join(HERE, "Tank.blend")

PARAMS = dict(
    # Hull, from the origin (ground, middle). Lengths along Y, widths are half-widths along X.
    hull_rear=-2.95,
    hull_nose=3.05,
    belly=0.42,
    deck=1.30,                 # the hull roof
    fender=0.95,               # underside of the overhang over the tracks
    hull_half_width=1.45,      # over the overhang
    lower_half_width=0.76,     # between the tracks
    glacis_start=1.55,         # where the roof starts down to the nose
    nose=(1.04, 1.09, 0.88),   # the nose's top at its corners and at its middle, and its bottom
    nose_beak=0.24,            # how far the middle of the nose stands ahead of its corners
    # Running gear: one side's, mirrored.
    track_x=1.08,              # centre line of each track
    track_half_width=0.29,
    track_thickness=0.08,
    road_wheels=7,
    road_wheel_span=(-1.95, 1.70),
    road_wheel_radius=0.30,
    road_wheel_z=0.38,
    sprocket=(-2.42, 0.55, 0.28),   # y, z, r
    idler=(2.25, 0.50, 0.25),
    return_run=0.86,                # height of the track's centre line on its way back, under the skirt
    link=0.17,                      # length of one track link
    # Skirts: panels along each side, from the fender down over the top of the wheels.
    skirt=(-2.70, 2.35, 0.50),      # y0, y1, bottom z
    skirt_panels=5,
    skirt_thickness=0.10,
    # The turret ring, on the deck.
    ring_y=0.15,
    # Turret, from the ring centre on the deck.
    turret_rear=-1.95,
    turret_front=1.30,
    turret_height=0.62,
    turret_half_width=1.22,
    cheek_tip=(0.92, 1.85),         # x, y of each cheek's point
    # Gun, from the trunnions.
    trunnion=(1.20, 0.33),          # y, z in the turret's frame
    mantlet=(0.32, 0.20, 0.62),     # half-width, half-height, length forward of the trunnions
    sleeve=(0.55, 3.70, 0.135, 0.118),   # y0, y1, r0, r1 (the tan thermal sleeve, 8 sides)
    collar=(3.70, 3.80, 0.140),
    muzzle_tube=(3.80, 4.60, 0.106),
    muzzle_lip=(4.60, 4.72, 0.124),
    bore=0.062,
    antenna_height=2.1,
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
    # Matte, barely metallic: a painted finish, which a strong sky does not turn blue.
    MATS["tan"] = material("TankTan", srgb(190, 166, 126))
    MATS["camo"] = material("TankCamo", srgb(78, 76, 70))
    MATS["dark"] = material("TankDark", srgb(52, 52, 50), roughness=0.8)
    MATS["track"] = material("TankTrack", srgb(46, 44, 42), roughness=0.9)
    MATS["link"] = material("TankTrackLink", srgb(70, 64, 58), roughness=0.9)
    MATS["rust"] = material("TankTrackEdge", srgb(132, 86, 52), roughness=0.9)
    MATS["steel"] = material("TankSteel", srgb(44, 45, 46), metallic=0.3, roughness=0.55)
    MATS["glass"] = material("TankGlass", srgb(22, 28, 36), metallic=0.2, roughness=0.25)
    MATS["bore"] = material("TankBore", srgb(8, 8, 8), roughness=1.0)


# --------------------------------------------------------------------------------------------
# Geometry


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
        """The convex hull of `points`: how a faceted lump of armour is made from its corners."""
        bm = self.bm
        idx = self.slot(mat)
        verts = [bm.verts.new(p) for p in points]
        result = bmesh.ops.convex_hull(bm, input=verts)
        bmesh.ops.delete(bm, geom=[g for g in result["geom_interior"] + result["geom_unused"]
                                   if isinstance(g, bmesh.types.BMVert)], context="VERTS")
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

    def cylinder(self, a, b, r, n, mat, phase=None, r_end=None):
        """A closed prism (or frustum, with `r_end`) of n sides from point a to point b."""
        a, b = Vector(a), Vector(b)
        axis = (b - a).normalized()
        u = (Z if abs(axis.dot(Z)) < 0.9 else X).cross(axis).normalized()
        v = axis.cross(u)
        if phase is None:
            phase = math.pi / n
        return self.loft([ring(a, u, v, r, r, n, phase), ring(b, u, v, r_end or r, r_end or r, n, phase)], mat)

    def camo(self, bands, frame=Matrix.Identity(4)):
        """Cuts each band's two planes through the mesh and paints the faces between them.

        A band is (normal, near, far) in the hull's frame; `frame` takes this part's coordinates
        there, so a band runs on across the turret wherever the turret is when it is cut.
        """
        bm = self.bm
        inverse = frame.inverted()
        dark = self.slot("camo")
        paintable = {self.slot("tan")}
        for normal, near, far in bands:
            n = Vector(normal).normalized()
            n_local = (inverse.to_3x3().transposed() @ n).normalized()
            for d in (near, far):
                p_local = inverse @ (n * d)
                geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
                bmesh.ops.bisect_plane(bm, geom=geom, dist=1e-5, plane_co=p_local, plane_no=n_local)
            for f in bm.faces:
                c = frame @ f.calc_center_median()
                if f.material_index in paintable and near < n.dot(c) < far:
                    f.material_index = dark

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


# --------------------------------------------------------------------------------------------
# The hull


def section(y, half_width, bottom, top, chamfer, top_inset=0.0, top_middle=None):
    """A hull cross-section at `y`: a rectangle with its upper corners cut, anticlockwise in X-Z.

    With a point at the middle of the top and of the bottom, so a nose can be drawn to a beak and
    its top can ridge.
    """
    w, c = half_width, chamfer
    return [Vector(p) for p in (
        (w, y, bottom), (w, y, top - c), (w - c - top_inset, y, top), (0.0, y, top if top_middle is None else top_middle),
        (-(w - c - top_inset), y, top), (-w, y, top - c), (-w, y, bottom), (0.0, y, bottom))]


def build_hull(part, p):
    rear, nose = p["hull_rear"], p["hull_nose"]
    w, deck, fender = p["hull_half_width"], p["deck"], p["fender"]

    # The upper hull: the deck and the overhang over the tracks, from the rear plate to the
    # glacis; then the glacis down to a nose whose middle stands ahead of its corners, ridged a
    # little along the middle as it goes.
    nose_top, nose_ridge, nose_bottom = p["nose"]
    tip = section(nose - p["nose_beak"], w - 0.06, nose_bottom, nose_top, 0.06, 0.28, nose_ridge)
    for v in tip:
        v.y += p["nose_beak"] * max(0.0, 1.0 - abs(v.x) / (w - 0.30))
    part.loft([
        section(rear, w - 0.05, fender, deck - 0.08, 0.10),
        section(rear + 0.15, w, fender, deck, 0.14),
        section(p["glacis_start"], w, fender, deck, 0.14, top_middle=deck + 0.03),
        tip,
    ], "tan")

    # The lower hull, between the tracks, its glacis sloping back from under the nose.
    lw = p["lower_half_width"]
    part.hull([Vector((sx * lw, y, z)) for sx in (-1, 1) for y, z in (
        (rear + 0.05, p["belly"] + 0.10), (rear + 0.12, fender), (nose - 0.30, nose_bottom + 0.02),
        (nose - 0.70, p["belly"] + 0.08), (rear + 0.30, p["belly"]), (nose - 0.95, p["belly"]))], "tan")

    # Headlights let into the nose's corners.
    for side in (-1, 1):
        part.box((side * 1.10, nose - p["nose_beak"] + 0.01, (nose_top + nose_bottom) / 2 - 0.01),
                 (0.15, 0.025, 0.035), "glass")

    # The driver's vision blocks on the glacis, and a hatch behind them.
    part.hull([Vector((x, y, z)) for x in (-0.28, 0.28) for y, z in (
        (p["glacis_start"] + 0.25, deck - 0.03), (p["glacis_start"] + 0.42, deck - 0.12),
        (p["glacis_start"] + 0.25, deck + 0.04), (p["glacis_start"] + 0.36, deck - 0.02))], "dark")
    part.cylinder((0.0, p["glacis_start"] - 0.05, deck), (0.0, p["glacis_start"] - 0.05, deck + 0.04), 0.26, 8, "tan")

    # The engine deck: two grilles, and a stowage box on each side of them.
    for side in (-1, 1):
        part.box((side * 0.45, rear + 0.85, deck + 0.01), (0.32, 0.55, 0.02), "dark")
        part.box((side * 1.12, rear + 0.75, deck + 0.10), (0.24, 0.45, 0.10), "tan")


def build_skirts(part, p):
    y0, y1, bottom = p["skirt"]
    n, t = p["skirt_panels"], p["skirt_thickness"]
    w, top = p["hull_half_width"], p["fender"] + 0.02
    gap = 0.025
    step = (y1 - y0) / n
    for side in (-1, 1):
        outer, inner = side * (w + 0.02), side * (w + 0.02 - t)
        for i in range(n):
            a, b = y0 + i * step + gap, y0 + (i + 1) * step - gap
            low_front = bottom if i < n - 1 else bottom + 0.30   # the front panel's lower corner is cut
            low_rear = bottom if i > 0 else bottom + 0.12
            pts = []
            for x in (outer, inner):
                pts += [Vector((x, a, low_rear)), Vector((x, b, low_front)), Vector((x, a, top)), Vector((x, b, top))]
            # A slight batter: the bottom edge stands out a little more than the top.
            for v in pts:
                if v.z < top - 0.01:
                    v.x += side * 0.03
            part.hull(pts, "tan")


def convex_hull_2d(points):
    """Andrew's monotone chain, anticlockwise, over (y, z) pairs."""
    pts = sorted(set(points))

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower, upper = [], []
    for q in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], q) <= 0:
            lower.pop()
        lower.append(q)
    for q in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], q) <= 0:
            upper.pop()
        upper.append(q)
    return lower[:-1] + upper[:-1]


def resample_closed(path, step):
    """Points every `step` along a closed polyline."""
    lengths = [(path[(i + 1) % len(path)] - path[i]).length for i in range(len(path))]
    total = sum(lengths)
    n = max(8, int(round(total / step)))
    out, seg, along = [], 0, 0.0
    for k in range(n):
        target = total * k / n
        while along + lengths[seg] < target:
            along += lengths[seg]
            seg += 1
        t = (target - along) / lengths[seg]
        out.append(path[seg].lerp(path[(seg + 1) % len(path)], t))
    return out


def wheel_stations(p):
    y0, y1 = p["road_wheel_span"]
    n = p["road_wheels"]
    return [y0 + (y1 - y0) * i / (n - 1) for i in range(n)]


def build_running_gear(part, p):
    half_t = p["track_thickness"] / 2
    rw, rz = p["road_wheel_radius"], p["road_wheel_z"]
    sy, sz, sr = p["sprocket"]
    iy, iz, ir = p["idler"]

    # The track's centre line: round every wheel it is wrapped on, half its thickness out.
    samples = []
    for cy, cz, r in [(y, rz, rw) for y in wheel_stations(p)] + [(sy, sz, sr), (iy, iz, ir)]:
        for k in range(24):
            a = 2 * math.pi * k / 24
            samples.append((round(cy + (r + half_t) * math.cos(a), 5), round(cz + (r + half_t) * math.sin(a), 5)))
    # The return run sags onto support rollers under the skirt rather than riding the wheel tops.
    samples += [(y, p["return_run"]) for y in (sy, iy)]
    outline = [Vector((0.0, y, z)) for y, z in convex_hull_2d(samples)]
    path = resample_closed(outline, p["link"])

    for side in (-1, 1):
        cx = side * p["track_x"]
        hw = p["track_half_width"]
        # One link at a time, so alternate links can be told apart and their faces stay square.
        n = len(path)
        for i in range(n):
            a, b = path[i], path[(i + 1) % n]
            t = (b - a).normalized()
            out = Vector((0.0, t.z, -t.y))  # the outward normal of an anticlockwise loop in Y-Z
            thick = half_t * (1.35 if i % 2 == 0 else 1.0)  # every other link a grouser proud of it
            quad = []
            for q in (a, b):
                quad.append([Vector((cx + hw, q.y, q.z)) + out * thick, Vector((cx - hw, q.y, q.z)) + out * thick,
                             Vector((cx - hw, q.y, q.z)) - out * half_t, Vector((cx + hw, q.y, q.z)) - out * half_t])
            faces = part.loft(quad, "track")
            # Outer face (first quad of each ring pair), then the two sides.
            faces[0].material_index = part.slot("link" if i % 2 == 0 else "track")
            faces[1].material_index = part.slot("rust")
            faces[3].material_index = part.slot("rust")

        # Road wheels: a pair of discs on each station with a hub between, inside the track.
        for y in wheel_stations(p):
            for dx in (-0.14, 0.14):
                x = cx + dx
                part.cylinder((x - 0.09, y, rz), (x + 0.09, y, rz), rw, 12, "dark")
            part.cylinder((cx + side * 0.23, y, rz), (cx + side * 0.27, y, rz), rw * 0.45, 8, "tan")
        # Sprocket behind, toothed; idler in front.
        teeth = []
        for k in range(24):
            a = 2 * math.pi * k / 24
            r = sr * (1.0 if k % 2 == 0 else 0.86)
            teeth.append((a, r))
        rings = []
        for x in (cx - 0.18, cx + 0.18):
            rings.append([Vector((x, sy + r * math.cos(a), sz + r * math.sin(a))) for a, r in teeth])
        part.loft(rings, "steel")
        part.cylinder((cx + side * 0.18, sy, sz), (cx + side * 0.24, sy, sz), sr * 0.55, 8, "tan")
        part.cylinder((cx - 0.20, iy, iz), (cx + 0.20, iy, iz), ir, 12, "dark")
        part.cylinder((cx + side * 0.20, iy, iz), (cx + side * 0.25, iy, iz), ir * 0.5, 8, "tan")


# --------------------------------------------------------------------------------------------
# The turret, in its own frame: origin on the ring's axis, on the deck


def turret_section(y, bottom_half, top_half, bottom, top, chamfer):
    w0, w1, c = bottom_half, top_half, chamfer
    return [Vector(q) for q in (
        (w0, y, bottom), (w1, y, top - c), (w1 - c, y, top), (-(w1 - c), y, top), (-w1, y, top - c), (-w0, y, bottom))]


def build_turret(part, p):
    rear, front, h, w = p["turret_rear"], p["turret_front"], p["turret_height"], p["turret_half_width"]

    # The body: a bustle behind, sloped sides, the roof lower over the front.
    part.loft([
        turret_section(rear, w - 0.30, w - 0.36, 0.14, h - 0.06, 0.08),
        turret_section(rear + 0.18, w - 0.12, w - 0.22, 0.04, h, 0.10),
        turret_section(0.45, w, w - 0.14, 0.0, h, 0.12),
        turret_section(front - 0.10, w - 0.08, w - 0.24, 0.0, h - 0.06, 0.12),
        turret_section(front, w - 0.30, w - 0.46, 0.04, h - 0.14, 0.08),
    ], "tan")

    # The cheeks: an arrowhead of armour each side of the gun, pointing forward.
    tx, ty = p["cheek_tip"]
    for side in (-1, 1):
        pts = [Vector((side * x, y, z)) for x, y, z in (
            (0.36, front - 0.25, 0.06), (0.36, front - 0.25, h - 0.10), (w - 0.04, front - 0.45, 0.04),
            (w - 0.10, front - 0.45, h - 0.12), (0.40, front + 0.30, 0.10), (0.40, front + 0.25, h - 0.18),
            (tx, ty, 0.14), (tx - 0.10, ty - 0.12, h - 0.24), (w - 0.06, front + 0.10, 0.10))]
        part.hull(pts, "tan")

    # Smoke dischargers: two rows of three on each flank, pointing out and forward.
    for side in (-1, 1):
        for row, z in enumerate((0.20, 0.38)):
            for k in range(3):
                y = front - 0.70 - 0.20 * k
                base = Vector((side * (w - 0.02), y, z))
                tip = base + Vector((side * 0.20, 0.10, 0.06))
                part.cylinder(base, tip, 0.065, 8, "dark")
                part.cylinder(tip - (tip - base) * 0.05, tip + (tip - base) * 0.02, 0.045, 8, "bore")

    # Commander's sight: a head on a short mast, front left; its window looks forward.
    cx, cy = -0.55, 0.20
    part.cylinder((cx, cy, h), (cx, cy, h + 0.22), 0.13, 8, "tan")
    part.hull([Vector((cx + x, cy + y, h + z)) for x, y, z in (
        (-0.24, -0.20, 0.22), (0.24, -0.20, 0.22), (-0.24, 0.24, 0.22), (0.24, 0.24, 0.22),
        (-0.22, -0.18, 0.56), (0.22, -0.18, 0.56), (-0.20, 0.20, 0.54), (0.20, 0.20, 0.54),
        (-0.24, 0.27, 0.30), (0.24, 0.27, 0.30))], "tan")
    part.box((cx, cy + 0.255, h + 0.40), (0.15, 0.03, 0.09), "glass")
    part.box((cx + 0.08, cy + 0.26, h + 0.40), (0.035, 0.03, 0.035), "bore")

    # Gunner's sight: a low box with a window, front right, beside the gun.
    gx, gy = 0.62, 0.70
    part.hull([Vector((gx + x, gy + y, h + z)) for x, y, z in (
        (-0.20, -0.25, -0.10), (0.20, -0.25, -0.10), (-0.20, 0.30, -0.10), (0.20, 0.30, -0.10),
        (-0.18, -0.22, 0.22), (0.18, -0.22, 0.22), (-0.18, 0.22, 0.24), (0.18, 0.22, 0.24))], "tan")
    part.box((gx, gy + 0.27, h + 0.10), (0.13, 0.04, 0.08), "glass")

    # The remote weapon station, rear right: a turntable, a cradle with sensors, a machine gun.
    rx, ry = 0.42, -0.65
    part.cylinder((rx, ry, h), (rx, ry, h + 0.10), 0.26, 10, "tan")
    part.hull([Vector((rx + x, ry + y, h + z)) for x, y, z in (
        (-0.22, -0.20, 0.10), (0.22, -0.20, 0.10), (-0.22, 0.20, 0.10), (0.22, 0.20, 0.10),
        (-0.18, -0.16, 0.40), (0.18, -0.16, 0.40), (-0.16, 0.18, 0.36), (0.16, 0.18, 0.36))], "tan")
    part.box((rx - 0.30, ry + 0.02, h + 0.30), (0.10, 0.16, 0.10), "tan")
    part.box((rx - 0.30, ry + 0.185, h + 0.30), (0.07, 0.015, 0.06), "glass")
    part.box((rx + 0.05, ry + 0.05, h + 0.47), (0.06, 0.22, 0.06), "dark")
    part.cylinder((rx + 0.05, ry + 0.27, h + 0.48), (rx + 0.05, ry + 0.85, h + 0.48), 0.025, 6, "steel")

    # A hatch, a stowage box on the bustle, and two whip antennas at the rear corners.
    part.cylinder((0.05, -0.55, h), (0.05, -0.55, h + 0.05), 0.30, 8, "tan")
    part.box((0.0, rear + 0.05, 0.36), (w - 0.45, 0.10, 0.20), "tan")
    for side in (-1, 1):
        base = Vector((side * (w - 0.42), rear + 0.30, h))
        part.cylinder(base, base + Vector((0, 0, 0.12)), 0.06, 6, "dark")
        part.cylinder(base, base + Vector((0, 0, p["antenna_height"])), 0.018, 4, "dark", r_end=0.008)


# --------------------------------------------------------------------------------------------
# The gun, in its own frame: origin on the trunnions


def build_gun(part, p):
    mw, mh, ml = p["mantlet"]
    # The mantlet: a faceted block the gun comes out of, flush with the turret's front.
    part.hull([Vector((x, y, z)) for x, y, z in (
        (-mw, -0.15, -mh), (mw, -0.15, -mh), (-mw, -0.15, mh), (mw, -0.15, mh),
        (-mw + 0.06, ml - 0.10, -mh + 0.04), (mw - 0.06, ml - 0.10, -mh + 0.04),
        (-mw + 0.08, ml - 0.10, mh - 0.02), (mw - 0.08, ml - 0.10, mh - 0.02),
        (-0.18, ml, -0.12), (0.18, ml, -0.12), (-0.18, ml, 0.14), (0.18, ml, 0.14))], "tan")

    y0, y1, r0, r1 = p["sleeve"]
    part.cylinder((0, y0, 0), (0, y1, 0), r0, 8, "tan", r_end=r1)
    a, b, r = p["collar"]
    part.cylinder((0, a, 0), (0, b, 0), r, 8, "tan")
    a, b, r = p["muzzle_tube"]
    part.cylinder((0, a, 0), (0, b, 0), r, 10, "steel")
    a, b, r = p["muzzle_lip"]
    # The lip, open at the front: its face is a ring round the bore, the bore a dark well.
    bore = p["bore"]
    rings = [ring(Vector((0, a, 0)), X, Z, r, r, 10, math.pi / 10), ring(Vector((0, b, 0)), X, Z, r, r, 10, math.pi / 10),
             ring(Vector((0, b, 0)), X, Z, bore, bore, 10, math.pi / 10)]
    part.loft(rings, "steel", cap_start=True, cap_end=False)
    part.loft([ring(Vector((0, b, 0)), X, Z, bore, bore, 10, math.pi / 10),
               ring(Vector((0, b - 0.35, 0)), X, Z, bore, bore, 10, math.pi / 10)], "bore", cap_start=False)


# --------------------------------------------------------------------------------------------
# Camouflage: bands in the hull's frame, (normal, near, far)

HULL_BANDS = [
    ((0.62, 0.78, 0.10), 1.35, 1.95),     # across the nose and the right fender
    ((-0.55, 0.83, 0.05), -1.95, -1.35),  # across the engine deck, left
    ((0.20, -0.30, 0.93), -1.6, -1.0),    # low along the skirts' rear
]
TURRET_BANDS = [
    ((0.70, 0.70, 0.15), 1.05, 1.40),     # diagonally over the turret, from its right cheek
]


# --------------------------------------------------------------------------------------------
# Assembly


def build():
    p = PARAMS
    hull = Part("Hull")
    build_hull(hull, p)
    build_skirts(hull, p)
    hull.camo(HULL_BANDS)
    build_running_gear(hull, p)
    hull_obj = hull.finish()

    ring_at = Vector((0.0, p["ring_y"], p["deck"]))
    turret = Part("Turret")
    build_turret(turret, p)
    turret.camo(TURRET_BANDS, Matrix.Translation(ring_at))
    turret_obj = turret.finish(hull_obj, ring_at)
    turret_obj.lock_rotation = (True, True, False)

    ty, tz = p["trunnion"]
    gun = Part("Gun")
    build_gun(gun, p)
    gun_obj = gun.finish(turret_obj, (0.0, ty, tz))
    gun_obj.lock_rotation = (False, True, True)

    # Unrotated, so in Godot its -Z (forward) runs down the bore, as the gun's and the hull's do.
    muzzle = bpy.data.objects.new("Muzzle", None)
    muzzle.empty_display_type = "PLAIN_AXES"
    muzzle.empty_display_size = 0.3
    bpy.context.scene.collection.objects.link(muzzle)
    muzzle.parent = gun_obj
    muzzle.location = (0.0, p["muzzle_lip"][1], 0.0)
    return hull_obj, turret_obj, gun_obj, muzzle


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    bpy.context.preferences.filepaths.save_version = 0  # no .blend1 beside the .blend


def export(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    # The whole scene: at export it holds the tank and nothing else (previews come after).
    bpy.ops.export_scene.gltf(
        filepath=path, export_format="GLB", export_yup=True, export_apply=True,
        export_materials="EXPORT", export_normals=True, export_texcoords=False, export_cameras=False,
        export_lights=False, export_animations=False, export_extras=False)
    print("wrote", os.path.relpath(path, REPO))


def report(objs):
    """Triangles and extents, in Godot's axes, so a change shows in numbers as well as pictures."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    total = 0
    for obj in objs:
        if obj.type != "MESH":
            continue
        mesh = obj.evaluated_get(depsgraph).to_mesh()
        mesh.calc_loop_triangles()
        total += len(mesh.loop_triangles)
        world = [obj.matrix_world @ v.co for v in mesh.vertices]
        lo = Vector((min(v.x for v in world), min(v.y for v in world), min(v.z for v in world)))
        hi = Vector((max(v.x for v in world), max(v.y for v in world), max(v.z for v in world)))
        print(f"  {obj.name:7s} {len(mesh.loop_triangles):5d} tris  x {lo.x:+.2f}..{hi.x:+.2f}  "
              f"y(up) {lo.z:+.2f}..{hi.z:+.2f}  z(back) {-hi.y:+.2f}..{-lo.y:+.2f}")
    muzzle = objs[-1].matrix_world.translation
    turret = objs[1].matrix_world.translation
    print(f"  {total} tris; turret axis at ({turret.x:.2f}, {turret.z:.2f}, {-turret.y:.2f}), "
          f"muzzle at ({muzzle.x:.2f}, {muzzle.z:.2f}, {-muzzle.y:.2f}) (Godot, from the hull's origin)")


# --------------------------------------------------------------------------------------------
# Previews


def render_previews(out_dir, objs):
    """A sky, a ground, a sun; a few cameras; Cycles on the CPU, which needs no display. Only for
    looking at: none of it is exported or saved."""
    scene = bpy.context.scene
    hull, turret, gun, _ = objs
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

    def shoot(name, location, target, ortho=None):
        cam.location = Vector(location)
        direction = Vector(target) - cam.location
        cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
        cam_data.type = "ORTHO" if ortho else "PERSP"
        if ortho:
            cam_data.ortho_scale = ortho
        scene.render.filepath = os.path.join(out_dir, name + ".png")
        bpy.ops.render.render(write_still=True)
        print("rendered", scene.render.filepath)

    os.makedirs(out_dir, exist_ok=True)
    shoot("front_three_quarter", (-9.5, 11.0, 4.2), (0.3, 1.2, 1.2))
    shoot("side", (-16.0, 1.2, 1.6), (0.0, 1.2, 1.3), ortho=10.5)
    shoot("top", (0.0, 1.2, 20.0), (0.0, 1.2, 0.0), ortho=11.0)
    turret.rotation_euler = (0, 0, math.radians(-50))
    gun.rotation_euler = (math.radians(6), 0, 0)
    shoot("turret_turned", (-9.5, 11.0, 4.2), (0.3, 1.2, 1.2))
    shoot("turret_turned_rear", (8.0, -10.0, 5.0), (0.0, 0.0, 1.2))
    turret.rotation_euler = (0, 0, 0)
    gun.rotation_euler = (0, 0, 0)
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
    report(list(objs))
    if render_dir:
        render_previews(os.path.abspath(render_dir), objs)
    bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH, compress=True)
    print("wrote", os.path.relpath(BLEND_PATH, REPO))


if __name__ == "__main__" or "--background" in sys.argv:
    main()
    sys.stdout.flush()
