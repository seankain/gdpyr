#!/usr/bin/env python3
"""Why a long ray can pass through a HeightMapShape3D (docs/LEVEL_EDITOR.md §3, LE-D5).

    python3 tools/level_editor_checks/heightmap_ray_walk.py

A port, in float32, of GodotPhysics3D's walk over a heightmap's cells
(GodotHeightMapShape3D::_intersect_grid_segment, modules/godot_physics_3d/godot_shape_3d.cpp:1793
at tag 4.6-stable), run as the engine runs it for a ray 16 m or longer in plan: once over the
shape's 16-cell chunks, and inside each chunk the ray passes the height of, once over that chunk's
cells with the piece of the ray inside it (:1757, :1975). It prints which cells each walk visits.

The rays are ones LE0's check cast at a flat 241 x 241 heightmap at y = 0.5
(scripts/level-editor-checks.sh ground): three the engine let through the ground, and one it met.
The port lets the same three through, and shows where: the piece of the ray inside the chunk starts
one float short of a lane between cells -- it was cut there -- so `floor` puts the walk in the cell
before the lane, and the start is then taken for one exactly on the lane (:1861, "Workaround cases
where the ray starts at an integer position"), which puts off the walk's first step by a whole
cell. For the rest of that chunk the walk tests the cell behind the one the ray is in. A ray that
meets the ground in a cell the walk never gets to meets nothing: the chunks after it are tested
from under the ground, where a face does not count (:1804).

numpy only. This is evidence for a decision, not a test of anything this repository builds.
"""
import numpy as np

f = np.float32
EPSILON = f(1e-5)            # CMP_EPSILON, which Math::is_zero_approx compares against
SAMPLES = 241
CHUNK = 16                   # GodotHeightMapShape3D::BOUNDS_CHUNK_SIZE (godot_shape_3d.h:400)
CHUNKS = SAMPLES // CHUNK + 1 + 1   # bounds_grid_width, and the 1 intersect_segment adds to it
ORIGIN = np.array([(SAMPLES - 1) / 2, 0, (SAMPLES - 1) / 2], dtype=f)
GROUND = 0.5


def length(v):
    return f(np.sqrt(f(v[0] * v[0]) + f(v[1] * v[1]) + f(v[2] * v[2])))


def walk(begin, end, width, depth, offset, process):
    """_intersect_grid_segment: `process` is called for each cell on the ray's plan, in order."""
    delta = (end - begin).astype(f)
    whole = length(delta)
    if whole < EPSILON:
        return False

    local = (begin + offset).astype(f)
    direction = (delta / whole).astype(f)
    flat = np.array([delta[0], delta[2]], dtype=f)
    flat_length = f(np.sqrt(f(flat[0] * flat[0]) + f(flat[1] * flat[1])))
    flat = (flat / flat_length).astype(f) if flat_length >= EPSILON else np.zeros(2, dtype=f)

    x_step = 1 if flat[0] > EPSILON else (-1 if flat[0] < -EPSILON else 0)
    z_step = 1 if flat[1] > EPSILON else (-1 if flat[1] < -EPSILON else 0)
    infinite = f(1e20)
    delta_x = f(1) / abs(flat[0]) if x_step else infinite
    delta_z = f(1) / abs(flat[1]) if z_step else infinite

    def first(step, at, per_cell):
        if not step:
            return infinite
        return f(((f(np.ceil(at)) - at) if step == 1 else (at - f(np.floor(at)))) * per_cell)

    cross_x, cross_z = first(x_step, local[0], delta_x), first(z_step, local[2], delta_z)
    x, z = int(np.floor(local[0])), int(np.floor(local[2]))

    # "Workaround cases where the ray starts at an integer position."
    notes = []
    if abs(cross_x) < EPSILON:
        cross_x = f(cross_x + delta_x)
        if x_step == -1:
            x -= 1
        notes.append(f"its x, {local[0]!s}, is taken for a whole number: the walk starts in column {x}")
    if abs(cross_z) < EPSILON:
        cross_z = f(cross_z + delta_z)
        if z_step == -1:
            z -= 1
        notes.append(f"its z, {local[2]!s}, is taken for a whole number: the walk starts in row {z}")

    x_start, z_start = max(min(x, width - 2), 0), max(min(z, depth - 2), 0)
    cross_x = f(cross_x + delta_x * x_step * (x_start - x))
    cross_z = f(cross_z + delta_z * z_step * (z_start - z))
    x, z = x_start, z_start

    distance = f(0)
    while True:
        previous, cell = distance, (x, z)
        if cross_x < cross_z:
            x += x_step
            distance, cross_x = cross_x, f(cross_x + delta_x)
        else:
            z += z_step
            distance, cross_z = cross_z, f(cross_z + delta_z)

        last = distance > flat_length
        if last:
            distance = flat_length
        if process(cell, previous, distance, whole, flat_length, begin, direction, notes):
            return True
        if last or x < 0 or z < 0 or x >= width - 1 or z >= depth - 1:
            return False


def cast(begin, end):
    begin, end = np.array(begin, dtype=f), np.array(end, dtype=f)
    t = (begin[1] - GROUND) / (begin[1] - end[1])
    meets = begin + (end - begin) * t + ORIGIN
    true_cell = (int(np.floor(meets[0])), int(np.floor(meets[2])))
    print(f"from ({begin[0]:.2f}, {begin[1]:.2f}, {begin[2]:.2f}) to ({end[0]:.2f}, {end[1]:.2f}, {end[2]:.2f}):"
          f" the ground is in cell {true_cell}, chunk ({true_cell[0] // CHUNK}, {true_cell[1] // CHUNK})")
    met = []

    def chunk(index, previous, distance, whole, flat_length, start, direction, _):
        # _heightmap_chunk_cull_segment: the piece of the ray inside this chunk, in metres again.
        scale = whole / flat_length
        enter = ((start + direction * f(previous * scale)) * f(CHUNK)).astype(f)
        leave = ((start + direction * f(distance * scale)) * f(CHUNK)).astype(f)
        if (enter[1] > GROUND and leave[1] > GROUND) or (enter[1] < GROUND and leave[1] < GROUND):
            return False

        cells, said = [], []

        def cell(index, *rest):
            cells.append(index)
            said[:] = rest[-1]
            return False

        walk(enter, leave, SAMPLES, SAMPLES, ORIGIN, cell)
        found = true_cell in cells
        local = enter + ORIGIN
        print(f"  chunk {index}: the ray's piece in it starts at ({local[0]!s}, {local[2]!s})")
        for note in said:
            print(f"    {note}")
        print(f"    {len(cells)} cells walked, {cells[0]} to {cells[-1]}: {true_cell} is {'one of them' if found else 'NOT one of them'}")
        met.append(found)
        return found

    walk((begin / f(CHUNK)).astype(f), (end / f(CHUNK)).astype(f), CHUNKS, CHUNKS, (ORIGIN / f(CHUNK)).astype(f), chunk)
    print(f"  the ray {'meets the ground' if any(met) else 'meets NOTHING'}\n")
    return any(met)


# What `scripts/level-editor-checks.sh ground` printed for the flat heightmap
# (docs/baselines/le0-67fec82-checks.txt): three of the 36 rays in 10,000 that met no terrain, with
# nothing else on them, and the first that met it.
PASSED_THROUGH = [
    ((-86.47273, 21.354223, -26.022736), (93.173416, -13.398487, -11.060335)),
    ((85.62552, 6.954612, -103.73263), (-55.3127, -7.9156103, 37.215874)),
    ((-99.63232, 20.00727, -58.283825), (-40.19373, -5.1430736, 56.678555)),
]
MET = [((76.9828, 15.567844, 106.99202), (54.506935, -6.3909655, 91.77502))]

if __name__ == "__main__":
    through = [not cast(a, b) for a, b in PASSED_THROUGH]
    met = [cast(a, b) for a, b in MET]
    print(f"the port lets {sum(through)} of the engine's {len(through)} through, and meets {sum(met)} of {len(met)} it met")
    raise SystemExit(0 if all(through) and all(met) else 1)
