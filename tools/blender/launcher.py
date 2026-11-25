"""The 40 mm launcher: a low-poly break-action weapon, its round, and the first-person viewmodel
that holds it, built in Blender and exported for Godot.

    python3 tools/blender/launcher.py          # with the `bpy` module (pip install bpy)
    blender --background --python tools/blender/launcher.py

Writes three files, every one of them from what is below and nothing else:

    Models/Weapons/launcher/launcher.glb      the weapon alone, rigged and animated: the world model
    Models/Weapons/launcher/launcher_fp.glb   the viewmodel: the weapon held by the arms of
                                              Models/Weapons/arms/arms.blend, as the camera sees it
    tools/blender/Launcher.blend              the same scene, to open and push around by hand

Every number is in PARAMS and in the timelines further down; change one and run it again.

The shape follows a concept rendering: a twelve-sided barrel that steps up behind its front half,
a collar where it meets the receiver, a faceted receiver with a latch on top, a trigger guard and
a second loop behind it, a red-brown faceted stock, sling swivels under the barrel and the stock,
and a squat red round with a tan head and rim.

Axes. Blender: +Y is where the muzzle points, +Z is up, +X is right. glTF (and so Godot) is
Y-up and looks down -Z, so the exporter's (x, y, z) -> (x, z, -y) turns the muzzle to Godot's
forward. The weapon's origin is on the bore axis at the breech face, where the barrel meets the
receiver: the barrel runs forward of it, the receiver and the stock behind.

The viewmodel's origin is the eye. The camera it is authored against (`Eye` in the .blend) sits
there looking down +Y at VIEW_FOV, the field of view Godot draws the viewmodel at
(WeaponDefinition.ViewmodelFovDegrees, BaseMaterial3D.fov_override), so what is framed here is
what the player sees. The sights up are a pose of their own, `aim`: the weapon square to the eye
with the rear notch on the line of sight, the arms re-solved to it from shoulders that stay put.
The script prints where the notch, the front blade and the muzzle end up, from the eye.

Bones. Both glTFs share the weapon's: `Weapon` (the whole gun, which the viewmodel's recoil and
reload move), `Barrel` (hinged at the pin under the breech; it drops to open), `Round` (a child of
the barrel: the round in the chamber, the case thrown out and the fresh one carried in), `Latch`
and `Trigger`. The viewmodel adds the arms' bones from arms.blend under a `Root` at the eye. The
arms are posed by IK in the .blend (each hand has a target; the right one rides on the weapon,
the left one is keyed every frame from a timeline of what it is holding) and the exporter samples
the solved pose, so the glTF carries plain bone rotations and Godot needs no IK. A node at the end
of the barrel, `Muzzle` (`Muzzle_FP` in the viewmodel: Blender's names are unique), is where the
flash is drawn from.

Animations: `idle` (looping), `fire` and `reload` in both files, and `aim` and `aim_fire` in the
viewmodel. Every action keys every bone, so whichever one Godot plays last leaves nothing behind
it; the .import files keep tracks that never change (animation/remove_immutable_tracks=false) for
the same reason.
"""

import math
import os
import random
import sys

import bpy  # first: the pip `bpy` module puts bmesh and mathutils on the path

import bmesh
from mathutils import Euler, Matrix, Quaternion, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT_DIR = os.path.join(REPO, "Models", "Weapons", "launcher")
WORLD_GLB = os.path.join(OUT_DIR, "launcher.glb")
VIEW_GLB = os.path.join(OUT_DIR, "launcher_fp.glb")
BLEND_PATH = os.path.join(HERE, "Launcher.blend")
ARMS_BLEND = os.path.join(REPO, "Models", "Weapons", "arms", "arms.blend")

FPS = 30

# The viewmodel's own vertical field of view [deg]. The game's camera is 75 degrees vertical
# (107 across at 16:9), at which anything held looks small and stretched; the viewmodel is drawn
# at this instead (BaseMaterial3D.fov_override), and framed for it here.
VIEW_FOV = 55.0

PARAMS = dict(
    sides=12,                  # the barrel's
    # Barrel, from the breech face (y = 0) forward. Radii are to the flats' corners.
    collar=(0.0, 0.034, 0.041),        # y0, y1, radius: the band where it meets the receiver
    rear_barrel=(0.034, 0.200, 0.037),
    front_barrel=(0.200, 0.355, 0.0335),
    bore=0.0205,                       # 40 mm, and a little
    rim_seat=(0.004, 0.0240),          # the counterbore the round's rim sits in: depth, radius
    muzzle_depth=0.07,                 # how far into the muzzle the bore is drawn
    hinge=(0.014, -0.040, 0.0075),     # pin: y, z, radius. The barrel turns about it to open.
    sight_height=0.066,                # the sight line above the bore
    # Receiver: chamfered octagons, front to back: y, half-width, top, bottom, chamfer.
    receiver=[
        (0.000, 0.032, 0.046, -0.054, 0.015),
        (-0.050, 0.035, 0.044, -0.050, 0.016),
        (-0.105, 0.023, 0.032, -0.042, 0.011),
    ],
    # Stock: y, half-width, top, bottom, chamfer.
    stock=[
        (-0.105, 0.021, 0.030, -0.040, 0.010),
        (-0.150, 0.019, 0.026, -0.043, 0.009),
        (-0.205, 0.020, 0.027, -0.052, 0.010),
        (-0.265, 0.023, 0.034, -0.088, 0.012),
        (-0.335, 0.025, 0.041, -0.121, 0.013),
        (-0.405, 0.026, 0.047, -0.142, 0.013),
    ],
    butt_pad=0.012,
    wood_jitter=0.0016,        # how far the stock's vertices are pushed about, for its facets
    wood_dark_share=0.35,      # the share of its faces in the darker wood
    open_degrees=46.0,         # how far the barrel drops
    latch_degrees=38.0,
    trigger_degrees=16.0,
    # The round: rim, case, head; from its base forward.
    round_rim=(0.004, 0.0235),
    round_case=(0.046, 0.0205),
    round_head=[(0.046, 0.0200), (0.060, 0.0200), (0.076, 0.0180), (0.089, 0.0130), (0.096, 0.0060),
                (0.098, 0.0)],
    round_sides=10,
)

# Where the weapon is held, in the viewmodel: the breech face's position from the eye and the
# weapon's turn [deg] (pitch about X, roll about Y, yaw about Z). The muzzle is turned a little
# in, toward the middle of the screen.
HIP_POSITION = Vector((0.140, 0.36, -0.150))
HIP_ROTATION = (0.0, 0.0, 4.0)

# With the sights up: how far ahead of the eye the rear notch is, and how much of the hip's kick
# the shot keeps with the stock in the shoulder.
AIM_NOTCH_DISTANCE = 0.30
AIM_KICK = 0.6

# The arms: arms.blend's are a T-pose with the shoulders 0.76 m apart and arms rather longer than
# a person's; each arm is moved bodily (they are not joined) so that its shoulder lands here,
# scaled by ARM_SCALE, and its hand scaled again about the wrist by HAND_SCALE.
SHOULDERS = {"L": Vector((-0.18, 0.02, -0.29)), "R": Vector((0.21, -0.07, -0.31))}
ARM_SCALE = 0.9
HAND_SCALE = 0.8
# Where each elbow is pulled toward, from the eye.
POLES = {"L": Vector((-0.55, 0.25, -0.75)), "R": Vector((0.45, 0.0, -1.0))}


# --------------------------------------------------------------------------------------------
# Small helpers


def srgb(r, g, b):
    """A colour as an artist picks it (sRGB, 0-255), as Blender stores it (linear)."""

    def lin(c):
        c /= 255.0
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

    return (lin(r), lin(g), lin(b), 1.0)


def material(name, rgba, metallic=0.0, roughness=0.6):
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
    # Barely metallic: a stylised finish, which a bright sky does not turn blue.
    MATS["metal"] = material("LauncherMetal", srgb(98, 128, 128), metallic=0.05, roughness=0.7)
    MATS["dark"] = material("LauncherDark", srgb(58, 72, 76), metallic=0.1, roughness=0.6)
    MATS["wood"] = material("LauncherWood", srgb(146, 56, 36), roughness=0.75)
    MATS["wood_dark"] = material("LauncherWoodDark", srgb(108, 38, 26), roughness=0.8)
    MATS["rubber"] = material("LauncherRubber", srgb(42, 36, 34), roughness=0.9)
    MATS["case"] = material("RoundCase", srgb(200, 52, 40), roughness=0.5)
    MATS["brass"] = material("RoundBrass", srgb(196, 160, 96), metallic=0.2, roughness=0.5)


def ring(center, u, v, ru, rv, n, phase=0.0):
    """n points round `center` in the plane of u and v, an ellipse of radii ru and rv."""
    pts = []
    for i in range(n):
        a = phase + 2.0 * math.pi * i / n
        pts.append(center + u * (ru * math.cos(a)) + v * (rv * math.sin(a)))
    return pts


X, Y, Z = Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))


def yring(y, r, n, phase=None, cx=0.0, cz=0.0):
    """A ring across the bore axis at `y`. The default phase puts a flat on top."""
    if phase is None:
        phase = math.pi / n
    return ring(Vector((cx, y, cz)), X, Z, r, r, n, phase)


def chamfered(y, w, top, bottom, c, cx=0.0):
    """A rectangle across the Y axis with its corners cut: eight points, anticlockwise in X-Z."""
    return [Vector(p) for p in (
        (cx + w, y, bottom + c), (cx + w, y, top - c), (cx + w - c, y, top), (cx - w + c, y, top),
        (cx - w, y, top - c), (cx - w, y, bottom + c), (cx - w + c, y, bottom), (cx + w - c, y, bottom))]


def box(x0, x1, y0, y1, z0, z1):
    """A box as two four-point rings along Y."""
    return [[Vector((x1, y, z0)), Vector((x1, y, z1)), Vector((x0, y, z1)), Vector((x0, y, z0))] for y in (y0, y1)]


class Part:
    """One piece of the weapon under construction: a bmesh, the bone it rides on, its faces' materials."""

    def __init__(self, name, bone):
        self.name = name
        self.bone = bone
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

    def sweep(self, path, half_width, half_thickness, mat, axis=X, caps=True):
        """A bar of rectangular section along `path` (points in the plane normal to `axis`)."""
        rings = []
        for i, p in enumerate(path):
            a = path[max(i - 1, 0)]
            b = path[min(i + 1, len(path) - 1)]
            t = (b - a).normalized()
            nrm = axis.cross(t).normalized()
            rings.append([p + axis * half_width + nrm * half_thickness, p - axis * half_width + nrm * half_thickness,
                          p - axis * half_width - nrm * half_thickness, p + axis * half_width - nrm * half_thickness])
        return self.loft(rings, mat, caps, caps)

    def cylinder(self, a, b, r, n, mat, phase=0.0):
        """A closed prism of n sides from point a to point b."""
        axis = (b - a).normalized()
        u = axis.orthogonal().normalized()
        v = axis.cross(u)
        return self.loft([ring(a, u, v, r, r, n, phase), ring(b, u, v, r, r, n, phase)], mat)

    def finish(self):
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
        return obj


# --------------------------------------------------------------------------------------------
# The weapon


def build_barrel(p):
    part = Part("Barrel", "Barrel")
    n = p["sides"]
    y0c, y1c, rc = p["collar"]
    y0r, y1r, rr = p["rear_barrel"]
    y0f, y1f, rf = p["front_barrel"]
    bore = p["bore"]
    seat_depth, seat_r = p["rim_seat"]
    # Outside, breech to muzzle, then into the muzzle; the breech face is a separate loft.
    outside = [yring(y0c, rc, n), yring(y1c, rc, n), yring(y1c, rr, n), yring(y1r, rr, n),
               yring(y1r, rf, n), yring(y1f, rf, n), yring(y1f, bore, n),
               yring(y1f - p["muzzle_depth"], bore, n)]
    part.loft(outside, "metal", cap_start=False, cap_end=True)
    # Breech face: from the collar's edge in to the rim's seat, down it, and into the chamber.
    breech = [yring(y0c, rc, n), yring(y0c, seat_r, n), yring(seat_depth, seat_r, n),
              yring(seat_depth, bore, n), yring(0.11, bore, n)]
    part.loft(breech, "metal", cap_start=False, cap_end=True)
    # The lug the hinge pin goes through, under the breech.
    hy, hz, hr = p["hinge"]
    part.loft(box(-0.017, 0.017, -0.004, 0.044, hz - 0.011, -rr + 0.008), "metal")
    # Sights: a folded leaf on the rear half with a notch at its back, a strip on the front
    # half, and a blade between two ears at the muzzle. The sight line is their tops.
    sh = p["sight_height"]
    top_r = rr * math.cos(math.pi / n)  # the flat on top
    part.loft(box(-0.012, 0.012, 0.050, 0.150, top_r - 0.002, top_r + 0.008), "dark")
    for x in (-0.009, 0.005):  # the notch's two posts
        part.loft(box(x, x + 0.004, 0.050, 0.058, top_r + 0.008, sh), "dark")
    part.loft(box(-0.007, 0.007, 0.150, 0.160, top_r - 0.002, top_r + 0.012), "dark")  # leaf hinge
    top_f = rf * math.cos(math.pi / n)
    part.loft(box(-0.009, 0.009, 0.215, 0.330, top_f - 0.002, top_f + 0.005), "dark")
    part.loft(box(-0.0022, 0.0022, 0.330, 0.343, top_f, sh), "dark")  # blade
    for x in (-0.012, 0.008):  # ears
        part.loft(box(x, x + 0.004, 0.328, 0.345, top_f, sh - 0.002), "dark")
    # Front sling swivel, under the front half.
    swivel(part, 0.272, -rf * math.cos(math.pi / n) + 0.002, 0.012)
    return part


def swivel(part, y, z, r):
    """A sling swivel: a small base and a loop hanging below it, in the Y-Z plane."""
    part.loft(box(-0.004, 0.004, y - 0.008, y + 0.008, z - 0.007, z + 0.003), "dark")
    path = [Vector((0, y + r * math.cos(a), z - 0.007 + r * math.sin(a))) for a in
            [-math.pi * k / 6 for k in range(0, 7)]]
    part.sweep(path, 0.0025, 0.0018, "dark")


def build_receiver(p):
    part = Part("Receiver", "Weapon")
    rings = [chamfered(*r) for r in p["receiver"]]
    # A middle ring with its flanks pushed out gives the receiver its pyramid facets.
    mid = chamfered(-0.024, 0.036, 0.045, -0.052, 0.016)
    part.loft([rings[0], mid, rings[1], rings[2]], "metal")
    # Cheeks either side of the barrel's lug, for the pin.
    hy, hz, hr = p["hinge"]
    for x0, x1 in ((-0.030, -0.018), (0.018, 0.030)):
        part.loft(box(x0, x1, -0.006, hy + 0.012, hz - 0.012, hz + 0.014), "metal")
    part.cylinder(Vector((-0.032, hy, hz)), Vector((0.032, hy, hz)), hr, 8, "dark")
    # Screws on both flanks.
    for y, z in ((-0.016, 0.022), (-0.066, 0.018), (-0.062, -0.030)):
        for side in (-1, 1):
            x = side * 0.0345
            part.cylinder(Vector((x, y, z)), Vector((x + side * 0.003, y, z)), 0.0045, 6, "dark")
    # Trigger guard, under the receiver.
    guard = [Vector((0, y, z)) for y, z in ((-0.014, -0.048), (-0.013, -0.068), (-0.022, -0.086),
                                             (-0.040, -0.094), (-0.060, -0.090), (-0.072, -0.076),
                                             (-0.077, -0.060), (-0.078, -0.046))]
    part.sweep(guard, 0.0055, 0.0032, "dark")
    # The second loop, behind it, under the wrist: where the fingers go.
    loop = [Vector((0, y, z)) for y, z in ((-0.090, -0.043), (-0.096, -0.064), (-0.110, -0.084),
                                           (-0.132, -0.094), (-0.156, -0.091), (-0.172, -0.078),
                                           (-0.180, -0.062), (-0.182, -0.048))]
    part.sweep(loop, 0.0055, 0.0032, "dark")
    return part


def build_stock(p):
    part = Part("Stock", "Weapon")
    rnd = random.Random(7)
    j = p["wood_jitter"]
    rings = []
    for k, r in enumerate(p["stock"]):
        pts = chamfered(*r)
        if 0 < k < len(p["stock"]) - 1:
            pts = [q + Vector((rnd.uniform(-j, j), rnd.uniform(-j, j), rnd.uniform(-j, j))) for q in pts]
        rings.append(pts)
    faces = part.loft(rings, "wood")
    # Facets, as the concept has them: every quad in two triangles, some of them darker.
    tri = bmesh.ops.triangulate(part.bm, faces=faces, quad_method="BEAUTY")["faces"]
    dark = part.slot("wood_dark")
    for f in tri:
        if rnd.random() < p["wood_dark_share"]:
            f.material_index = dark
    # Butt pad.
    y, w, top, bottom, c = p["stock"][-1]
    t = p["butt_pad"]
    part.loft([chamfered(y + 0.001, w + 0.001, top + 0.001, bottom - 0.001, c),
               chamfered(y - t, w + 0.001, top + 0.001, bottom - 0.001, c)], "rubber")
    # Rear sling swivel, under the stock.
    swivel(part, -0.352, -0.128, 0.012)
    return part


def build_latch(p):
    part = Part("Latch", "Latch")
    top = p["receiver"][0][2]
    # A lever lying along the top of the receiver, pivoting at its back end, with a thumb tab
    # out to the right.
    part.loft(box(-0.009, 0.009, -0.068, -0.010, top - 0.003, top + 0.007), "dark")
    part.loft(box(0.009, 0.026, -0.024, -0.010, top + 0.0, top + 0.006), "dark")
    part.cylinder(Vector((0, -0.060, top - 0.004)), Vector((0, -0.060, top + 0.010)), 0.006, 6, "dark")
    return part


def build_trigger(p):
    part = Part("Trigger", "Trigger")
    blade = [Vector((0, y, z)) for y, z in ((-0.040, -0.044), (-0.042, -0.058), (-0.046, -0.070),
                                             (-0.053, -0.080), (-0.060, -0.084))]
    part.sweep(blade, 0.0038, 0.0025, "dark")
    return part


TRIGGER_PIVOT = Vector((0, -0.040, -0.048))
LATCH_PIVOT = Vector((0, -0.060, 0.046))


def build_round(p, name="Round"):
    """The 40 mm round, base at the origin, nose along +Y."""
    part = Part(name, "Round")
    n = p["round_sides"]
    rim_t, rim_r = p["round_rim"]
    case_len, case_r = p["round_case"]
    part.loft([yring(0.0, rim_r, n), yring(rim_t, rim_r, n)], "brass")
    part.loft([yring(rim_t, case_r, n), yring(case_len, case_r, n)], "case", cap_start=True, cap_end=False)
    head = [yring(y, max(r, 1e-4), n) for y, r in p["round_head"][:-1]]
    faces = part.loft(head, "brass", cap_start=True, cap_end=False)
    # The tip: a fan to one point.
    bm = part.bm
    tip = bm.verts.new(Vector((0, p["round_head"][-1][0], 0)))
    last = [v for v in bm.verts if abs(v.co.y - p["round_head"][-2][0]) < 1e-6 and v is not tip]
    last.sort(key=lambda v: math.atan2(v.co.z, v.co.x))
    idx = part.slot("brass")
    for a, b in zip(last, last[1:] + last[:1]):
        f = bm.faces.new((a, b, tip))
        f.material_index = idx
    return part


# --------------------------------------------------------------------------------------------
# Rigs


def new_armature(name):
    data = bpy.data.armatures.new(name)
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    data.display_type = "STICK"
    obj.show_in_front = True
    return obj


def edit_mode(obj):
    bpy.context.view_layer.objects.active = obj
    for o in bpy.context.view_layer.objects:
        o.select_set(o is obj)
    bpy.ops.object.mode_set(mode="EDIT")


def object_mode():
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")


def add_bone(arm, name, head, tail, parent=None, up=Z, deform=True, connect=False):
    eb = arm.data.edit_bones.new(name)
    eb.head = head
    eb.tail = tail
    eb.align_roll(up)
    eb.use_deform = deform
    if parent:
        eb.parent = arm.data.edit_bones[parent]
        eb.use_connect = connect
    return eb


def weapon_bones(arm, frame, parent=None):
    """The weapon's bones, placed by `frame` (the weapon's space -> the armature's)."""
    rot = frame.to_3x3()
    fwd, up = rot @ Y, rot @ Z

    def at(p):
        return frame @ p

    hy, hz, _ = PARAMS["hinge"]
    add_bone(arm, "Weapon", at(Vector((0, 0, 0))), at(Vector((0, 0.12, 0))), parent, up)
    add_bone(arm, "Barrel", at(Vector((0, hy, hz))), at(Vector((0, hy + 0.12, hz))), "Weapon", up)
    add_bone(arm, "Round", at(Vector((0, 0, 0))), at(Vector((0, 0.05, 0))), "Barrel", up)
    # Every one of them points along the weapon with its Z up, so that a bone's own axes are the
    # weapon's: X right, Y forward, Z up.
    add_bone(arm, "Latch", at(LATCH_PIVOT), at(LATCH_PIVOT + Vector((0, 0.04, 0))), "Weapon", up)
    add_bone(arm, "Trigger", at(TRIGGER_PIVOT), at(TRIGGER_PIVOT + Vector((0, 0.03, 0))), "Weapon", up)


def build_weapon_mesh(name, frame):
    """The weapon as one mesh, every vertex weighted wholly to the bone its part rides on."""
    p = PARAMS
    parts = [build_barrel(p), build_receiver(p), build_stock(p), build_latch(p), build_trigger(p),
             build_round(p)]
    objs = []
    for part in parts:
        bone = part.bone
        obj = part.finish()
        vg = obj.vertex_groups.new(name=bone)
        vg.add(range(len(obj.data.vertices)), 1.0, "REPLACE")
        objs.append(obj)
    for obj in objs:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs, selected_objects=objs):
        bpy.ops.object.join()
    obj = objs[0]
    obj.name = name
    obj.data.name = name
    obj.data.transform(frame)
    return obj


def skin(mesh_obj, arm):
    mesh_obj.parent = arm
    mod = mesh_obj.modifiers.new("Armature", "ARMATURE")
    mod.object = arm


# --------------------------------------------------------------------------------------------
# Animation helpers


def smooth(t):
    t = min(max(t, 0.0), 1.0)
    return t * t * (3 - 2 * t)


def ease_out(t):
    t = min(max(t, 0.0), 1.0)
    return 1 - (1 - t) ** 3


def ease_in(t):
    t = min(max(t, 0.0), 1.0)
    return t ** 3


class Track:
    """A value through time from (frame, value, easing-into-it) keys; values are floats or tuples."""

    def __init__(self, keys):
        self.keys = keys

    def at(self, f):
        keys = self.keys
        if f <= keys[0][0]:
            return keys[0][1]
        for (f0, v0, _), (f1, v1, ease) in zip(keys, keys[1:]):
            if f0 <= f <= f1:
                t = (f - f0) / (f1 - f0) if f1 > f0 else 1.0
                t = {"smooth": smooth, "out": ease_out, "in": ease_in, "linear": lambda x: x,
                     "step": lambda x: 1.0 if x >= 1.0 else 0.0}[ease](t)
                if isinstance(v0, tuple):
                    return tuple(a + (b - a) * t for a, b in zip(v0, v1))
                return v0 + (v1 - v0) * t
        return keys[-1][1]


def key_pose(pb, f, loc=None, rot=None, scale=None):
    """Keys a pose bone: loc in the bone's own axes, rot an XYZ Euler [deg] or a Quaternion."""
    pb.rotation_mode = "QUATERNION"
    if loc is not None:
        pb.location = loc
        pb.keyframe_insert("location", frame=f)
    if rot is not None:
        q = rot if isinstance(rot, Quaternion) else Euler([math.radians(a) for a in rot], "XYZ").to_quaternion()
        pb.rotation_quaternion = q
        pb.keyframe_insert("rotation_quaternion", frame=f)
    if scale is not None:
        pb.scale = (scale, scale, scale)
        pb.keyframe_insert("scale", frame=f)


def new_action(arm, name):
    action = bpy.data.actions.new(name)
    arm.animation_data_create()
    arm.animation_data.action = action
    for pb in arm.pose.bones:
        pb.rotation_mode = "QUATERNION"
        pb.location = (0, 0, 0)
        pb.rotation_quaternion = (1, 0, 0, 0)
        pb.scale = (1, 1, 1)
    return action


def stash(arm, action, track_name, length):
    """Puts the action on an NLA track of its own, named as the glTF animation will be."""
    action.use_fake_user = True
    action.use_frame_range = True
    action.frame_range = (0, length)
    ad = arm.animation_data
    track = ad.nla_tracks.new()
    track.name = track_name
    strip = track.strips.new(track_name, 0, action)
    if hasattr(strip, "action_slot") and action.slots:
        strip.action_slot = action.slots[0]
    strip.action_frame_start, strip.action_frame_end = 0, length
    # Muted while the next action is authored, or its keys would pick up this one's pose.
    track.mute = True
    ad.action = None


def pose_matrix(arm, name):
    """A bone's evaluated pose, in the armature's space, with constraints."""
    return arm.pose.bones[name].matrix.copy()


def basis_for(arm, name, armature_space):
    """The pose-basis a bone needs to sit at `armature_space`, given its parent's current pose."""
    pb = arm.pose.bones[name]
    rest = pb.bone.matrix_local
    if pb.parent is None:
        return rest.inverted() @ armature_space
    parent_rest = pb.parent.bone.matrix_local
    parent_pose = pb.parent.matrix
    return (parent_pose @ parent_rest.inverted() @ rest).inverted() @ armature_space


def blend(a, b, t):
    la, ra, _ = a.decompose()
    lb, rb, _ = b.decompose()
    return Matrix.LocRotScale(la.lerp(lb, t), ra.slerp(rb, t), None)


def follow(arm, timeline, f):
    """
    Where something is at frame f from a timeline of (frame, spec) holds, blending smoothly from
    one spec to the next. A spec is a function of the armature's current (evaluated) pose that
    returns an armature-space matrix, so that something holding the barrel moves with it.
    """
    if f <= timeline[0][0]:
        return timeline[0][1](arm)
    for (f0, s0), (f1, s1) in zip(timeline, timeline[1:]):
        if f0 <= f <= f1:
            if s0 is s1 or f1 <= f0:
                return s1(arm)
            return blend(s0(arm), s1(arm), smooth((f - f0) / (f1 - f0)))
    return timeline[-1][1](arm)


def on_bone(name, offset):
    """A spec: held at `offset` from a bone's pose (scale stripped)."""

    def spec(arm):
        loc, rot, _ = pose_matrix(arm, name).decompose()
        return Matrix.LocRotScale(loc, rot, None) @ offset

    return spec


def fixed(matrix):
    return lambda arm: matrix


# --------------------------------------------------------------------------------------------
# The weapon's own motion, shared by both rigs. Each returns {bone: Track of (loc, rot[deg])}.

def weapon_tracks_fire(kick=1.0):
    """The shot: the trigger, and the weapon's kick scaled by `kick` (less with it shouldered)."""
    t = PARAMS["trigger_degrees"]

    def k(v):
        return tuple(x * kick for x in v)

    return {
        "Trigger": Track([(0, (0.0,), "linear"), (1, (-t,), "out"), (7, (-t,), "linear"), (11, (0.0,), "smooth")]),
        # Kick: back, up, muzzle up; then settle a hair past rest and back.
        "Weapon.loc": Track([(0, (0, 0, 0), "linear"), (1, (0, 0, 0), "linear"), (3, k((0.0, -0.055, 0.016)), "out"),
                             (8, k((0.0, -0.018, 0.007)), "smooth"), (13, k((0.0, 0.004, -0.001)), "smooth"),
                             (18, (0, 0, 0), "smooth")]),
        "Weapon.rot": Track([(0, (0, 0, 0), "linear"), (1, (0, 0, 0), "linear"), (3, k((10.0, 0.8, -0.6)), "out"),
                             (8, k((4.0, 0.3, 0.0)), "smooth"), (13, k((-0.6, 0, 0)), "smooth"),
                             (18, (0, 0, 0), "smooth")]),
    }


RELOAD = dict(
    length=75,
    latch=(6, 10, 60, 63),       # pressed over 6-10, held, let go over 60-63
    open=(10, 17, 19),           # drops 10-17, bounces back to rest by 19
    extract=(19, 22),            # the extractor pulls the case back
    eject=(22, 31),              # it flies; hidden from the end of this
    pouch=(33, 35),              # moved there, hidden, by the first; in the hand by the second
    entry=46,                    # behind the chamber, lined up
    seated=52,
    close=(58, 62),
)


# The weapon's pose while it is open, from where it is held. It turns about the breech, muzzle
# up, so the right hand stays low, the stock goes down out of shot, and the barrel, dropped open
# below it, points ahead again with the chamber facing the eye over the receiver.
RELOAD_LOC = (-0.085, -0.055, 0.060)
RELOAD_ROT = (40.0, -15.0, 10.0)


def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def weapon_tracks_reload():
    r = RELOAD
    od = PARAMS["open_degrees"]
    ld = PARAMS["latch_degrees"]
    l0, l1, l2, l3 = r["latch"]
    o0, o1, o2 = r["open"]
    c0, c1 = r["close"]
    n = r["length"]
    return {
        "Latch": Track([(0, (0.0,), "linear"), (l0, (0.0,), "linear"), (l1, (ld,), "out"), (l2, (ld,), "linear"),
                        (l3, (0.0,), "in")]),
        "Barrel": Track([(0, (0.0,), "linear"), (o0, (0.0,), "linear"), (o1, (-od - 3.0,), "in"),
                         (o2, (-od,), "smooth"), (c0, (-od,), "linear"), (c1, (0.0,), "in"),
                         (c1 + 2, (0.0,), "linear")]),
        "Trigger": Track([(0, (0.0,), "linear"), (n, (0.0,), "linear")]),
        # The weapon comes in toward the middle and rolls its top toward the camera, so the
        # breech is in view; flicks to throw the case; jolts as it is shut; goes back.
        "Weapon.loc": Track([(0, (0, 0, 0), "linear"), (9, RELOAD_LOC, "smooth"),
                             (19, add(RELOAD_LOC, (0.0, -0.005, 0.005)), "smooth"),
                             (23, add(RELOAD_LOC, (0.010, -0.040, 0.025)), "out"),
                             (28, RELOAD_LOC, "smooth"), (50, add(RELOAD_LOC, (-0.005, 0.0, 0.005)), "smooth"),
                             (c0, RELOAD_LOC, "smooth"), (c1, add(RELOAD_LOC, (0.0, 0.010, 0.020)), "in"),
                             (c1 + 3, RELOAD_LOC, "out"), (n - 2, (0, 0, 0), "smooth"),
                             (n, (0, 0, 0), "linear")]),
        "Weapon.rot": Track([(0, (0, 0, 0), "linear"), (9, RELOAD_ROT, "smooth"),
                             (19, add(RELOAD_ROT, (-2.0, -2.0, 0.0)), "smooth"),
                             (23, add(RELOAD_ROT, (14.0, -10.0, -4.0)), "out"),
                             (28, RELOAD_ROT, "smooth"), (50, add(RELOAD_ROT, (-1.0, -1.0, 0.0)), "smooth"),
                             (c0, RELOAD_ROT, "smooth"), (c1, add(RELOAD_ROT, (8.0, 4.0, 0.0)), "in"),
                             (c1 + 3, add(RELOAD_ROT, (-2.0, 0.0, 0.0)), "out"), (n - 2, (0, 0, 0), "smooth"),
                             (n, (0, 0, 0), "linear")]),
    }


def weapon_tracks_idle():
    # Breathing: two seconds, a couple of millimetres and a fraction of a degree.
    return {
        "Weapon.loc": Track([(0, (0, 0, 0), "smooth"), (30, (0.0, 0.0, -0.0025), "smooth"),
                             (60, (0, 0, 0), "smooth")]),
        "Weapon.rot": Track([(0, (0, 0, 0), "smooth"), (30, (-0.5, 0.0, 0.2), "smooth"),
                             (60, (0, 0, 0), "smooth")]),
    }


def key_weapon(arm, tracks, length, base=None):
    """
    Keys the weapon's bones on every frame from its tracks; bones without one stay at rest.
    `base` is a pose of the whole weapon the tracks' motion is made from — the sights up — in the
    weapon bone's own space; without one it is where the weapon is held.
    """
    for f in range(length + 1):
        pb = arm.pose.bones
        wl = tracks.get("Weapon.loc")
        wr = tracks.get("Weapon.rot")
        loc = Vector(wl.at(f) if wl else (0, 0, 0))
        rot = Euler([math.radians(a) for a in (wr.at(f) if wr else (0, 0, 0))], "XYZ").to_quaternion()
        if base is not None:
            loc, rot, _ = (base @ Matrix.LocRotScale(loc, rot, None)).decompose()
        key_pose(pb["Weapon"], f, loc=loc, rot=rot)
        b = tracks.get("Barrel")
        key_pose(pb["Barrel"], f, loc=(0, 0, 0), rot=(b.at(f)[0] if b else 0.0, 0, 0))
        latch = tracks.get("Latch")
        # The lever swings out to the right, about the receiver's vertical: the bone's -X.
        key_pose(pb["Latch"], f, loc=(0, 0, 0), rot=(0, 0, -(latch.at(f)[0] if latch else 0.0)))
        trig = tracks.get("Trigger")
        # Pulled: its blade, below the pivot, swings back.
        key_pose(pb["Trigger"], f, loc=(0, 0, 0), rot=((trig.at(f)[0] if trig else 0.0), 0, 0))


class RoundPath:
    """
    The reload's round, as armature-space holds (see `follow`): seated, pulled back by the
    extractor, thrown, hidden and moved to the pouch, carried up, lined up behind the chamber and
    pushed home. Hidden only where nobody sees it change: in flight off screen, and in the pouch.
    """

    def __init__(self, arm, pouch, lift):
        r = RELOAD
        self.arm = arm
        seat = arm.data.bones["Round"].matrix_local
        barrel_rest = arm.data.bones["Barrel"].matrix_local
        seat_off = barrel_rest.inverted() @ seat
        seated = on_bone("Barrel", seat_off)
        self.extracted = on_bone("Barrel", seat_off @ Matrix.Translation((0, -0.026, 0)))
        entry = on_bone("Barrel", seat_off @ Matrix.Translation((0, -0.115, 0)))
        e0, e1 = r["eject"]
        p0, p1 = r["pouch"]
        self.e0, self.e1, self.p1 = e0, e1, p1
        self.start = None
        self.timeline = [(0, seated), (r["extract"][0], seated), (r["extract"][1], self.extracted),
                         (e0, self.flight), (p0, self.flight), (p0 + 0.001, fixed(pouch)), (p1, fixed(pouch)),
                         (p1 + 5, fixed(lift)), (r["entry"], entry), (r["seated"], seated),
                         (r["length"], seated)]

    def prepare(self):
        """The flight starts from where the extractor left the round, at the flight's first frame."""
        bpy.context.scene.frame_set(self.e0)
        self.start = self.extracted(self.arm)

    def flight(self, arm):
        # Thrown up, back and out to the right, tumbling.
        t = (bpy.context.scene.frame_current - self.e0) / FPS
        loc, rot, _ = self.start.decompose()
        pos = loc + Vector((0.9, -1.5, 1.9)) * t + Vector((0, 0, -9.8)) * (0.5 * t * t)
        spin = Quaternion(Vector((1, 0.3, 0.2)).normalized(), 16.0 * t)
        return Matrix.LocRotScale(pos, spin @ rot, None)

    def at(self, f):
        return follow(self.arm, self.timeline, f)

    def visible(self, f):
        return 0.0 if self.e1 <= f < self.p1 else 1.0


# --------------------------------------------------------------------------------------------
# The world model

def build_world():
    arm = new_armature("LauncherRig")
    edit_mode(arm)
    weapon_bones(arm, Matrix.Identity(4))
    object_mode()
    mesh = build_weapon_mesh("Launcher", Matrix.Identity(4))
    skin(mesh, arm)
    muzzle = bpy.data.objects.new("Muzzle", None)
    bpy.context.scene.collection.objects.link(muzzle)
    muzzle.empty_display_size = 0.02
    attach(muzzle, arm, "Barrel", Vector((0, PARAMS["front_barrel"][1], 0)))
    return arm, mesh, muzzle


def attach(obj, arm, bone, at_point):
    """Parents an object to a bone, placed at an armature-space point with the armature at rest."""
    obj.parent = arm
    obj.parent_type = "BONE"
    obj.parent_bone = bone
    obj.matrix_parent_inverse = Matrix.Identity(4)
    b = arm.data.bones[bone]
    # A bone's child hangs off its tail, in the bone's own axes.
    tail = arm.matrix_world @ b.matrix_local @ Matrix.Translation((0, b.length, 0))
    obj.matrix_basis = tail.inverted() @ Matrix.Translation(at_point)


def animate_world(arm):
    def own(tracks):
        return {k: v for k, v in tracks.items() if not k.startswith("Weapon.")}

    for name, tracks, length in (("fire", own(weapon_tracks_fire()), 18),
                                 ("reload", own(weapon_tracks_reload()), RELOAD["length"]), ("idle_loop", {}, 1)):
        action = new_action(arm, "Launcher." + name)
        key_weapon(arm, tracks, length)
        key_all(arm, length)
        if name == "reload":
            # Without hands, it comes up from below the weapon.
            pouch = Matrix.Translation((0.0, -0.20, -0.25))
            lift = Matrix.LocRotScale(Vector((0.0, -0.16, 0.0)), Euler((math.radians(30), 0, 0)), None)
            key_round(arm, RoundPath(arm, pouch, lift), length)
        stash(arm, action, name, length)


def key_all(arm, length):
    """Every bone gets a key at both ends, so an animation leaves nothing of the last one behind."""
    scene = bpy.context.scene
    for f in (0, length):
        scene.frame_set(f)
        for pb in arm.pose.bones:
            for path in ("location", "rotation_quaternion", "scale"):
                pb.keyframe_insert(path, frame=f)


def key_round(arm, path, length):
    scene = bpy.context.scene
    path.prepare()
    pb = arm.pose.bones["Round"]
    for f in range(length + 1):
        scene.frame_set(f)
        loc, rot, _ = basis_for(arm, "Round", path.at(f)).decompose()
        key_pose(pb, f, loc=loc, rot=rot, scale=path.visible(f))


# --------------------------------------------------------------------------------------------
# The viewmodel: the weapon in arms.blend's arms


def hip_frame():
    rx, ry, rz = (math.radians(a) for a in HIP_ROTATION)
    return Matrix.LocRotScale(HIP_POSITION, Euler((rx, ry, rz), "XYZ"), None)


# The rear sight's notch, in the weapon's own space: where the line of sight leaves from.
NOTCH = Vector((0, 0.054, PARAMS["sight_height"]))


def aim_frame():
    """The weapon square to the eye, the notch AIM_NOTCH_DISTANCE ahead of it on the line of sight."""
    return Matrix.Translation(Vector((0, AIM_NOTCH_DISTANCE, 0)) - NOTCH)


def import_arms():
    """arms.blend's arms, mirrored, as (mesh object in world space, {bone: (head, tail, roll-up, parent, connect)})."""
    with bpy.data.libraries.load(ARMS_BLEND, link=False) as (src, dst):
        dst.objects = [n for n in src.objects if n in ("Arms", "Armature", "ScaleReference")]
    objs = {o.name: o for o in dst.objects if o}
    for o in objs.values():
        bpy.context.scene.collection.objects.link(o)
    src_arm, src_mesh = objs["Armature"], objs["Arms"]
    # The mesh, with its mirror applied and the armature left out, in world space.
    for m in src_mesh.modifiers:
        if m.type == "ARMATURE":
            m.show_viewport = False
    dg = bpy.context.evaluated_depsgraph_get()
    mesh = bpy.data.meshes.new_from_object(src_mesh.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
    mesh.transform(src_mesh.matrix_world)
    mesh.validate()
    mesh.name = "Arms"
    names = list(src_mesh.vertex_groups.keys())
    mats = list(src_mesh.data.materials)
    bones = {}
    for b in src_arm.data.bones:
        m = src_arm.matrix_world @ b.matrix_local
        bones[b.name] = dict(head=src_arm.matrix_world @ b.head_local, tail=src_arm.matrix_world @ b.tail_local,
                             z=(m.to_3x3() @ Z).normalized(), parent=b.parent.name if b.parent else None,
                             connect=b.use_connect, deform=b.use_deform)
    obj = bpy.data.objects.new("Arms", mesh)
    bpy.context.scene.collection.objects.link(obj)
    if not obj.vertex_groups:  # the names travel with the mesh since Blender 3.0
        for n in names:
            obj.vertex_groups.new(name=n)
    obj.data.materials.clear()
    for m in mats:
        obj.data.materials.append(m)
    # The skin's image, by where it is rather than relative to arms.blend; and wired to the
    # surface's colour, which in arms.blend it is not (Blender's viewport shows an image node
    # either way; a glTF only carries what is connected).
    for img in bpy.data.images:
        if img.filepath.endswith("ArmSkin.png"):
            img.filepath = os.path.join(os.path.dirname(ARMS_BLEND), "ArmSkin.png")
            img.reload()
    for mat in mats:
        if mat and mat.node_tree:
            nodes = mat.node_tree.nodes
            tex = next((n for n in nodes if n.type == "TEX_IMAGE" and n.image), None)
            bsdf = next((n for n in nodes if n.type == "BSDF_PRINCIPLED"), None)
            if tex and bsdf and not bsdf.inputs["Base Color"].is_linked:
                mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    for o in objs.values():
        bpy.data.objects.remove(o)
    obj.name = "Arms"
    return obj, bones


def side_of(name):
    return "L" if name.endswith(".L") else "R" if name.endswith(".R") else None


HAND_BONES = ("Palm", "Pinky", "Ring", "Middle", "Index", "Thumb")


def place_arms(mesh_obj, bones):
    """Moves each arm bodily so its shoulder is at SHOULDERS[side], and shrinks the hands."""
    out = {}
    wrist = {}
    transforms = {}
    for side in ("L", "R"):
        shoulder = bones["Bicep." + side]["head"]
        transforms[side] = Matrix.Translation(SHOULDERS[side]) @ Matrix.Scale(ARM_SCALE, 4) @ Matrix.Translation(-shoulder)
        wrist[side] = transforms[side] @ bones["Palm." + side]["head"]

    def hand_scale(p, side, w):
        return wrist[side] + (p - wrist[side]) * (1.0 + (HAND_SCALE - 1.0) * w)

    for name, b in bones.items():
        side = side_of(name)
        if side is None:
            continue
        t = transforms[side]
        head, tail = t @ b["head"], t @ b["tail"]
        if name.split(".")[0].rstrip("0123456789") in HAND_BONES and not name.startswith("IK"):
            head, tail = hand_scale(head, side, 1.0), hand_scale(tail, side, 1.0)
        out[name] = dict(b, head=head, tail=tail)
    # The mesh: by which side its weights are on, and by how much of it is hand.
    groups = {g.index: g.name for g in mesh_obj.vertex_groups}
    for v in mesh_obj.data.vertices:
        w_side = {"L": 0.0, "R": 0.0}
        w_hand = 0.0
        total = 0.0
        for g in v.groups:
            name = groups[g.group]
            s = side_of(name)
            if s:
                w_side[s] += g.weight
                total += g.weight
                if name.split(".")[0].rstrip("0123456789") in HAND_BONES:
                    w_hand += g.weight
        side = "L" if w_side["L"] >= w_side["R"] else "R"
        p = transforms[side] @ v.co
        v.co = hand_scale(p, side, w_hand / total if total else 0.0)
    return out


def build_viewmodel():
    arms_obj, src_bones = import_arms()
    bones = place_arms(arms_obj, src_bones)
    arm = new_armature("ViewmodelRig")
    edit_mode(arm)
    add_bone(arm, "Root", Vector((0, 0, 0)), Vector((0, 0.05, 0)), None, Z, deform=True)
    frame = hip_frame()
    weapon_bones(arm, frame, "Root")
    # The arms' bones, in an order that has every parent before its children.
    order = [n for n in bones if not n.startswith("IK")]
    done = set()
    while order:
        for n in list(order):
            parent = bones[n]["parent"]
            if parent in (None, "Root") or parent in done:
                b = bones[n]
                add_bone(arm, n, b["head"], b["tail"], "Root" if parent in (None, "Root") else parent, b["z"],
                         deform=True, connect=b["connect"])
                done.add(n)
                order.remove(n)
    # IK targets, one per hand, with the palm's rest orientation so that copying their rotation
    # is the hand's; poles for the elbows.
    for side in ("L", "R"):
        palm = arm.data.edit_bones["Palm." + side]
        parent = "Weapon" if side == "R" else "Root"
        eb = add_bone(arm, "IK.Hand." + side, palm.head.copy(), palm.tail.copy(), parent, palm.z_axis.copy(), deform=False)
        eb.roll = palm.roll
        add_bone(arm, "IK.Pole." + side, POLES[side], POLES[side] + Vector((0, 0.05, 0)), "Root", Z, deform=False)
    object_mode()

    weapon = build_weapon_mesh("LauncherFP", frame)
    skin(weapon, arm)
    skin(arms_obj, arm)
    # Named apart from the world model's, as Blender insists; Godot looks for "Muzzle*".
    muzzle = bpy.data.objects.new("Muzzle_FP", None)
    bpy.context.scene.collection.objects.link(muzzle)
    muzzle.empty_display_size = 0.02
    attach(muzzle, arm, "Barrel", frame @ Vector((0, PARAMS["front_barrel"][1], 0)))

    for side in ("L", "R"):
        fore = arm.pose.bones["Forearm." + side]
        ik = fore.constraints.new("IK")
        ik.target, ik.subtarget = arm, "IK.Hand." + side
        ik.pole_target, ik.pole_subtarget = arm, "IK.Pole." + side
        ik.chain_count = 2
        palm = arm.pose.bones["Palm." + side]
        cr = palm.constraints.new("COPY_ROTATION")
        cr.target, cr.subtarget = arm, "IK.Hand." + side
    return arm, arms_obj, weapon, muzzle


def tune_poles(arm):
    """The pole angle that puts each elbow on its pole's side, of the four square ones."""
    scene = bpy.context.scene
    for side in ("L", "R"):
        ik = arm.pose.bones["Forearm." + side].constraints[0]
        best = None
        for deg in (-90, 0, 90, 180):
            ik.pole_angle = math.radians(deg)
            bpy.context.view_layer.update()
            sh = arm.pose.bones["Bicep." + side].head
            wr = arm.pose.bones["Forearm." + side].tail
            el = arm.pose.bones["Forearm." + side].head
            pole = arm.pose.bones["IK.Pole." + side].head
            mid = (sh + wr) / 2
            axis = (wr - sh).normalized()

            def perp(v):
                return (v - axis * v.dot(axis)).normalized()

            score = perp(el - mid).dot(perp(pole - mid))
            if best is None or score > best[0]:
                best = (score, deg)
        ik.pole_angle = math.radians(best[1])
        print(f"pole angle {side}: {best[1]} deg (alignment {best[0]:.3f})")


def hand_matrix(arm, side, wrist, fingers, palm_normal):
    """An IK target's armature-space matrix: the wrist there, the fingers along `fingers`, the palm facing `palm_normal`."""
    bone = arm.data.bones["Palm." + side]
    rest = bone.matrix_local.to_3x3()
    rest_normal = rest.inverted() @ Vector((0, 0, -1))  # arms.blend's palms face down at rest
    f = fingers.normalized()
    n = (palm_normal - f * palm_normal.dot(f)).normalized()
    local_y = Vector((0, 1, 0))
    ln = (rest_normal - local_y * rest_normal.dot(local_y)).normalized()
    src = Matrix((local_y, ln, local_y.cross(ln))).transposed()
    dst = Matrix((f, n, f.cross(n))).transposed()
    rot = dst @ src.inverted()
    return Matrix.Translation(wrist) @ rot.to_4x4()


# Grips, in the weapon's own space: wrist position, finger direction, palm normal.
GRIP_R = (Vector((0.052, -0.170, -0.006)), Vector((-0.10, 0.62, -0.78)), Vector((-1.0, 0.0, -0.1)))
GRIP_BARREL_L = (Vector((-0.058, 0.150, -0.068)), Vector((0.78, 0.55, 0.30)), Vector((-0.1, 0.0, 1.0)))
# Holding the round from below, lying along the fingers with the thumb up its left side: in the
# round's own space (base at the origin, nose +Y), so its base stays in view as it goes in.
GRIP_ROUND_L = (Vector((0.004, -0.078, -0.046)), Vector((0.05, 1.0, 0.28)), Vector((0.0, -0.25, 1.0)))

# Finger curls [deg] per segment (1, 2, 3) for each hand pose.
CURLS = {
    "grip_r": dict(Index=(25, 40, 20), Middle=(70, 80, 45), Ring=(72, 82, 45), Pinky=(75, 80, 45), Thumb=(15, 30, 20)),
    "trigger_r": dict(Index=(38, 58, 30), Middle=(70, 80, 45), Ring=(72, 82, 45), Pinky=(75, 80, 45), Thumb=(15, 30, 20)),
    "barrel_l": dict(Index=(30, 30, 15), Middle=(34, 32, 16), Ring=(36, 34, 18), Pinky=(40, 38, 20), Thumb=(5, 10, 5)),
    "open_l": dict(Index=(8, 10, 5), Middle=(10, 12, 6), Ring=(12, 14, 6), Pinky=(14, 16, 8), Thumb=(0, 5, 0)),
    "round_l": dict(Index=(35, 45, 25), Middle=(45, 55, 30), Ring=(60, 65, 35), Pinky=(70, 70, 40), Thumb=(15, 20, 10)),
}


def curl_rotations(arm, side, pose):
    """The pose-basis rotation of every finger segment for a hand pose: each curled toward the palm."""
    out = {}
    normal = Vector((0, 0, -1))  # the palms face down at rest, and the arms were moved, not turned
    for finger, angles in CURLS[pose].items():
        for seg, deg in enumerate(angles, start=1):
            name = f"{finger}{seg}.{side}"
            bone = arm.data.bones[name]
            d = (bone.tail_local - bone.head_local).normalized()
            axis = d.cross(normal)
            if axis.length < 1e-4:
                continue
            local = bone.matrix_local.to_3x3().inverted() @ axis.normalized()
            out[name] = Quaternion(local, math.radians(deg))
    return out


def key_fingers(arm, side, timeline, length):
    """Keys a hand's fingers from (frame, pose-name) holds, blending between poses."""
    poses = {}
    for _, name in timeline:
        poses.setdefault(name, curl_rotations(arm, side, name))
    bones = sorted({b for p in poses.values() for b in p})
    for f in range(length + 1):
        if f <= timeline[0][0]:
            a = b = timeline[0][1]
            t = 0.0
        else:
            a = b = timeline[-1][1]
            t = 0.0
            for (f0, n0), (f1, n1) in zip(timeline, timeline[1:]):
                if f0 <= f <= f1:
                    a, b, t = n0, n1, smooth((f - f0) / (f1 - f0)) if f1 > f0 else 1.0
                    break
        for name in bones:
            qa = poses[a].get(name, Quaternion())
            qb = poses[b].get(name, Quaternion())
            key_pose(arm.pose.bones[name], f, loc=(0, 0, 0), rot=qa.slerp(qb, t))


def animate_viewmodel(arm):
    scene = bpy.context.scene
    frame = hip_frame()
    weapon_rest = arm.data.bones["Weapon"].matrix_local
    barrel_rest = arm.data.bones["Barrel"].matrix_local

    def grip(spec_frame, wrist, fingers, normal, side):
        rot = spec_frame.to_3x3()
        return hand_matrix(arm, side, spec_frame @ wrist, rot @ fingers, rot @ normal)

    # The right hand rides on the weapon: its target is the weapon bone's child, set once.
    right = grip(frame, *GRIP_R, "R")
    right_loc, right_rot, _ = (arm.data.bones["IK.Hand.R"].matrix_local.inverted() @ right).decompose()
    # On the barrel: an offset from the barrel bone's pose.
    barrel_grip = barrel_rest.inverted() @ grip(frame, *GRIP_BARREL_L, "L")
    on_barrel = on_bone("Barrel", barrel_grip)
    released = fixed(Matrix.Translation((0.0, -0.03, -0.06)) @ grip(frame, *GRIP_BARREL_L, "L"))
    reach = fixed(hand_matrix(arm, "L", Vector((-0.10, 0.24, -0.50)), Vector((0.3, 0.6, 0.6)),
                              Vector((0.4, 0, 0.8))))
    # The round the left hand fetches: from a pouch low on the left, up past the weapon.
    pouch = Matrix.LocRotScale(Vector((-0.06, 0.30, -0.44)), Euler((math.radians(-50), 0, math.radians(35))), None)
    lift = Matrix.LocRotScale(Vector((-0.03, 0.36, -0.22)), Euler((math.radians(10), 0, math.radians(20))), None)
    path = RoundPath(arm, pouch, lift)
    # Holding it: an offset from where the round is meant to be, not from its pose, which is
    # scaled to nothing while it is hidden.
    round_rest = arm.data.bones["Round"].matrix_local
    round_grip = round_rest.inverted() @ grip(round_rest, *GRIP_ROUND_L, "L")
    on_round = lambda arm_: path.at(scene.frame_current) @ round_grip

    # The elbows' pole angles, with the hands where they hold the weapon (posed, not keyed).
    for side, (loc, rot) in (("R", (right_loc, right_rot)),
                             ("L", basis_for(arm, "IK.Hand.L", on_barrel(arm)).decompose()[:2])):
        pb = arm.pose.bones["IK.Hand." + side]
        pb.rotation_mode = "QUATERNION"
        pb.location, pb.rotation_quaternion = loc, rot
    tune_poles(arm)

    # The sights up: the weapon square to the eye with the notch on the line of sight, the arms
    # re-solved to it from shoulders that stay where they are.
    aimed = weapon_rest.inverted() @ aim_frame()
    r = RELOAD
    trigger_r = [(0, "grip_r"), (1, "trigger_r"), (7, "trigger_r"), (11, "grip_r")]
    clips = {
        "idle_loop": (60, weapon_tracks_idle(), None, [(0, on_barrel), (60, on_barrel)],
                      [(0, "grip_r")], [(0, "barrel_l")], False),
        "fire": (18, weapon_tracks_fire(), None, [(0, on_barrel), (18, on_barrel)],
                 trigger_r, [(0, "barrel_l")], False),
        "aim": (1, {}, aimed, [(0, on_barrel), (1, on_barrel)], [(0, "grip_r")], [(0, "barrel_l")], False),
        "aim_fire": (18, weapon_tracks_fire(AIM_KICK), aimed, [(0, on_barrel), (18, on_barrel)],
                     trigger_r, [(0, "barrel_l")], False),
        "reload": (r["length"], weapon_tracks_reload(), None,
                   [(0, on_barrel), (r["extract"][0], on_barrel), (r["extract"][1] + 2, released),
                    (r["pouch"][0] - 2, reach), (r["pouch"][1], on_round), (r["seated"] + 1, on_round),
                    (r["close"][0] - 2, on_barrel), (r["length"], on_barrel)],
                   [(0, "grip_r")],
                   [(0, "barrel_l"), (r["extract"][0], "barrel_l"), (r["extract"][1] + 2, "open_l"),
                    (r["pouch"][0] - 1, "open_l"), (r["pouch"][1], "round_l"), (r["seated"], "round_l"),
                    (r["seated"] + 3, "open_l"), (r["close"][0] - 2, "barrel_l")], True),
    }
    for name, (length, tracks, base, left, fingers_r, fingers_l, has_round) in clips.items():
        action = new_action(arm, "Viewmodel." + name)
        key_weapon(arm, tracks, length, base)
        for f in (0, length):
            key_pose(arm.pose.bones["IK.Hand.R"], f, loc=right_loc, rot=right_rot)
        key_fingers(arm, "R", fingers_r, length)
        key_fingers(arm, "L", fingers_l, length)
        key_all(arm, length)
        if has_round:
            key_round(arm, path, length)
        target = arm.pose.bones["IK.Hand.L"]
        for f in range(length + 1):
            scene.frame_set(f)
            m = follow(arm, left, f)
            loc, rot, _ = basis_for(arm, "IK.Hand.L", m).decompose()
            key_pose(target, f, loc=loc, rot=rot)
        stash(arm, action, name, length)


# --------------------------------------------------------------------------------------------
# Scene, export


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.fps = FPS
    scene.unit_settings.system = "METRIC"
    bpy.context.preferences.edit.keyframe_new_interpolation_type = "LINEAR"
    bpy.context.preferences.filepaths.save_version = 0  # no .blend1 beside the .blend


def collect(name, objs):
    coll = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(coll)
    for o in objs:
        for c in o.users_collection:
            c.objects.unlink(o)
        coll.objects.link(o)
    return coll


def export(path, objs):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    object_mode()
    for o in objs:
        if o.animation_data:
            o.animation_data.action = None
            for track in o.animation_data.nla_tracks:
                track.mute = False
    for o in bpy.context.view_layer.objects:
        o.select_set(o in objs)
    bpy.ops.export_scene.gltf(
        filepath=path, export_format="GLB", use_selection=True, export_yup=True, export_apply=True,
        export_animations=True, export_animation_mode="NLA_TRACKS", export_force_sampling=True,
        export_def_bones=True, export_optimize_animation_size=False, export_rest_position_armature=True,
        export_anim_single_armature=True, export_skins=True, export_extras=False)
    print("wrote", os.path.relpath(path, REPO))


def report(arm):
    """Where the muzzle and the sights are, from the eye, as the clips pose them."""
    scene = bpy.context.scene

    def godot(v):
        return f"({v.x:.4f}, {v.z:.4f}, {-v.y:.4f})"

    ad = arm.animation_data
    for track in ad.nla_tracks:
        track.mute = track.name != "aim"
    scene.frame_set(0)
    weapon = arm.pose.bones["Weapon"].matrix @ arm.data.bones["Weapon"].matrix_local.inverted() @ hip_frame()
    front = Vector((0, 0.3365, PARAMS["sight_height"]))
    print("aimed: notch at", godot(weapon @ NOTCH), "front blade at", godot(weapon @ front), "(Godot, from the eye)")
    for track in ad.nla_tracks:
        track.mute = track.name != "idle_loop"
    scene.frame_set(0)
    weapon = arm.pose.bones["Weapon"].matrix @ arm.data.bones["Weapon"].matrix_local.inverted() @ hip_frame()
    print("held: muzzle at", godot(weapon @ Vector((0, PARAMS["front_barrel"][1], 0))))


def main():
    reset_scene()
    make_materials()
    world_arm, world_mesh, world_muzzle = build_world()
    collect("World", [world_arm, world_mesh, world_muzzle])
    animate_world(world_arm)

    view_arm, arms, weapon, muzzle = build_viewmodel()
    collect("Viewmodel", [view_arm, arms, weapon, muzzle])
    animate_viewmodel(view_arm)

    cam_data = bpy.data.cameras.new("Eye")
    cam_data.sensor_fit = "VERTICAL"
    cam_data.angle_y = math.radians(VIEW_FOV)
    cam_data.clip_start = 0.05
    cam = bpy.data.objects.new("Eye", cam_data)
    cam.rotation_euler = (math.radians(90), 0, 0)  # down +Y
    bpy.context.scene.collection.objects.link(cam)
    bpy.context.scene.camera = cam
    scene = bpy.context.scene
    scene.render.resolution_x, scene.render.resolution_y = 1280, 720

    export(WORLD_GLB, [world_arm, world_mesh, world_muzzle])
    export(VIEW_GLB, [view_arm, arms, weapon, muzzle])
    report(view_arm)

    # Leave the .blend playing the reload, on both.
    for arm in (world_arm, view_arm):
        for track in arm.animation_data.nla_tracks:
            track.mute = track.name != "reload"
    scene.frame_start, scene.frame_end = 0, RELOAD["length"]
    scene.frame_set(0)
    bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH, compress=True)
    bpy.ops.file.make_paths_relative()
    bpy.ops.wm.save_mainfile(compress=True)
    print("wrote", os.path.relpath(BLEND_PATH, REPO))


if __name__ == "__main__" or "--background" in sys.argv:
    main()
