"""The 4x4: the ground force's armed six-wheeled buggy, low-poly and faceted, built in Blender and
exported for Godot with its running gear in pieces that move.

    python3 tools/blender/four_by_four.py          # with the `bpy` module (pip install bpy)
    blender --background --python tools/blender/four_by_four.py

Writes two files, both from what is below and nothing else:

    Models/Vehicles/four_by_four/four_by_four.glb    the model Vehicles/four_by_four.tres draws it with
    tools/blender/FourByFour.blend                   the same scene, to open by hand

Every dimension is in PARAMS; change one and run it again. `--render DIR` also writes preview
renders (front three-quarter, side, front, top, rear three-quarter) into DIR.

The shape follows two concept renderings: a desert-racer buggy in olive drab, a tube roll cage over
an open two-seat cab with a raked windscreen and a flat roof, faceted panels on the cage (a sail
panel either side behind the doors, angular wings over the front wheels, a low hood with an intake
on it), a tubular bull bar round two hexagonal lamps, and a long flat deck behind the cab over a
rear tandem, carrying jerrycans and ammunition crates and a heavy machine gun on a pedestal behind a
faceted shield. Three axles, every wheel on its own double wishbones and coilover. No markings.

Axes. Blender: +Y is the front, +Z is up, +X is the right. glTF (and so Godot) is Y-up and looks
down -Z, so the exporter's (x, y, z) -> (x, z, -y) turns the nose to Godot's forward. The origin is
the vehicle's own (docs/GROUND_VEHICLES.md §5.1): the middle of the wheels' hardpoints' plane, which
is the centre of mass, the ground RIDE below it with the truck at rest. The axles are where
FourByFourTraits puts them, from the same three numbers as Vehicles/four_by_four.tres.

What moves, and how Scripts/Fps/CrewVehicle.cs moves it:

    Body               the sprung chassis: pitches and rolls with the springs
    GunYaw             turns about Godot +Y at the gun mount; GunPitch, under it, elevates about +X;
      GunPitch           Muzzle, under that, is the end of the barrel
        Muzzle
    Wheel0..Wheel5     tyre and rim, at its hub: spins about +X, the front pair also steer about +Y.
                       0/1 front left/right, 2/3 tandem leading, 4/5 tandem trailing
    Knuckle0..5        the upright at the hub: steers with its wheel, never spins.
      LowerJoint, UpperJoint   its ball joints
    LowerArm0..5       a wishbone, its origin the inboard pivot on the chassis, reaching along +X
    UpperArm0..5       (right) or -X (left) to its ball joint: swings about Godot Z
      ShockMount         (under LowerArm) where the coilover's foot is
    Shock0..5          the coilover, its origin the top mount on the chassis, reaching down -Y in
                       Godot: pointed at its foot and stretched along its length

All at the rest pose: at rest on flat ground, everything meets.
"""

import math
import os
import sys

import bpy  # first: the pip `bpy` module puts bmesh and mathutils on the path

import bmesh
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT_DIR = os.path.join(REPO, "Models", "Vehicles", "four_by_four")
GLB_PATH = os.path.join(OUT_DIR, "four_by_four.glb")
BLEND_PATH = os.path.join(HERE, "FourByFour.blend")

# What Vehicles/four_by_four.tres says, which the sim's step and the model must agree on.
WHEELBASE = 3.15          # front axle to the tandem's middle
FRONT_AXLE = 1.75         # front axle ahead of the origin
TANDEM = 0.86             # the tandem's two axles apart
HALF_TRACK = 0.85
WHEEL_RADIUS = 0.40
SPRING_REST = 0.40
SPRING_HZ = 1.6
GRAVITY = 9.81
STATIC = GRAVITY / (2 * math.pi * SPRING_HZ) ** 2
RIDE = SPRING_REST - STATIC + WHEEL_RADIUS       # the hardpoints' plane above flat ground at rest
HUB_Z = -(SPRING_REST - STATIC)                  # a wheel's centre below the hardpoints' plane at rest
GROUND = -RIDE

TANDEM_MIDDLE = WHEELBASE - FRONT_AXLE
# Blender +Y is forward, so an axle's y is minus the sim's z (FourByFourTraits.AxleZ).
AXLES_Y = (FRONT_AXLE, -(TANDEM_MIDDLE - TANDEM / 2), -(TANDEM_MIDDLE + TANDEM / 2))

GUN_MOUNT = (0.0, -0.95, 1.35)   # GunMount (0, 1.35, 0.95) in Godot
GUN_BARREL = 1.10                # GunBarrelMeters: pivot to muzzle

PARAMS = dict(
    tyre_half_width=0.17,
    tyre_carcass=0.355,        # radius under the tread blocks
    tyre_sidewall=0.30,        # where the sidewall meets the rim's bead
    tread_blocks=22,           # round each of the two staggered rows
    tyre_segments=24,
    rim_radius=0.245,
    spokes=10,
    # Suspension, right side (x > 0), in the chassis's frame; the left is the mirror.
    lower_pivot=(0.27, -0.36),    # x, z of the lower wishbone's inboard pivot
    upper_pivot=(0.40, -0.09),
    lower_joint=(-0.13, -0.12),   # on the knuckle, from the hub: x inward of it, z
    upper_joint=(-0.14, 0.13),
    shock_top=(0.44, 0.30),       # x, z of the coilover's top mount
    shock_foot=0.74,              # along the lower wishbone, of its length
    arm_spread=0.17,              # the wishbones' inboard legs, fore and aft of the axle
    # The deck: top, thickness, half-width, rear end (y).
    deck=(0.40, 0.06, 0.95, -2.40),
    cab_rear=-0.40,
    cowl=1.20,
    roof=(1.10, 0.80, -0.06, 0.63),   # height, front y, rear y, half-width
    belt=0.40,                         # the cage's belt rail
    nose=2.40,
)


# --------------------------------------------------------------------------------------------
# Materials


def srgb(r, g, b):
    """A colour as an artist picks it (sRGB, 0-255), as Blender stores it (linear)."""

    def lin(c):
        c /= 255.0
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

    return (lin(r), lin(g), lin(b), 1.0)


def material(name, rgba, metallic=0.0, roughness=0.75, alpha=1.0):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = rgba
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    if alpha < 1.0:
        bsdf.inputs["Alpha"].default_value = alpha
        mat.surface_render_method = "BLENDED"
    mat.diffuse_color = rgba
    return mat


MATS = {}


def make_materials():
    # Matte olive drab, as the concept's; the cage a shade darker than the panels.
    MATS["olive"] = material("FxfOlive", srgb(92, 99, 80))
    MATS["cage"] = material("FxfCage", srgb(76, 83, 66), roughness=0.7)
    MATS["dark"] = material("FxfDark", srgb(44, 46, 42), roughness=0.8)
    MATS["under"] = material("FxfUnder", srgb(60, 65, 53), roughness=0.85)
    MATS["metal"] = material("FxfMetal", srgb(104, 106, 104), metallic=0.5, roughness=0.45)
    MATS["tyre"] = material("FxfTyre", srgb(48, 48, 48), roughness=0.95)
    MATS["rim"] = material("FxfRim", srgb(78, 84, 68), metallic=0.1, roughness=0.6)
    MATS["glass"] = material("FxfGlass", srgb(150, 168, 176), metallic=0.1, roughness=0.1, alpha=0.3)
    MATS["black"] = material("FxfBlack", srgb(16, 16, 16), roughness=1.0)
    MATS["lens"] = material("FxfLens", srgb(220, 222, 210), roughness=0.25)
    MATS["tail"] = material("FxfTailLamp", srgb(150, 30, 24), roughness=0.4)
    MATS["gun"] = material("FxfGun", srgb(38, 40, 38), metallic=0.4, roughness=0.5)
    MATS["seat"] = material("FxfSeat", srgb(34, 36, 34), roughness=0.9)
    MATS["crate"] = material("FxfCrate", srgb(86, 92, 62), roughness=0.8)


# --------------------------------------------------------------------------------------------
# Geometry (the helpers of tools/blender/supply_truck.py)


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


def both_sides(fn):
    for side in (1, -1):
        fn(side)


def mx(p, side):
    """A right-side point on `side`."""
    return Vector((p[0] * side, p[1], p[2]))


# --------------------------------------------------------------------------------------------
# Wheels: an all-terrain tyre on a ten-spoke beadlock rim, built round its own hub


def build_wheel(part, side, p):
    """One wheel at the origin, its face towards `side`."""
    R, hw = WHEEL_RADIUS, p["tyre_half_width"]
    t, sw = p["tyre_carcass"], p["tyre_sidewall"]
    n = p["tyre_segments"]

    def at(dx, r, a):
        return Vector((side * dx, r * math.cos(a), r * math.sin(a)))

    def rings(profile, count=n, phase=0.0):
        return [[at(dx, r, phase + 2 * math.pi * i / count) for i in range(count)] for dx, r in profile]

    # The carcass, inside bead to outside bead, shouldered at the tread.
    part.loft(rings([(-hw + 0.02, sw - 0.02), (-hw, sw + 0.02), (-hw, t - 0.05), (-hw + 0.04, t),
                     (hw - 0.04, t), (hw, t - 0.05), (hw, sw + 0.02), (hw - 0.02, sw - 0.02)]),
              "tyre", cap_start=False, cap_end=False)
    # The rim's barrel, bead to bead, inside the tyre; a beadlock ring proud of the outer face.
    rr = p["rim_radius"]
    part.loft(rings([(hw - 0.02, sw - 0.02), (hw + 0.005, rr + 0.03), (hw + 0.005, rr), (hw - 0.03, rr - 0.01),
                     (-hw + 0.03, rr - 0.01), (-hw + 0.01, rr + 0.02), (-hw + 0.02, sw - 0.02)]),
              "rim", cap_start=False, cap_end=False)
    # The dish: a dark disc deep in the barrel, the spokes standing out of it to a raised hub.
    part.cylinder(at(hw - 0.13, 0, 0), at(hw - 0.10, 0, 0), rr - 0.012, 20, "black")
    hub = 0.075
    for k in range(p["spokes"]):
        a = 2 * math.pi * k / p["spokes"]
        w_in, w_out = 0.035, 0.022
        pts = []
        for r, w in ((hub, w_in), (rr - 0.015, w_out)):
            for da in (-w / r, w / r):
                for dx in (hw - 0.105, hw - 0.04 - (0.025 if r > hub else 0.0)):
                    pts.append(at(dx, r, a + da))
        part.hull(pts, "rim")
    part.cylinder(at(hw - 0.11, 0, 0), at(hw - 0.025, 0, 0), hub + 0.01, 10, "rim")
    part.cylinder(at(hw - 0.03, 0, 0), at(hw - 0.005, 0, 0), 0.045, 6, "metal")

    # Tread: two staggered rows of blocks, each wrapping over its shoulder onto the sidewall.
    blocks = p["tread_blocks"]
    pitch = 2 * math.pi / blocks
    span = 0.62 * pitch
    for row, s in ((0, 1), (1, -1)):
        for k in range(blocks):
            a = pitch * (k + 0.5 * row)
            pts = []
            for b in (a - span / 2, a + span / 2):
                lean = 0.12 * pitch * s
                for dx, r in ((0.015, t - 0.01), (0.015, R), (hw - 0.035, R), (hw + 0.008, R - 0.03),
                              (hw + 0.008, t - 0.07), (hw - 0.03, t - 0.01)):
                    pts.append(at(s * dx, r, b + (lean if dx > 0.1 else 0.0)))
            part.hull(pts, "tyre")


# --------------------------------------------------------------------------------------------
# Suspension: double wishbones and a coilover at every wheel


def hub(i):
    """Wheel i's centre at rest, in the chassis's frame."""
    side = -1 if i % 2 == 0 else 1
    return Vector((side * HALF_TRACK, AXLES_Y[i // 2], HUB_Z)), side


def build_knuckle(part, side, p):
    """The upright at the hub, inside the rim's barrel: a post between the two ball joints and a
    stub axle out to the hub."""
    lx, lz = p["lower_joint"]
    ux, uz = p["upper_joint"]
    lo, up = Vector((side * lx, 0, lz)), Vector((side * ux, 0, uz))
    part.hull([lo + Vector((dx, dy, 0)) for dx in (-0.03, 0.03) for dy in (-0.04, 0.04)]
              + [up + Vector((dx, dy, 0)) for dx in (-0.025, 0.025) for dy in (-0.035, 0.035)]
              + [Vector((side * -0.08, dy, dz)) for dy in (-0.07, 0.07) for dz in (-0.07, 0.07)], "dark")
    part.cylinder((side * -0.10, 0, 0), (side * -0.02, 0, 0), 0.045, 8, "metal")
    # The brake disc.
    part.cylinder((side * -0.075, 0, 0), (side * -0.06, 0, 0), 0.16, 16, "metal")


def build_arm(part, side, length, spread, r, drop):
    """A wishbone from two inboard pivots, `spread` fore and aft, to a ball joint `length` out
    and `drop` below: tubes, a cross brace and a gusset plate at the joint."""
    a, b = Vector((0, spread, 0)), Vector((0, -spread, 0))
    j = Vector((side * length, 0, -drop))
    part.tube([a, j], r, "cage")
    part.tube([b, j], r, "cage")
    part.tube([a.lerp(j, 0.45), b.lerp(j, 0.45)], r * 0.8, "cage")
    part.cylinder(a + Vector((0, 0.03, 0)), a - Vector((0, 0.03, 0)), r * 1.5, 8, "dark")
    part.cylinder(b + Vector((0, 0.03, 0)), b - Vector((0, 0.03, 0)), r * 1.5, 8, "dark")
    part.hull([j + Vector((side * dx, dy, dz)) for dx in (-0.08, 0.02) for dy in (-0.04, 0.04) for dz in (-0.015, 0.015)]
              + [a.lerp(j, 0.75) + Vector((0, dy, 0)) for dy in (-0.05, 0.05)], "cage")


def build_shock(part, length):
    """A coilover from its top mount down -Z: a piggyback body, a coil spring of flat rings, a
    shaft and an eye at either end."""
    top, foot = Vector((0, 0, 0)), Vector((0, 0, -length))
    part.cylinder(top + Vector((0, 0.025, 0)), top - Vector((0, 0.025, 0)), 0.03, 6, "dark")
    part.cylinder(top - Vector((0, 0, 0.02)), top - Vector((0, 0, length * 0.55)), 0.034, 8, "dark")
    part.cylinder(top - Vector((0.045, 0, 0.05)), top - Vector((0.045, 0, length * 0.38)), 0.022, 6, "dark")
    part.cylinder(top - Vector((0, 0, length * 0.55)), foot + Vector((0, 0, 0.02)), 0.014, 6, "metal")
    coils = 7
    for k in range(coils):
        z = -length * (0.12 + 0.72 * k / (coils - 1))
        part.cylinder((0, 0, z + 0.012), (0, 0, z - 0.012), 0.058, 10, "metal")
    part.cylinder((0, 0, -length * 0.08), (0, 0, -length * 0.11), 0.066, 10, "dark")
    part.cylinder((0, 0, -length * 0.87), (0, 0, -length * 0.90), 0.066, 10, "dark")
    part.cylinder(foot + Vector((0, 0.025, 0)), foot - Vector((0, 0.025, 0)), 0.026, 6, "dark")


def build_running_gear(body, p):
    """Six wheels, knuckles, wishbones and coilovers, each its own object at the rest pose."""
    wheel_meshes, knuckle_meshes = {}, {}
    for side in (-1, 1):
        w = Part("WheelL" if side < 0 else "WheelR")
        build_wheel(w, side, p)
        wheel_meshes[side] = w.to_mesh()
        k = Part("KnuckleL" if side < 0 else "KnuckleR")
        build_knuckle(k, side, p)
        knuckle_meshes[side] = k.to_mesh()

    lx, lz = p["lower_joint"]
    ux, uz = p["upper_joint"]
    objs = []
    for i in range(6):
        h, side = hub(i)
        wheel = place(f"Wheel{i}", wheel_meshes[side], None, h)
        knuckle = place(f"Knuckle{i}", knuckle_meshes[side], None, h)
        lower_joint = place(f"LowerJoint{i}", None, knuckle, (side * lx, 0, lz))
        upper_joint = place(f"UpperJoint{i}", None, knuckle, (side * ux, 0, uz))

        for name, (px, pz), joint, r in (("LowerArm", p["lower_pivot"], lower_joint, 0.022),
                                         ("UpperArm", p["upper_pivot"], upper_joint, 0.018)):
            pivot = Vector((side * px, h.y, pz))
            target = h + joint.location
            reach = target - pivot
            arm = Part(f"{name}{i}")
            build_arm(arm, side, abs(reach.x), p["arm_spread"] * (1.0 if name == "LowerArm" else 0.75), r, -reach.z)
            arm_obj = arm.finish(None, pivot)
            if name == "LowerArm":
                foot = Vector((side * abs(reach.x) * p["shock_foot"], 0, reach.z * p["shock_foot"] + 0.035))
                mount = place(f"ShockMount{i}", None, arm_obj, foot)
                foot_world = pivot + foot
            objs.append(arm_obj)

        sx, sz = p["shock_top"]
        top = Vector((side * sx, h.y, sz))
        shock = Part(f"Shock{i}")
        build_shock(shock, (top - foot_world).length)
        shock_obj = shock.finish(None, top)
        # Pointed at its foot: its -Z down the line from the top mount to the foot.
        shock_obj.rotation_mode = "QUATERNION"
        shock_obj.rotation_quaternion = (top - foot_world).normalized().to_track_quat("Z", "Y")
        objs += [wheel, knuckle, shock_obj]

        # The chassis's brackets the arms and the coilover hang from.
        for px, pz in (p["lower_pivot"], p["upper_pivot"]):
            for dy in (-1, 1):
                y = h.y + dy * p["arm_spread"] * (1.0 if pz == p["lower_pivot"][1] else 0.75)
                body.hull([Vector((side * (px + dx), y + ddy, pz + dz)) for dx in (-0.06, 0.03)
                           for ddy in (-0.035, 0.035) for dz in (-0.035, 0.05)], "dark")
        body.hull([Vector((side * (sx + dx), h.y + dy, sz + dz)) for dx in (-0.07, 0.04)
                   for dy in (-0.05, 0.05) for dz in (-0.03, 0.06)], "dark")
    return objs


# --------------------------------------------------------------------------------------------
# The chassis: frame, belly, cab cage, panels, hood, bull bar, deck and what is on it


def build_frame(part, p):
    lpx, lpz = p["lower_pivot"]
    upx, upz = p["upper_pivot"]
    # Lower rails the wishbones pivot on, nose to tail.
    for side in (-1, 1):
        part.tube([(side * lpx, 2.12, lpz + 0.02), (side * lpx, -2.28, lpz + 0.02)], 0.035, "dark")
        # The upper rails, under the hood and the deck.
        part.tube([(side * upx, 2.05, upz + 0.05), (side * upx, 1.25, upz + 0.10)], 0.03, "dark")
        part.tube([(side * upx, -0.55, upz + 0.06), (side * upx, -2.20, upz + 0.06)], 0.03, "dark")
    # The belly pan under the cab, and a skid plate sloping up under the nose.
    part.hull(mirrored([(0.62, -0.40, -0.30), (0.62, 1.30, -0.30), (0.55, -0.40, -0.25), (0.55, 1.30, -0.25)]), "olive")
    part.hull(mirrored([(0.52, 1.30, -0.30), (0.46, 2.25, -0.16), (0.52, 1.30, -0.26), (0.46, 2.22, -0.12)]), "dark")
    # The engine and drivetrain under the deck, and the radiator behind the cab.
    part.hull(mirrored([(0.36, -0.45, -0.24), (0.36, -2.25, -0.20), (0.36, -0.45, 0.33), (0.36, -2.25, 0.33),
                        (0.30, -2.34, -0.05)]), "under")
    part.span((-0.30, -0.70, 0.02), (0.30, -0.50, 0.33), "black")
    # Diffs on the axles' line.
    for y in AXLES_Y:
        part.hull([Vector((sx * 0.16, y + sy * 0.14, z)) for sx in (-1, 1) for sy in (-1, 1) for z in (-0.40, -0.16)]
                  + [Vector((0, y + 0.22, -0.28))], "dark")


def build_cab(part, p):
    belt, cowl, rear = p["belt"], p["cowl"], p["cab_rear"]
    rz, ry0, ry1, rw = p["roof"]
    r = 0.032

    def side_tubes(s):
        # The rocker, the belt rail and the door's X, the front post, the A-pillar, the roof rail,
        # the main hoop down to the deck.
        part.tube([mx((0.72, rear, -0.20), s), mx((0.72, cowl + 0.05, -0.20), s)], r, "cage")
        part.tube([mx((0.76, rear + 0.05, belt), s), mx((0.74, cowl, belt + 0.02), s)], r, "cage")
        part.tube([mx((0.74, rear + 0.08, -0.18), s), mx((0.75, cowl - 0.05, belt - 0.02), s)], r, "cage")
        part.tube([mx((0.75, rear + 0.08, belt - 0.02), s), mx((0.74, cowl - 0.05, -0.18), s)], r, "cage")
        part.tube([mx((0.72, cowl + 0.05, -0.20), s), mx((0.74, cowl, belt + 0.02), s)], r, "cage")
        part.tube([mx((0.74, cowl, belt + 0.02), s), mx((rw, ry0, rz - 0.02), s)], r, "cage")
        part.tube([mx((rw, ry0, rz - 0.02), s), mx((rw, ry1, rz - 0.02), s)], r, "cage")
        part.tube([mx((rw, ry1, rz - 0.02), s), mx((0.74, rear + 0.02, belt), s), mx((0.72, rear, -0.20), s)], r, "cage")
        # Back stays from the roof down to the deck beside the gun.
        part.tube([mx((rw - 0.08, ry1, rz - 0.02), s), mx((0.58, -1.30, p["deck"][0] + 0.02), s)], 0.026, "cage")
        # The harness bar's ends, and a diagonal in the main hoop.
        part.tube([mx((0.70, rear + 0.02, belt + 0.30), s), mx((0.0, rear + 0.02, belt + 0.30), s)], 0.026, "cage")

    both_sides(side_tubes)
    # Cross tubes: over the windscreen, at the roof's back, the windscreen's middle post, the dash bar.
    part.tube([(-rw, ry0, rz - 0.02), (rw, ry0, rz - 0.02)], r, "cage")
    part.tube([(-rw, ry1, rz - 0.02), (rw, ry1, rz - 0.02)], r, "cage")
    part.tube([(0.0, cowl, belt + 0.04), (0.0, ry0, rz - 0.02)], 0.024, "cage")
    part.tube([(-0.74, cowl - 0.02, belt + 0.02), (0.74, cowl - 0.02, belt + 0.02)], r, "cage")
    part.tube([(-0.74, rear + 0.02, belt), (0.74, rear + 0.02, belt)], r, "cage")
    part.tube([(-rw, ry1, rz - 0.02), (rw - 0.05, rear + 0.03, belt + 0.02)], 0.026, "cage")

    # The roof: a flat panel, chamfered along its front edge.
    part.hull(mirrored([(rw + 0.02, ry1 - 0.02, rz), (rw + 0.02, ry0 - 0.02, rz), (rw - 0.04, ry0 + 0.06, rz - 0.03),
                        (rw + 0.02, ry1 - 0.02, rz + 0.035), (rw - 0.02, ry0 - 0.04, rz + 0.035)]), "olive")
    # The windscreen, in two panes either side of the middle post.
    for s in (-1, 1):
        a, b = mx((0.02, cowl - 0.01, belt + 0.06), s), mx((0.70, cowl - 0.01, belt + 0.04), s)
        c, d = mx((rw - 0.03, ry0 + 0.01, rz - 0.04), s), mx((0.02, ry0 + 0.01, rz - 0.04), s)
        normal = (b - a).cross(d - a).normalized()
        if normal.y < 0:
            normal = -normal
        part.plate([a, b, c, d], normal, 0.012, "glass")
    # The sail panels behind the doors: from the roof's back corner down to the deck.
    for s in (-1, 1):
        part.plate([mx((rw + 0.02, ry1 + 0.04, rz - 0.04), s), mx((rw + 0.02, ry1 - 0.10, rz - 0.04), s),
                    mx((0.78, rear - 0.06, belt + 0.02), s), mx((0.78, rear + 0.42, belt + 0.02), s),
                    mx((0.76, rear + 0.40, belt + 0.18), s)], mx((1, 0, 0), s), 0.03, "olive")
    # Kick panels at the cowl, down the front of the door opening.
    for s in (-1, 1):
        part.plate([mx((0.77, cowl + 0.02, belt + 0.04), s), mx((0.77, cowl - 0.22, belt + 0.04), s),
                    mx((0.77, cowl - 0.06, -0.05), s), mx((0.77, cowl + 0.06, -0.22), s),
                    mx((0.77, cowl + 0.12, -0.22), s)], mx((1, 0, 0), s), 0.025, "olive")
    # Rocker panels under the doors.
    for s in (-1, 1):
        part.hull([mx(q, s) for q in ((0.62, rear + 0.05, -0.27), (0.62, cowl - 0.02, -0.27), (0.80, rear + 0.08, -0.17),
                                     (0.80, cowl - 0.05, -0.17), (0.80, rear + 0.08, 0.02), (0.80, cowl - 0.05, 0.02),
                                     (0.66, rear + 0.05, 0.05), (0.66, cowl - 0.02, 0.05))], "olive")
    # The firewall behind the seats, the floor, the dash and the cowl's scuttle.
    part.span((-0.66, rear - 0.02, -0.25), (0.66, rear + 0.03, belt + 0.25), "dark")
    part.span((-0.66, rear, -0.27), (0.66, cowl, -0.22), "dark")
    part.hull(mirrored([(0.70, cowl - 0.02, belt + 0.04), (0.70, cowl - 0.30, belt + 0.02), (0.70, cowl - 0.02, 0.05),
                        (0.70, cowl - 0.24, 0.20)]), "dark")


def build_interior(part, p):
    """Two bucket seats, a wheel on the left, a gear lever between them."""
    for x in (-0.45, 0.42):
        y0 = 0.30
        # The base, the back reclined, the side bolsters and the headrest.
        part.hull([(x + sx * 0.21, y0 + sy, z) for sx in (-1, 1) for sy, z in ((0.22, -0.20), (0.22, -0.08), (-0.22, -0.20),
                                                                                (-0.22, -0.10))], "seat")
        part.hull([(x + sx * 0.22, y, z) for sx in (-1, 1) for y, z in ((-0.20, -0.12), (-0.30, 0.55), (-0.36, 0.55),
                                                                         (-0.28, -0.12))], "seat")
        for sx in (-1, 1):
            part.hull([(x + sx * dx, y, z) for dx in (0.17, 0.23) for y, z in ((0.20, -0.08), (-0.24, -0.08), (-0.20, 0.30),
                                                                                (0.05, 0.02))], "seat")
    # The steering wheel on its column, before the driver.
    sx, sy, sz = -0.45, 0.80, 0.50
    rim = ring(Vector((sx, sy, sz)), X, Vector((0, -0.42, 0.91)).normalized(), 0.16, 0.16, 10)
    part.tube(rim + [rim[0]], 0.016, "black", n=4)
    part.tube([rim[2], Vector((sx, sy, sz)), rim[8]], 0.014, "black", n=4)
    part.cylinder((sx, sy + 0.02, sz - 0.03), (sx, cowl_y(p) - 0.25, sz - 0.25), 0.025, 6, "black")
    part.tube([(0.0, 0.55, -0.22), (0.0, 0.48, 0.10)], 0.018, "black")


def cowl_y(p):
    return p["cowl"]


def build_hood(part, p):
    """The low hood over the front axle, its intake, the wings over the front wheels and the lamps."""
    cowl, nose = p["cowl"], p["nose"]
    belt = p["belt"]
    # The hood: crowned, narrowing and dropping to the nose.
    part.hull(mirrored([(0.72, cowl, belt + 0.04), (0.18, cowl, belt + 0.09), (0.56, 2.18, 0.33), (0.14, 2.18, 0.37),
                        (0.74, cowl, 0.10), (0.58, 2.18, 0.04)]), "olive")
    # The intake on it, with two dark slots.
    part.hull(mirrored([(0.30, 1.32, belt + 0.06), (0.30, 1.70, 0.44), (0.24, 1.36, belt + 0.16), (0.24, 1.66, 0.52)]),
              "olive")
    for s in (-1, 1):
        part.span(mx((0.04, 1.66, 0.46), s), mx((0.26, 1.69, 0.51), s), "black")
    # A box on the hood's left shoulder (an air filter), as on the concept's side view.
    part.hull([(-0.62, 1.42, 0.47), (-0.40, 1.42, 0.49), (-0.62, 1.78, 0.42), (-0.40, 1.78, 0.44),
               (-0.62, 1.42, 0.56), (-0.40, 1.42, 0.57), (-0.62, 1.74, 0.52), (-0.40, 1.74, 0.53)], "dark")

    # The wings: a flat faceted shelf over each front wheel from the cowl to the nose, a drop at
    # its outer edge, clear of the tyre at full bump.
    yf = AXLES_Y[0]
    for s in (-1, 1):
        part.hull([mx(q, s) for q in ((0.70, cowl - 0.02, belt + 0.05), (0.58, 2.20, 0.34), (1.04, cowl + 0.12, 0.32),
                                     (1.04, 2.12, 0.27), (1.04, yf, 0.36), (0.70, cowl - 0.02, belt + 0.01),
                                     (0.58, 2.20, 0.30), (1.04, cowl + 0.12, 0.29), (1.04, 2.12, 0.24),
                                     (1.04, yf, 0.33))], "olive")
        part.plate([mx((1.045, cowl + 0.12, 0.32), s), mx((1.045, yf, 0.36), s), mx((1.045, 2.12, 0.27), s),
                    mx((1.045, 2.0, 0.22), s), mx((1.045, yf, 0.29), s), mx((1.045, cowl + 0.22, 0.25), s)],
                   mx((1, 0, 0), s), 0.02, "olive")
    # The nose panel under the hood's front edge, with a radiator grille in it.
    part.hull(mirrored([(0.58, 2.18, 0.04), (0.56, 2.18, 0.33), (0.50, 2.24, 0.04), (0.48, 2.24, 0.30)]), "olive")
    for k in range(4):
        z = -0.05 + 0.05 * k
        part.span((-0.30, 2.24, z), (0.30, 2.27, z + 0.025), "black")
    # Two hexagonal lamps in steel housings.
    for s in (-1, 1):
        c = Vector((s * 0.24, 2.26, 0.16))
        part.loft([ring(c, X, Z, 0.11, 0.075, 6, math.pi / 6), ring(c + Vector((0, 0.06, 0)), X, Z, 0.11, 0.075, 6, math.pi / 6)],
                  "metal")
        part.loft([ring(c + Vector((0, 0.06, 0)), X, Z, 0.09, 0.058, 6, math.pi / 6),
                   ring(c + Vector((0, 0.075, 0)), X, Z, 0.08, 0.05, 6, math.pi / 6)], "lens")


def build_bull_bar(part, p):
    """The tube cage round the nose and its lamps, out to the bumper, as in the concept's front."""
    nose = p["nose"]
    r = 0.032
    for s in (-1, 1):
        # Upper and lower rails from the hood's corner and the frame to the nose.
        part.tube([mx((0.66, 1.92, 0.37), s), mx((0.56, 2.30, 0.28), s), mx((0.44, nose, 0.22), s)], r, "cage")
        part.tube([mx((0.30, 2.05, -0.30), s), mx((0.50, 2.30, -0.12), s), mx((0.46, nose, -0.06), s)], r, "cage")
        part.tube([mx((0.44, nose, 0.22), s), mx((0.46, nose, -0.06), s)], r, "cage")
        # Struts up to the shock towers and back to the wings.
        part.tube([mx((0.56, 2.30, 0.28), s), mx((0.50, 2.30, -0.12), s)], 0.026, "cage")
        part.tube([mx((0.56, 2.30, 0.28), s), mx((0.30, 2.10, -0.30), s)], 0.024, "cage")
        part.tube([mx((0.14, nose + 0.01, 0.24), s), mx((0.14, nose + 0.01, -0.08), s)], 0.024, "cage")
    part.tube([(-0.44, nose, 0.22), (0.44, nose, 0.22)], r, "cage")
    part.tube([(-0.46, nose, -0.06), (0.46, nose, -0.06)], r, "cage")
    part.tube([(-0.46, nose - 0.02, 0.06), (0.46, nose - 0.02, 0.06)], 0.024, "cage")
    # Two small lamps on the top rail, as on the concept's side view.
    for s in (-1, 1):
        part.cylinder(mx((0.30, nose - 0.02, 0.30), s), mx((0.30, nose + 0.06, 0.30), s), 0.05, 8, "metal")
        part.cylinder(mx((0.30, nose + 0.06, 0.30), s), mx((0.30, nose + 0.07, 0.30), s), 0.042, 8, "lens")


def build_deck(part, p):
    top, thick, hw, end = p["deck"]
    front = p["cab_rear"] - 0.02
    # The deck itself, its edges chamfered.
    part.hull(mirrored([(hw, front, top), (hw, end + 0.04, top), (hw - 0.04, end, top - 0.02),
                        (hw, front, top - thick), (hw, end + 0.04, top - thick), (hw - 0.04, end, top - thick)]), "olive")
    # A low lip round its back and sides.
    for s in (-1, 1):
        part.span(mx((hw - 0.04, end + 0.02, top), s), mx((hw, front - 0.40, top + 0.05), s), "olive")
    part.span((-hw, end, top), (hw, end + 0.04, top + 0.05), "olive")
    # The fenders over the tandem, as the concept's: flush with the deck, a ridge standing out
    # along each side and a facet back in under it, the lower edge clear of the tyres at full
    # bump; the front end swept up and back to the cab, the rear end cut square under the deck.
    y0, y1 = front - 0.02, end + 0.01
    for s in (-1, 1):
        part.hull([mx(q, s) for q in (
            (0.80, y0, top - 0.01), (0.95, y0, top - 0.01), (0.95, y1, top - 0.01), (0.80, y1, top - 0.01),
            (1.08, y0 - 0.22, 0.34), (1.08, y1 + 0.06, 0.34), (1.00, y0 - 0.34, 0.26), (1.00, y1 + 0.02, 0.26),
            (0.82, y0 - 0.34, 0.26), (0.82, y1 + 0.02, 0.26))], "olive")
    # The panel across the back under the deck, its lower corners cut, with the tail lamps in it.
    part.hull(mirrored([(0.95, end + 0.04, top - 0.01), (0.95, end + 0.04, 0.20), (0.80, end + 0.04, 0.06),
                        (0.95, end + 0.10, top - 0.01), (0.95, end + 0.10, 0.20), (0.80, end + 0.10, 0.06)]), "olive")
    for s in (-1, 1):
        part.span(mx((0.66, end + 0.02, 0.22), s), mx((0.86, end + 0.05, 0.31), s), "tail")
    # A step and a hitch below it.
    part.tube([(-0.50, end + 0.02, 0.0), (0.50, end + 0.02, 0.0)], 0.035, "cage")
    for s in (-1, 1):
        part.tube([mx((0.36, end + 0.02, 0.0), s), mx((0.32, end + 0.12, 0.08), s)], 0.03, "cage")
    part.span((-0.06, end - 0.06, -0.10), (0.06, end + 0.06, 0.0), "dark")


def build_cargo(part, p):
    """Jerrycans and ammunition crates down the left of the deck and in its front right corner,
    clear of where the gunner stands (behind the pedestal) and the passenger sits (rear right)."""
    top = p["deck"][0]

    def jerrycan(x, y):
        hx, hy, h = 0.085, 0.175, 0.46
        part.hull([(x + sx * hx, y + sy * hy, top + dz) for sx in (-1, 1) for sy in (-1, 1) for dz in (0.0, h - 0.05)]
                  + [(x + sx * (hx - 0.02), y + sy * (hy - 0.05), top + h) for sx in (-1, 1) for sy in (-1, 1)], "olive")
        # The X pressed in its sides, and its three handles.
        for s in (-1, 1):
            part.tube([(x + s * (hx + 0.003), y - hy + 0.04, top + 0.06), (x + s * (hx + 0.003), y + hy - 0.04, top + h - 0.10)],
                      0.008, "dark", n=4)
            part.tube([(x + s * (hx + 0.003), y + hy - 0.04, top + 0.06), (x + s * (hx + 0.003), y - hy + 0.04, top + h - 0.10)],
                      0.008, "dark", n=4)
        part.cylinder((x, y + hy - 0.05, top + h), (x, y + hy - 0.02, top + h + 0.05), 0.025, 6, "dark")
        for dy in (-0.09, -0.01, 0.07):
            part.span((x - 0.012, y + dy - 0.02, top + h), (x + 0.012, y + dy + 0.02, top + h + 0.035), "dark")

    jerrycan(-0.82, -2.18)
    jerrycan(-0.62, -2.18)

    def crate(lo, hi, mat="crate"):
        lo, hi = Vector(lo), Vector(hi)
        part.span(lo, hi, mat)
        # A lid a touch proud and two handles.
        part.span((lo.x - 0.01, lo.y - 0.01, hi.z - 0.03), (hi.x + 0.01, hi.y + 0.01, hi.z + 0.005), mat)
        for y in (lo.y + 0.06, hi.y - 0.10):
            part.span((lo.x - 0.02, y, hi.z - 0.10), (lo.x, y + 0.04, hi.z - 0.06), "dark")

    crate((-0.92, -1.86, top), (-0.56, -1.16, top + 0.30))
    crate((-0.90, -1.06, top), (-0.58, -0.66, top + 0.26))
    crate((0.48, -0.88, top), (0.88, -0.52, top + 0.28))
    crate((0.52, -0.84, top + 0.28), (0.84, -0.58, top + 0.46), "olive")
    crate((0.56, -1.50, top), (0.90, -1.12, top + 0.22))


def build_pedestal(part):
    x, y, z = GUN_MOUNT
    top = p_deck_top()
    part.cylinder((x, y, top), (x, y, top + 0.03), 0.16, 8, "dark")
    part.cylinder((x, y, top + 0.03), (x, y, z - 0.17), 0.055, 8, "cage")
    for k in range(3):
        a = 2 * math.pi * k / 3 + math.pi / 2
        d = Vector((math.cos(a), math.sin(a), 0))
        base = Vector((x, y, top + 0.03))
        part.hull([base + d * 0.05 + Vector((0, 0, 0.25)), base + d * 0.05, base + d * 0.15,
                   base + d * 0.05 + d.cross(Z) * 0.01, base + d * 0.15 + d.cross(Z) * 0.01,
                   base + d * 0.05 + Vector((0, 0, 0.25)) + d.cross(Z) * 0.01], "cage")


def p_deck_top():
    return PARAMS["deck"][0]


def build_body(p):
    body = Part("Body")
    build_frame(body, p)
    build_cab(body, p)
    build_interior(body, p)
    build_hood(body, p)
    build_bull_bar(body, p)
    build_deck(body, p)
    build_cargo(body, p)
    build_pedestal(body)
    return body


# --------------------------------------------------------------------------------------------
# The gun: a pintle head and shield that turn, a heavy machine gun that elevates in it


def build_gun_yaw(part):
    """Round the pivot at the origin: the pintle head and its yoke, and a faceted shield ahead of
    the gun with a slot the barrel elevates in."""
    part.cylinder((0, 0, -0.18), (0, 0, -0.08), 0.05, 8, "cage")
    part.cylinder((0, 0, -0.10), (0, 0, -0.07), 0.08, 8, "dark")
    for s in (-1, 1):
        part.span((s * 0.095, -0.08, -0.08), (s * 0.115, 0.08, 0.06), "dark")
    part.span((-0.115, -0.06, -0.10), (0.115, 0.06, -0.07), "dark")
    # The shield: two halves either side of the slot, a bridge over it above the gun's highest
    # elevation where it passes, and wings angled back; on an arm from the yoke.
    y, t = 0.24, 0.018
    lo, hi, slot = -0.14, 0.34, 0.045
    for s in (-1, 1):
        part.plate([mx((slot, y, lo), s), mx((0.30, y - 0.02, lo + 0.03), s), mx((0.31, y - 0.02, hi - 0.06), s),
                    mx((0.26, y, hi), s), mx((slot, y, hi), s)], (0, 1, 0), t, "olive")
        part.plate([mx((0.30, y - 0.02, lo + 0.03), s), mx((0.38, y - 0.20, lo + 0.06), s),
                    mx((0.38, y - 0.20, hi - 0.10), s), mx((0.31, y - 0.02, hi - 0.06), s)],
                   mx((0.93, 0.37, 0), s), t, "olive")
        part.span(mx((0.10, 0.02, -0.06), s), mx((0.14, y - t, -0.02), s), "dark")
    part.plate([(-slot - 0.01, y, hi - 0.04), (slot + 0.01, y, hi - 0.04), (slot + 0.01, y, hi), (-slot - 0.01, y, hi)],
               (0, 1, 0), t, "olive")
    part.plate([(-slot - 0.01, y, lo), (slot + 0.01, y, lo), (slot + 0.01, y, lo + 0.04), (-slot - 0.01, y, lo + 0.04)],
               (0, 1, 0), t, "olive")


def build_gun_pitch(part):
    """A heavy machine gun, its bore along +Y through the pivot at the origin: receiver, perforated
    jacket, barrel and flash hider to GUN_BARREL ahead; spade grips behind; a box on its left."""
    part.hull([(sx * 0.075, y, z) for sx in (-1, 1) for y, z in ((-0.42, -0.07), (0.10, -0.07), (0.10, 0.07),
                                                                  (-0.42, 0.08), (-0.30, 0.11), (0.04, 0.11))], "gun")
    # Feed cover and rear sight on top.
    part.span((-0.06, -0.28, 0.11), (0.06, -0.02, 0.135), "gun")
    part.span((-0.012, -0.38, 0.11), (0.012, -0.34, 0.17), "gun")
    # Trunnion stubs out to the yoke.
    part.cylinder((-0.10, 0, 0), (0.10, 0, 0), 0.025, 6, "gun")
    # The jacket, the barrel, the carrying handle and the flash hider.
    part.cylinder((0, 0.10, 0), (0, 0.34, 0), 0.042, 8, "gun")
    for y in (0.15, 0.21, 0.27):
        part.cylinder((0, y, 0), (0, y + 0.025, 0), 0.046, 8, "black")
    part.cylinder((0, 0.34, 0), (0, GUN_BARREL - 0.08, 0), 0.022, 8, "gun")
    part.cylinder((0, GUN_BARREL - 0.10, 0), (0, GUN_BARREL, 0), 0.032, 8, "gun")
    part.tube([(0, 0.24, 0.04), (0, 0.24, 0.10), (0, 0.36, 0.10), (0, 0.36, 0.03)], 0.01, "gun", n=4)
    # Spade grips and the butterfly trigger between them.
    for s in (-1, 1):
        part.tube([(s * 0.06, -0.42, 0.02), (s * 0.075, -0.54, 0.0), (s * 0.075, -0.56, -0.10)], 0.017, "gun")
    part.span((-0.04, -0.47, -0.01), (0.04, -0.44, 0.03), "gun")
    # The ammunition box on its left, and the belt up into the feed.
    part.span((-0.30, -0.30, -0.14), (-0.13, -0.02, 0.08), "olive")
    part.span((-0.31, -0.31, 0.06), (-0.12, -0.01, 0.09), "olive")
    part.hull([(-0.13, -0.24, 0.06), (-0.13, -0.08, 0.06), (-0.075, -0.22, 0.09), (-0.075, -0.10, 0.09),
               (-0.13, -0.24, 0.08), (-0.13, -0.08, 0.08)], "metal")


def build_gun():
    yaw = Part("GunYaw")
    build_gun_yaw(yaw)
    yaw_obj = yaw.finish(None, GUN_MOUNT)
    pitch = Part("GunPitch")
    build_gun_pitch(pitch)
    pitch_obj = pitch.finish(yaw_obj, (0, 0, 0))
    place("Muzzle", None, pitch_obj, (0, GUN_BARREL, 0))
    return yaw_obj, pitch_obj


# --------------------------------------------------------------------------------------------


def build():
    p = PARAMS
    body = build_body(p)
    gear = build_running_gear(body, p)
    body_obj = body.finish()
    yaw_obj, pitch_obj = build_gun()
    return [body_obj, yaw_obj, pitch_obj] + gear


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    bpy.context.preferences.filepaths.save_version = 0  # no .blend1 beside the .blend


def export(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    # The whole scene: at export it holds the vehicle and nothing else (previews come after).
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
    and whether every piece is closed and faces outward, which Godot's back-face culling needs.
    Each mesh is counted once, however many objects draw it."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    seen, total, drawn = set(), 0, 0
    for obj in objs:
        if obj.type != "MESH":
            continue
        mesh = obj.evaluated_get(depsgraph).to_mesh()
        mesh.calc_loop_triangles()
        tris = len(mesh.loop_triangles)
        drawn += tris
        world = [obj.matrix_world @ v.co for v in mesh.vertices]
        lo = Vector((min(v.x for v in world), min(v.y for v in world), min(v.z for v in world)))
        hi = Vector((max(v.x for v in world), max(v.y for v in world), max(v.z for v in world)))
        line = (f"  {obj.name:11s} {tris:5d} tris  x {lo.x:+.2f}..{hi.x:+.2f}  y(up) {lo.z:+.3f}..{hi.z:+.2f}  "
                f"z(back) {-hi.y:+.2f}..{-lo.y:+.2f}")
        key = obj.data.name
        if key in seen:
            print(line + "  (shared mesh)")
            continue
        seen.add(key)
        total += tris
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
        print(line + f"\n              {len(pieces)} pieces, {inward} facing inward, {open_edges} open edges")
    print(f"  {total} triangles in its meshes, {drawn} drawn")
    print(f"  ground {GROUND:+.4f} below the origin; hubs at {HUB_Z:+.4f}; axles at Godot z "
          + ", ".join(f"{-y:+.3f}" for y in AXLES_Y))


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
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=40)
    bm.to_mesh(ground_mesh)
    bm.free()
    ground_mesh.materials.append(material("PreviewGround", srgb(200, 200, 198), roughness=1.0))
    ground = bpy.data.objects.new("PreviewGround", ground_mesh)
    ground.location = (0, 0, GROUND)
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
    mid = GROUND + 0.9
    shoot("front_three_quarter", (-7.0, 8.5, mid + 2.2), (0.0, 0.1, mid))
    shoot("side", (16.0, 0.0, mid), (0.0, 0.0, mid), ortho=5.6)
    shoot("front", (0.0, 16.0, mid), (0.0, 0.0, mid), ortho=3.6)
    shoot("top", (0.0, 0.0, 20.0), (0.0, 0.0, 0.0), ortho=5.6, roll=math.radians(90))
    shoot("rear_three_quarter", (6.5, -8.5, mid + 2.6), (0.0, -0.4, mid))
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
