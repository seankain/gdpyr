"""The helicopter: the ground force's light attack helicopter, low-poly and faceted, built in Blender
and exported for Godot with its two rotors in pieces that turn.

    python3 tools/blender/helicopter.py          # with the `bpy` module (pip install bpy)
    blender --background --python tools/blender/helicopter.py

Writes two files, both from what is below and nothing else:

    Models/Vehicles/helicopter/helicopter.glb    the model Vehicles/helicopter.tres draws it with
    tools/blender/Helicopter.blend               the same scene, to open by hand

Every dimension is in PARAMS or the constants above it; change one and run it again. `--render DIR`
also writes preview renders (front three-quarter, side, front, top, rear three-quarter) into DIR.

The shape follows a concept rendering: an olive drab light attack helicopter whose fuselage is an
open tube space frame — a faceted armoured nose with a chin gun, a tandem two-seat cockpit inside
the cage, a cabin with the engine and the transmission inside it under a faceted fairing and the
mast, a short stub wing either side carrying a rocket pod and a pair of missiles, a triangulated
truss boom to a faceted fenestron shroud with a fin and a T-tail over it — on a four-bladed main
rotor, two wheels under the cockpit and a tail wheel. Outside the frame are bench seats facing
outward, each with a footrest and a grab handle: here one each side, the left ahead of the stub
wing and the right behind it, for the two passengers, who fire their own weapons from them. The
concept's figures in those seats are not modelled; players sit there. No markings.

Axes. Blender: +Y is the front, +Z is up, +X is the right. glTF (and so Godot) is Y-up and looks
down -Z, so the exporter's (x, y, z) -> (x, z, -y) turns the nose to Godot's forward. The origin is
the vehicle's own (docs/GROUND_VEHICLES.md §5.2): on the ground under the wheels, below the mast.

What moves, and how Scripts/Fps/CrewVehicle.cs moves it:

    Body               everything that does not turn: tilts with the helicopter in flight.
      PilotSeat          empties on the top of each seat's cushion, where Vehicles/helicopter.tres
      LeftSeat           puts the bottom of its seated capsule: what the engine check compares
      RightSeat
    Rotor              the main rotor's hub and four blades, at the hub: turns about Godot +Y
    TailRotor          the fenestron's fan, at its middle: turns about Godot +X

All at rest: identity rotations, so that a turn set on a node is the whole of its pose.
"""

import math
import os
import sys

import bpy  # first: the pip `bpy` module puts bmesh and mathutils on the path

import bmesh
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT_DIR = os.path.join(REPO, "Models", "Vehicles", "helicopter")
GLB_PATH = os.path.join(OUT_DIR, "helicopter.glb")
BLEND_PATH = os.path.join(HERE, "Helicopter.blend")

# What Vehicles/helicopter.tres says, which the definition and the model must agree on. Blender
# axes: a Godot z is minus the y here.
ROTOR_HUB = (0.0, 0.0, 3.38)       # the main rotor's hub, over the origin
ROTOR_RADIUS = 5.0
FAN_CENTRE = (0.0, -5.20, 1.75)    # the fenestron's fan, in its duct
SEATS = {                          # the top of each seat's cushion
    "PilotSeat": (0.0, 2.25, 1.00),     # the cockpit's front seat
    "LeftSeat": (-1.02, 1.05, 1.05),    # outside, ahead of the left stub wing
    "RightSeat": (1.02, -1.05, 1.05),   # outside, behind the right stub wing
}

PARAMS = dict(
    # The space frame's cross-sections, nose to tail: y, then (half-width, z) of its bottom
    # rails, its belt rails, its shoulder rails and its top rails.
    stations=(
        (2.95, (0.54, 0.70), (0.60, 1.25), (0.50, 1.55), (0.30, 1.74)),
        (2.30, (0.58, 0.68), (0.63, 1.30), (0.58, 1.82), (0.34, 2.16)),
        (1.55, (0.58, 0.68), (0.64, 1.33), (0.60, 1.92), (0.36, 2.32)),
        (0.85, (0.58, 0.68), (0.64, 1.35), (0.60, 1.95), (0.40, 2.36)),
        (-0.40, (0.58, 0.68), (0.64, 1.35), (0.60, 1.95), (0.52, 2.35)),
        (-1.55, (0.46, 1.10), (0.50, 1.50), (0.50, 1.90), (0.46, 2.32)),
    ),
    # The boom: its truss runs from the last station to here, (y, half-width, bottom z, top z).
    boom_end=(-4.40, 0.16, 1.55, 2.05),
    boom_bays=5,
    tube=0.038,                 # the frame's main tubes
    brace=0.028,                # its diagonals
    nose_tip=4.20,
    # The fenestron: the duct's bore and the shroud's outer radius, its half-thickness at each.
    duct=(0.55, 0.90, 0.24, 0.15),
    fan_blades=10,
    # The stub wing, either side: root y (front, back), tip y (front, back), root x, tip x, z.
    wing=((0.30, -0.50), (0.15, -0.40), 0.60, 1.72, 1.30),
    pod_x=1.24,                 # the rocket pod under it, and the missiles' rail
    missiles_x=(0.80, 0.97),
    main_wheel=(1.00, 1.05, 0.28, 0.09),   # x, y, radius, half-width
    tail_wheel=(-4.70, 0.15, 0.05),        # y, radius, half-width
    blades=4,
    blade_chord=0.44,
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
    # Matte olive drab, as the concept's: its panels a yellower olive than the frame's tubes.
    MATS["olive"] = material("HeliOlive", srgb(98, 104, 74))
    MATS["cage"] = material("HeliCage", srgb(82, 88, 62), roughness=0.7)
    MATS["dark"] = material("HeliDark", srgb(44, 46, 42), roughness=0.8)
    MATS["seat"] = material("HeliSeat", srgb(38, 40, 38), roughness=0.9)
    MATS["metal"] = material("HeliMetal", srgb(104, 106, 104), metallic=0.5, roughness=0.45)
    MATS["rotor"] = material("HeliRotor", srgb(66, 68, 66), metallic=0.2, roughness=0.55)
    MATS["tyre"] = material("HeliTyre", srgb(40, 40, 40), roughness=0.95)
    MATS["rim"] = material("HeliRim", srgb(78, 84, 62), metallic=0.1, roughness=0.6)
    MATS["gun"] = material("HeliGun", srgb(38, 40, 38), metallic=0.4, roughness=0.5)
    MATS["black"] = material("HeliBlack", srgb(16, 16, 16), roughness=1.0)
    MATS["store"] = material("HeliStore", srgb(112, 116, 96), roughness=0.6)


# --------------------------------------------------------------------------------------------
# Geometry (the helpers of tools/blender/four_by_four.py)


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

    def lathe(self, center, axis, profile, n, mat, phase=0.0):
        """A closed ring swept round `axis` through `center`: `profile` is a closed loop of
        (along the axis, radius) points, each swept into a ring of n."""
        axis = Vector(axis).normalized()
        u = (Z if abs(axis.dot(Z)) < 0.9 else X).cross(axis).normalized()
        v = axis.cross(u)
        center = Vector(center)
        rings = [ring(center + axis * a, u, v, r, r, n, phase) for a, r in profile]
        return self.loft(rings + [rings[0]], mat, cap_start=False, cap_end=False)

    def hull(self, points, mat):
        """The convex hull of `points`: how a faceted lump is made from its corners."""
        bm = self.bm
        idx = self.slot(mat)
        verts = [bm.verts.new(Vector(p)) for p in points]
        result = bmesh.ops.convex_hull(bm, input=verts)
        loose = {g: None for g in result["geom_interior"] + result["geom_unused"] if isinstance(g, bmesh.types.BMVert)}
        bmesh.ops.delete(bm, geom=list(loose), context="VERTS")
        faces = [g for g in result["geom"] if isinstance(g, bmesh.types.BMFace)]
        for f in faces:
            f.material_index = idx
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

    def tube(self, points, r, mat, n=6):
        """A bent tube through `points`: a prism per leg, each a little long so the joints close."""
        for a, b in zip(points, points[1:]):
            a, b = Vector(a), Vector(b)
            d = (b - a).normalized() * (r * 0.6)
            self.cylinder(a - d, b + d, r, n, mat)

    def plate(self, outline, normal, thickness, mat):
        """A flat panel: `outline` on its outer face, `thickness` back along -normal."""
        n = Vector(normal).normalized()
        return self.hull([Vector(c) for c in outline] + [Vector(c) - n * thickness for c in outline], mat)

    def to_mesh(self):
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
        return mesh

    def finish(self, parent=None, location=(0, 0, 0)):
        return place(self.name, self.to_mesh(), parent, location)


def place(name, data, parent=None, location=(0, 0, 0)):
    """An object of `data` (a mesh, or None for an empty) under `parent`, at `location` in its frame."""
    obj = bpy.data.objects.new(name, data)
    if data is None:
        obj.empty_display_type = "PLAIN_AXES"
        obj.empty_display_size = 0.08
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = parent
    obj.location = location
    return obj


def mirrored(points):
    """Points given for the right side (+X), and the same for the left."""
    return [Vector(q) for q in points] + [Vector((-q[0], q[1], q[2])) for q in points]


def mx(p, side):
    """A right-side point on `side`."""
    return Vector((p[0] * side, p[1], p[2]))


# --------------------------------------------------------------------------------------------
# The space frame: cockpit cage, cabin and boom truss


def corners(station, side):
    """A station's bottom, belt, shoulder and top rails' points on `side`."""
    y = station[0]
    return [Vector((side * w, y, z)) for w, z in station[1:]]


def build_frame(part, p):
    """Hoops at every station, rails between them, a diagonal in every bay of every face."""
    st = p["stations"]
    r, rb = p["tube"], p["brace"]
    for k, s in enumerate(st):
        left, right = corners(s, -1), corners(s, 1)
        # The hoop. The first one is the nose's back, where its armour covers the bottom.
        part.tube(left + right[::-1] + ([] if k == 0 else [left[0]]), r, "cage", n=8)
    for k, (a, b) in enumerate(zip(st, st[1:])):
        for side in (-1, 1):
            ca, cb = corners(a, side), corners(b, side)
            for level in range(len(ca)):
                part.tube([ca[level], cb[level]], r, "cage", n=8)
            # A diagonal between each pair of rails, the other way round in the next bay and the
            # next pair up: a zigzag down the side.
            for level in range(len(ca) - 1):
                if (k + level + (side > 0)) % 2 == 0:
                    part.tube([ca[level], cb[level + 1]], rb, "cage")
                else:
                    part.tube([ca[level + 1], cb[level]], rb, "cage")
        # Across the top, zigzag.
        atl, atr = corners(a, -1)[-1], corners(a, 1)[-1]
        btl, btr = corners(b, -1)[-1], corners(b, 1)[-1]
        part.tube([atl, btr] if k % 2 == 0 else [atr, btl], rb, "cage")
    # The canopy's spine, from the nose's top over the cockpit to the transmission deck.
    spine = [Vector((0, s[0], s[-1][1] + 0.01)) for s in st[:4]]
    part.tube(spine, rb, "cage")


def build_boom(part, p):
    """The tail boom: four longerons from the cabin's last hoop to the fenestron, a square frame
    at every bay and a diagonal across every face of every bay, alternating, as the concept's."""
    y0, (wb0, zb0), _, _, (wt0, zt0) = p["stations"][-1]
    y1, w1, zb1, zt1 = p["boom_end"]
    bays = p["boom_bays"]
    rb = p["brace"]

    def frame(t):
        y = y0 + (y1 - y0) * t
        wb = wb0 + (w1 - wb0) * t
        wt = wt0 + (w1 - wt0) * t
        zb = zb0 + (zb1 - zb0) * t
        zt = zt0 + (zt1 - zt0) * t
        return (Vector((-wb, y, zb)), Vector((wb, y, zb)), Vector((wt, y, zt)), Vector((-wt, y, zt)))

    frames = [frame(i / bays) for i in range(bays + 1)]
    for c in range(4):
        part.tube([f[c] for f in frames], p["tube"] * 0.9, "cage", n=8)
    for i, f in enumerate(frames[1:], start=1):
        part.tube([f[0], f[1], f[2], f[3], f[0]], rb * 0.9, "cage")
    for i, (a, b) in enumerate(zip(frames, frames[1:])):
        flip = i % 2 == 0
        # Left face, right face, top, bottom.
        for (p0, p1) in ((0, 3), (1, 2), (3, 2), (0, 1)):
            part.tube([a[p0], b[p1]] if flip else [a[p1], b[p0]], rb * 0.85, "cage")


def build_skin(part, p):
    """The faceted armour and panels on the frame: the nose, the cockpit's lower sides, the
    belly, the transmission deck and its fairing round the mast."""
    st = p["stations"]
    tip = p["nose_tip"]
    # The nose: a faceted wedge from the cockpit's first hoop to its tip, drooping, with a ridge
    # down its top and a keel under it.
    part.hull(mirrored([(0.12, tip - 0.04, 0.88), (0.12, tip - 0.06, 1.12), (0.40, tip - 0.45, 0.72),
                        (0.42, tip - 0.40, 1.28), (0.56, 2.90, 0.68), (0.59, 2.90, 1.28), (0.40, 2.90, 1.66)])
              + [Vector((0, tip, 1.00)), Vector((0, tip - 0.35, 1.42)), Vector((0, 2.90, 1.73)),
                 Vector((0, tip - 0.40, 0.66)), Vector((0, 2.90, 0.64))], "olive")
    # A sensor blister on the nose's top and a cutter ahead of the canopy.
    part.hull([(sx * 0.13, 3.45 + dy, 1.47) for sx in (-1, 1) for dy in (-0.16, 0.16)]
              + [(sx * 0.08, 3.45 + dy, 1.60) for sx in (-1, 1) for dy in (-0.10, 0.10)], "olive")
    part.hull([(sx * 0.015, 2.95 + dy, z) for sx in (-1, 1) for dy, z in ((0.0, 1.72), (0.20, 1.70), (0.0, 1.98))], "dark")
    # The cockpit's lower sides, armoured back to the front seat.
    for s in (-1, 1):
        part.plate([mx((0.61, 2.94, 1.26), s), mx((0.56, 2.94, 0.69), s), mx((0.59, 2.10, 0.67), s),
                    mx((0.64, 2.10, 1.31), s)], mx((1, 0, 0), s), 0.03, "olive")
    # The belly under the cockpit and the cabin, and up under the cabin's back to the boom.
    y_cab = st[4][0]
    part.hull(mirrored([(0.54, 2.92, 0.64), (0.56, y_cab, 0.64), (0.54, 2.92, 0.69), (0.56, y_cab, 0.69)]), "dark")
    yb, (wb, zb) = st[-1][0], st[-1][1]
    part.hull(mirrored([(0.56, y_cab, 0.64), (0.56, y_cab, 0.69), (wb - 0.02, yb, zb - 0.05), (wb - 0.02, yb, zb)]), "dark")
    # The transmission deck over the cabin, and the fairing on it round the mast.
    ytop = st[3][0]
    part.hull(mirrored([(0.40, ytop + 0.05, 2.33), (0.52, st[4][0], 2.33), (0.46, yb, 2.30),
                        (0.40, ytop + 0.05, 2.38), (0.52, st[4][0], 2.38), (0.46, yb, 2.35)]), "olive")
    part.hull(mirrored([(0.38, 0.85, 2.36), (0.24, 0.70, 2.78), (0.52, 0.10, 2.36), (0.46, -0.05, 2.84),
                        (0.28, 0.30, 3.04), (0.26, -0.50, 3.04), (0.46, -1.48, 2.34), (0.28, -1.36, 2.66)])
              + [Vector((0, 0.90, 2.52)), Vector((0, -1.54, 2.52))], "olive")
    # The engine's intakes either side of the fairing, its exhaust out of the back.
    for s in (-1, 1):
        part.hull([mx((0.40 + dx, -0.55 + dy, z), s) for dx in (0.0, 0.10) for dy in (-0.22, 0.22) for z in (2.42, 2.66)],
                  "dark")
    part.cylinder((0, -1.30, 2.58), (0, -1.78, 2.52), 0.13, 8, "dark", r_end=0.11)
    part.cylinder((0, -1.76, 2.52), (0, -1.80, 2.52), 0.09, 8, "black")


def build_innards(part, p):
    """What the open frame shows inside: the cockpit's floor, two seats, panels and sticks; the
    cabin's fuel cell, the engine and the gearbox under the mast."""
    # The cockpit's floor and its two seats in tandem, the rear one raised.
    part.span((-0.52, 0.86, 0.68), (0.52, 2.92, 0.74), "dark")
    for y, z in ((SEATS["PilotSeat"][1], SEATS["PilotSeat"][2]), (1.25, 1.18)):
        part.hull([(sx * 0.24, y + dy, z + dz) for sx in (-1, 1) for dy in (-0.24, 0.22) for dz in (-0.10, 0.0)],
                  "seat")
        part.hull([(sx * 0.24, y + dy, z + dz) for sx in (-1, 1) for dy, dz in ((-0.20, 0.0), (-0.26, 0.0),
                                                                                (-0.36, 0.78), (-0.30, 0.78))], "seat")
        part.hull([(sx * 0.15, y + dy, z + dz) for sx in (-1, 1) for dy, dz in ((-0.30, 0.82), (-0.38, 0.82),
                                                                                (-0.31, 0.98), (-0.39, 0.98))], "seat")
        # Its pedestal down to the floor.
        part.span((-0.16, y - 0.18, 0.74), (0.16, y + 0.14, z - 0.10), "dark")
        # The stick ahead of it.
        part.cylinder((0, y + 0.36, z - 0.05), (0, y + 0.40, z + 0.36), 0.018, 6, "black")
    # The instrument panels: the front one under the canopy's bow, the rear one behind the pilot.
    part.hull(mirrored([(0.42, 2.72, 1.18), (0.42, 2.84, 1.18), (0.36, 2.66, 1.48), (0.36, 2.76, 1.50)]), "dark")
    part.hull(mirrored([(0.36, 1.62, 1.50), (0.36, 1.72, 1.50), (0.30, 1.58, 1.84), (0.30, 1.66, 1.86)]), "dark")
    # The cabin: a fuel cell on its floor, the gearbox under the deck, the engine's drum behind it.
    part.hull(mirrored([(0.44, 0.70, 0.70), (0.44, -0.95, 0.70), (0.44, 0.70, 1.02), (0.40, -0.95, 1.08)]), "olive")
    part.span((-0.30, -0.45, 2.00), (0.30, 0.35, 2.33), "dark")
    part.cylinder((0, -0.30, 1.86), (0, -1.40, 1.86), 0.27, 10, "metal")
    for y in (-0.30, -1.40):
        part.cylinder((0, y + 0.04, 1.86), (0, y - 0.04, 1.86), 0.30, 10, "dark")
    part.tube([(0, -0.85, 2.13), (0, -0.85, 2.34)], 0.06, "dark")
    # The mast and its swashplate.
    hx, hy, hz = ROTOR_HUB
    part.cylinder((hx, hy, 2.90), (hx, hy, hz - 0.10), 0.12, 8, "metal")
    part.cylinder((hx, hy, 3.10), (hx, hy, 3.16), 0.26, 10, "dark")
    for k in range(p["blades"]):
        a = 2 * math.pi * k / p["blades"] + math.pi / 4
        part.tube([(0.23 * math.cos(a), 0.23 * math.sin(a), 3.16), (0.23 * math.cos(a), 0.23 * math.sin(a), hz - 0.10)],
                  0.014, "metal", n=4)


def build_wing(part, p):
    """A stub wing either side, its spar into the frame, a rocket pod under its outer half and a
    rail of two missiles under its inner."""
    (rf, rr), (tf, tr), x0, x1, z = p["wing"]
    for s in (-1, 1):
        zt = z - 0.08
        part.hull([mx(q, s) for q in ((x0, rf, z + 0.05), (x0, rr, z + 0.03), (x0, rf - 0.12, z - 0.04),
                                      (x0, rr, z - 0.03), (x1, tf, zt + 0.03), (x1, tr, zt + 0.02),
                                      (x1, tf - 0.08, zt - 0.03), (x1, tr, zt - 0.02))], "olive")
        part.tube([mx((0.50, -0.10, z), s), mx((x0 + 0.05, -0.10, z), s)], 0.045, "dark")
        # A pylon down to the pod, and the pod: a tube of rockets, its front a dark honeycomb.
        px = p["pod_x"]
        pz = z - 0.42
        part.span(mx((px - 0.04, -0.30, pz + 0.12), s), mx((px + 0.04, 0.10, z - 0.04), s), "dark")
        part.cylinder(mx((px, -0.62, pz), s), mx((px, 0.48, pz), s), 0.17, 8, "olive", phase=0.0)
        part.cylinder(mx((px, 0.48, pz), s), mx((px, 0.58, pz), s), 0.17, 8, "olive", phase=0.0, r_end=0.14)
        part.cylinder(mx((px, 0.58, pz), s), mx((px, 0.59, pz), s), 0.13, 8, "black", phase=0.0)
        part.cylinder(mx((px, -0.66, pz), s), mx((px, -0.62, pz), s), 0.14, 8, "dark", phase=0.0, r_end=0.17)
        # The missiles' rail and its two missiles, finned.
        mz = z - 0.20
        part.span(mx((0.82, -0.45, mz + 0.06), s), mx((0.95, 0.40, z - 0.04), s), "dark")
        for mxo in p["missiles_x"]:
            a, b = mx((mxo, -0.55, mz), s), mx((mxo, 0.50, mz), s)
            part.cylinder(a, b, 0.065, 8, "store")
            part.cylinder(b, b + Vector((0, 0.14, 0)), 0.065, 8, "dark", r_end=0.012)
            for k in range(4):
                ang = math.pi / 4 + k * math.pi / 2
                d = Vector((math.cos(ang), 0, math.sin(ang)))
                part.hull([a + d * 0.06 + Vector((0, 0.02, 0)), a + d * 0.06 + Vector((0, 0.18, 0)),
                           a + d * 0.13 + Vector((0, 0.04, 0)), a + d * 0.13 + Vector((0, 0.10, 0)),
                           a + d * 0.06 + Vector((0, 0.02, 0)) + d.cross(Y) * 0.008,
                           a + d * 0.06 + Vector((0, 0.18, 0)) + d.cross(Y) * 0.008], "store")


def build_tail(part, p):
    """The fenestron's shroud round its duct, the fairing from the boom into it, the stator
    and the gearbox in the duct, the fin over it and the T-tail across the fin's top."""
    c = Vector(FAN_CENTRE)
    bore, outer, t_in, t_out = p["duct"]
    part.lathe(c, X, [(-t_in, bore), (-t_out, outer), (t_out, outer), (t_in, bore)], 14, "olive", phase=math.pi / 14)
    # The boom's end faired into the shroud's front.
    y1, w1, zb1, zt1 = p["boom_end"]
    part.hull(mirrored([(w1 + 0.03, y1 + 0.05, zb1 - 0.05), (w1 + 0.03, y1 + 0.05, zt1 + 0.05),
                        (t_out, c.y + bore + 0.06, c.z - 0.62), (t_out, c.y + bore + 0.06, c.z + 0.62)]), "olive")
    # A ventral strake under the shroud, the tail wheel's mount.
    yt = p["tail_wheel"][0]
    part.hull(mirrored([(0.06, yt + 0.20, c.z - outer * 0.55), (0.06, yt - 0.30, c.z - outer * 0.75),
                        (0.05, yt, 0.42)]), "olive")
    # In the duct: the gearbox behind the fan, three stator vanes out to the bore.
    part.cylinder(c + Vector((0.07, 0, 0)), c + Vector((0.20, 0, 0)), 0.15, 10, "dark")
    for k in range(3):
        a = math.pi / 2 + 2 * math.pi * k / 3
        e = Vector((0, math.cos(a), math.sin(a)))
        t = Vector((0, -math.sin(a), math.cos(a)))
        part.hull([c + Vector((x, 0, 0)) + e * r + t * w for x in (0.12, 0.17) for r in (0.13, bore + 0.02)
                   for w in (-0.03, 0.03)], "dark")
    # The fin: up from the shroud's top and swept back.
    top = 3.38
    part.hull(mirrored([(0.10, c.y + 0.40, c.z + outer - 0.25), (0.10, c.y - 0.62, c.z + outer - 0.25),
                        (0.06, c.y - 0.25, top), (0.06, c.y - 0.90, top), (0.08, c.y - 0.85, c.z + outer - 0.10)]),
              "olive")
    # The T-tail: tapered, its ends cut square.
    part.hull(mirrored([(0.0, c.y - 0.22, top - 0.03), (0.0, c.y - 0.92, top - 0.03), (0.0, c.y - 0.22, top + 0.07),
                        (0.0, c.y - 0.92, top + 0.07), (1.40, c.y - 0.45, top - 0.01), (1.40, c.y - 0.82, top - 0.01),
                        (1.40, c.y - 0.45, top + 0.04), (1.40, c.y - 0.82, top + 0.04)]), "olive")


def build_wheel(part, center, radius, half_width, n):
    """A tyre round a rim, its axle along X."""
    c = Vector(center)
    hw = half_width
    inner = radius * 0.52
    part.lathe(c, X, [(-hw, inner), (-hw, radius * 0.88), (-hw * 0.65, radius), (hw * 0.65, radius),
                      (hw, radius * 0.88), (hw, inner)], n, "tyre")
    part.cylinder(c - X * (hw * 0.8), c + X * (hw * 0.8), inner + 0.01, n - 2, "rim")
    part.cylinder(c - X * (hw + 0.025), c + X * (hw + 0.025), radius * 0.2, 6, "metal")


def build_gear(part, p):
    """Two wheels under the cockpit, on legs out from the bottom rails, braced; a tail wheel in a
    fork under the shroud."""
    wx, wy, wr, whw = p["main_wheel"]
    for s in (-1, 1):
        hub = Vector((s * wx, wy, wr))
        build_wheel(part, hub, wr, whw, 14)
        inboard = hub - X * (s * (whw + 0.04))
        part.cylinder(mx((0.58, wy, 0.70), s), inboard + Vector((0, 0, 0.04)), 0.05, 8, "dark")
        part.cylinder(mx((0.64, wy, 0.62), s), mx((0.80, wy, 0.42), s), 0.065, 8, "metal")
        part.tube([mx((0.58, wy + 0.50, 0.70), s), inboard + Vector((0, 0.02, 0.06))], 0.03, "dark")
        part.tube([mx((0.58, wy - 0.45, 0.70), s), inboard + Vector((0, -0.02, 0.06))], 0.03, "dark")
        part.cylinder(inboard, hub, 0.035, 6, "metal")
    ty, tr, thw = p["tail_wheel"]
    hub = Vector((0, ty, tr))
    build_wheel(part, hub, tr, thw, 10)
    for s in (-1, 1):
        part.tube([mx((thw + 0.03, ty, tr), s), mx((thw + 0.03, ty + 0.05, 0.42), s)], 0.02, "dark")
    part.span((-(thw + 0.05), ty - 0.03, 0.40), (thw + 0.05, ty + 0.10, 0.46), "dark")
    part.cylinder((0, ty + 0.03, 0.44), (0, ty + 0.03, 0.62), 0.035, 6, "metal")


def build_outer_seat(part, side, y, pan):
    """A bench seat outside the frame, facing out: its cushion and back, the tubes it hangs on,
    a footrest below it and a grab handle ahead of it. Side -1 is the left."""
    s = side
    r = 0.026
    # Cushion and back.
    part.hull([mx((x, y + dy, pan + dz), s) for x in (0.76, 1.27) for dy in (-0.23, 0.23) for dz in (-0.07, 0.0)], "seat")
    part.hull([mx((x, y + dy, z), s) for x in (0.70, 0.78) for dy in (-0.23, 0.23) for z in (pan + 0.02, pan + 0.70)],
              "seat")
    part.hull([mx((x, y + dy, z), s) for x in (0.70, 0.77) for dy in (-0.13, 0.13) for z in (pan + 0.78, pan + 0.95)],
              "seat")
    # Its frame: rails under the cushion out from the fuselage, a front bar, struts down to the
    # bottom rail, and the back's uprights.
    for dy in (-0.26, 0.26):
        part.tube([mx((0.62, y + dy, pan - 0.08), s), mx((1.30, y + dy, pan - 0.08), s)], r, "cage")
        part.tube([mx((1.22, y + dy, pan - 0.08), s), mx((0.58, y + dy, 0.70), s)], r, "cage")
        part.tube([mx((0.67, y + dy, pan - 0.08), s), mx((0.67, y + dy, pan + 0.98), s), mx((0.64, y + dy, 1.40), s)],
                  r, "cage")
    part.tube([mx((1.30, y - 0.26, pan - 0.08), s), mx((1.30, y + 0.26, pan - 0.08), s)], r, "cage")
    part.tube([mx((0.67, y - 0.26, pan + 0.98), s), mx((0.67, y + 0.26, pan + 0.98), s)], r, "cage")
    # The footrest, hung from the bottom rail.
    fz = 0.50
    part.tube([mx((1.48, y - 0.28, fz), s), mx((1.48, y + 0.28, fz), s)], 0.024, "cage")
    for dy in (-0.28, 0.28):
        part.tube([mx((1.48, y + dy, fz), s), mx((0.58, y + dy, 0.70), s)], 0.022, "cage")
    part.span(mx((1.38, y - 0.24, fz + 0.01), s), mx((1.54, y + 0.24, fz + 0.03), s), "dark")
    # A grab handle ahead of the seat, from the belt rail out and down, as the concept's post.
    part.tube([mx((0.64, y + 0.36, 1.98), s), mx((0.98, y + 0.36, 1.86), s), mx((1.10, y + 0.36, 1.30), s),
               mx((0.62, y + 0.36, 0.72), s)], 0.022, "cage")


def build_gun(part):
    """The chin gun: a faceted turret under the nose and a long barrel, cosmetic."""
    c = Vector((0, 3.42, 0.60))
    part.hull(ring(c + Vector((0, 0, -0.10)), X, Y, 0.12, 0.12, 8) + ring(c + Vector((0, 0, 0.04)), X, Y, 0.17, 0.17, 8)
              + ring(c + Vector((0, 0, 0.12)), X, Y, 0.10, 0.10, 8), "dark")
    part.span((-0.07, 3.40, 0.50), (0.07, 3.78, 0.63), "gun")
    part.cylinder((0, 3.70, 0.57), (0, 4.66, 0.57), 0.032, 8, "gun")
    part.cylinder((0, 3.74, 0.57), (0, 4.00, 0.57), 0.05, 8, "gun")
    part.cylinder((0, 4.58, 0.57), (0, 4.72, 0.57), 0.045, 8, "gun")


def build_body(p):
    body = Part("Body")
    build_frame(body, p)
    build_boom(body, p)
    build_skin(body, p)
    build_innards(body, p)
    build_wing(body, p)
    build_tail(body, p)
    build_gear(body, p)
    build_gun(body)
    for name in ("LeftSeat", "RightSeat"):
        x, y, z = SEATS[name]
        build_outer_seat(body, 1 if x > 0 else -1, y, z)
    return body


# --------------------------------------------------------------------------------------------
# The rotors


def build_rotor(part, p):
    """Round the hub at the origin: its faceted body, a grip per blade and four blades, each a
    flat faceted aerofoil from its cuff to a swept tip. The rotor turns anticlockwise seen from
    above, so each blade's leading edge is towards +Z x its span."""
    part.hull(ring(Vector((0, 0, -0.10)), X, Y, 0.36, 0.36, 8, math.pi / 8)
              + ring(Vector((0, 0, 0.06)), X, Y, 0.40, 0.40, 8, 0.0)
              + ring(Vector((0, 0, 0.16)), X, Y, 0.22, 0.22, 8, math.pi / 8) + [Vector((0, 0, 0.24))], "dark")
    chord = p["blade_chord"]
    for k in range(p["blades"]):
        a = 2 * math.pi * k / p["blades"] + math.pi / 4
        d = Vector((math.cos(a), math.sin(a), 0))
        c = Z.cross(d)
        # The grip and the cuff over the blade's root.
        part.hull([d * rr + c * w + Z * h for rr in (0.30, 0.72) for w in (-0.10, 0.10) for h in (-0.07, 0.07)], "dark")

        def section(r, ch, th, sweep=0.0, droop=0.0):
            o = d * r - c * sweep - Z * droop
            return [o + c * (0.45 * ch), o + c * (0.20 * ch) + Z * (0.5 * th), o - c * (0.25 * ch) + Z * (0.3 * th),
                    o - c * (0.55 * ch), o - c * (0.25 * ch) - Z * (0.15 * th), o + c * (0.20 * ch) - Z * (0.35 * th)]

        part.loft([section(0.62, chord * 0.55, 0.10), section(1.05, chord, 0.08), section(2.6, chord, 0.065, droop=0.02),
                   section(ROTOR_RADIUS - 0.45, chord, 0.05, droop=0.04),
                   section(ROTOR_RADIUS - 0.08, chord * 0.70, 0.04, sweep=0.06, droop=0.05),
                   section(ROTOR_RADIUS, chord * 0.40, 0.03, sweep=0.12, droop=0.05)], "rotor")


def build_fan(part, p):
    """Round the fan's middle at the origin: a hub along X and its blades, pitched."""
    part.cylinder((-0.07, 0, 0), (0.07, 0, 0), 0.12, 10, "dark")
    part.cylinder((-0.12, 0, 0), (-0.07, 0, 0), 0.04, 10, "metal", r_end=0.11)
    bore = p["duct"][0]
    pitch = math.radians(28)
    for k in range(p["fan_blades"]):
        a = 2 * math.pi * k / p["fan_blades"]
        e = Vector((0, math.cos(a), math.sin(a)))
        t = Vector((0, -math.sin(a), math.cos(a)))
        chord = t * math.cos(pitch) + X * math.sin(pitch)
        n = e.cross(chord).normalized()
        part.hull([e * r + chord * w + n * h for r in (0.10, bore - 0.03) for w in (-0.055, 0.055) for h in (-0.008, 0.008)],
                  "rotor")


# --------------------------------------------------------------------------------------------


def build():
    p = PARAMS
    body_obj = build_body(p).finish()
    for name, at in SEATS.items():
        place(name, None, body_obj, at)
    rotor = Part("Rotor")
    build_rotor(rotor, p)
    rotor_obj = rotor.finish(None, ROTOR_HUB)
    fan = Part("TailRotor")
    build_fan(fan, p)
    fan_obj = fan.finish(None, FAN_CENTRE)
    return [body_obj, rotor_obj, fan_obj]


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    bpy.context.preferences.filepaths.save_version = 0  # no .blend1 beside the .blend


def export(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    # The whole scene: at export it holds the helicopter and nothing else (previews come after).
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
    total = 0
    for obj in objs:
        mesh = obj.evaluated_get(depsgraph).to_mesh()
        mesh.calc_loop_triangles()
        tris = len(mesh.loop_triangles)
        total += tris
        world = [obj.matrix_world @ v.co for v in mesh.vertices]
        lo = Vector((min(v.x for v in world), min(v.y for v in world), min(v.z for v in world)))
        hi = Vector((max(v.x for v in world), max(v.y for v in world), max(v.z for v in world)))
        pieces, by_edge = islands(mesh)
        open_edges = sum(1 for faces in by_edge.values() if len(faces) == 1)
        inward = 0
        for piece in pieces:
            volume = 0.0
            for tri in mesh.loop_triangles:
                if tri.polygon_index in piece:
                    a, b, c = (mesh.vertices[i].co for i in tri.vertices)
                    volume += a.dot(b.cross(c))
            inward += volume < 0
        print(f"  {obj.name:9s} {tris:5d} tris  x {lo.x:+.2f}..{hi.x:+.2f}  y(up) {lo.z:+.3f}..{hi.z:+.2f}  "
              f"z(back) {-hi.y:+.2f}..{-lo.y:+.2f}\n"
              f"            {len(pieces)} pieces, {inward} facing inward, {open_edges} open edges")
    print(f"  {total} triangles")
    for name, (x, y, z) in SEATS.items():
        print(f"  {name:9s} at Godot ({x:+.2f}, {z:+.2f}, {-y:+.2f})")
    print(f"  Rotor at Godot ({ROTOR_HUB[0]:+.2f}, {ROTOR_HUB[2]:+.2f}, {-ROTOR_HUB[1]:+.2f}), radius {ROTOR_RADIUS}; "
          f"TailRotor at ({FAN_CENTRE[0]:+.2f}, {FAN_CENTRE[2]:+.2f}, {-FAN_CENTRE[1]:+.2f})")


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
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.75, 0.77, 0.80, 1.0)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 1.0
    scene.world = world

    ground_mesh = bpy.data.meshes.new("Ground")
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=60)
    bm.to_mesh(ground_mesh)
    bm.free()
    ground_mesh.materials.append(material("PreviewGround", srgb(200, 200, 198), roughness=1.0))
    ground = bpy.data.objects.new("PreviewGround", ground_mesh)
    scene.collection.objects.link(ground)

    sun_data = bpy.data.lights.new("Sun", "SUN")
    sun_data.energy = 3.0
    sun = bpy.data.objects.new("Sun", sun_data)
    sun.rotation_euler = (math.radians(40), 0, math.radians(-35))
    scene.collection.objects.link(sun)

    cam_data = bpy.data.cameras.new("Preview")
    cam_data.lens = 50
    cam = bpy.data.objects.new("Preview", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam

    def shoot(name, location, target, ortho=None, roll=None):
        cam.location = Vector(location)
        direction = Vector(target) - cam.location
        cam.rotation_euler = (0.0, 0.0, roll) if roll is not None else direction.to_track_quat("-Z", "Y").to_euler()
        cam_data.type = "ORTHO" if ortho else "PERSP"
        if ortho:
            cam_data.ortho_scale = ortho
        scene.render.filepath = os.path.join(out_dir, name + ".png")
        bpy.ops.render.render(write_still=True)
        print("rendered", scene.render.filepath)

    os.makedirs(out_dir, exist_ok=True)
    shoot("front_three_quarter", (-13.0, 12.5, 7.5), (0.0, -0.4, 1.4))
    shoot("side", (-30.0, -0.95, 1.9), (0.0, -0.95, 1.9), ortho=12.6)
    shoot("front", (0.0, 30.0, 1.9), (0.0, 0.0, 1.9), ortho=11.0)
    shoot("top", (0.0, -0.95, 30.0), (0.0, -0.95, 0.0), ortho=12.6, roll=math.radians(90))
    shoot("rear_three_quarter", (11.5, -15.0, 7.0), (0.0, -1.0, 1.5))
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
