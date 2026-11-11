#!/usr/bin/env python3
"""The arithmetic behind docs/LEVEL_EDITOR.md, runnable.

    python3 tools/level_editor_checks/design_checks.py            # every section
    python3 tools/level_editor_checks/design_checks.py conform    # one: codec brush locked conform road bands transfer

numpy and nothing else. Every random draw is seeded, so two runs print the same thing.

This is not the implementation and not its test suite. The rules it exercises are the ones §6 of the
plan specifies for Scripts/Sim/Level/*.cs; when those exist, Tests/*.cs hold them to the same
properties and this file is only how the plan's numbers were arrived at. It models the terrain the
way the plan fixes it: samples 1 m apart, each cell split into the two triangles GodotPhysics3D's
HeightMapShape3D uses -- (x,z) (x+1,z) (x,z+1) and (x+1,z) (x+1,z+1) (x,z+1).
"""
import math
import sys
import zlib

import numpy as np

Q = 256            # a height sample is an int16 in 1/256 m
RIM = 1.5          # the grade-locked brush's untouched rim, in cells: more than sqrt(2)


# ---- shared ---------------------------------------------------------------------------------

def noise_terrain(n, amp, seed, lattices=(4, 8, 16, 32), persistence=0.5):
    """Value-noise fBm on an n x n grid: amp at the coarsest lattice, falling by `persistence`."""
    rng = np.random.default_rng(seed)
    out = np.zeros((n, n))
    for cells in lattices:
        lat = rng.uniform(-1, 1, (cells + 2, cells + 2))
        x = np.linspace(0, cells, n, endpoint=False)
        xi = x.astype(int)
        s = (x - xi) ** 2 * (3 - 2 * (x - xi))
        sx, sz = s[None, :], s[:, None]
        out += amp * ((lat[np.ix_(xi, xi)] * (1 - sx) + lat[np.ix_(xi, xi + 1)] * sx) * (1 - sz)
                      + (lat[np.ix_(xi + 1, xi)] * (1 - sx) + lat[np.ix_(xi + 1, xi + 1)] * sx) * sz)
        amp *= persistence
    return out


def edge_grade(h):
    """Steepest rise over run along a collider edge: +x, +z and the cell's diagonal."""
    return max(np.abs(np.diff(h, axis=0)).max(), np.abs(np.diff(h, axis=1)).max(),
               np.abs(h[1:, :-1] - h[:-1, 1:]).max() / math.sqrt(2))


def face_field(h):
    """Each cell's steeper triangle, as the length of its gradient."""
    a, b, c, d = h[:-1, :-1], h[:-1, 1:], h[1:, :-1], h[1:, 1:]
    return np.maximum(np.hypot(b - a, c - a), np.hypot(d - c, d - b))


def pair_grade(h, window=6):
    """Largest |h(a) - h(b)| / |a - b| over every pair of samples up to `window` cells apart."""
    n, best = h.shape[0], 0.0
    for dz in range(0, window + 1):
        for dx in range(-window, window + 1):
            if dz == 0 and dx <= 0:
                continue
            a = h[dz:, max(dx, 0):n + min(dx, 0)]
            b = h[:n - dz, max(-dx, 0):n - max(dx, 0)]
            best = max(best, float(np.abs(a - b).max()) / math.hypot(dx, dz))
    return best


def degrees(grade):
    return math.degrees(math.atan(grade))


# ---- 1. the height chunk ----------------------------------------------------------------------

def quantize(h):
    return np.clip(np.round(h * Q), -32768, 32767).astype('<i2')


def plane_predict(q):
    """Residual against left + up - upleft, which is exact on any plane; the first row and column against a neighbour."""
    v = q.astype(np.int32)
    p = np.zeros_like(v)
    p[0, 1:] = v[0, :-1]
    p[1:, 0] = v[:-1, 0]
    p[1:, 1:] = v[1:, :-1] + v[:-1, 1:] - v[:-1, :-1]
    return ((v - p) & 0xFFFF).astype('<u2').tobytes()


def height_chunks():
    """Four 241 x 241 terrains and what their height chunk comes to: (name, raw, deflate, predict + deflate)."""
    n = 241
    hills = noise_terrain(n, 12.4, seed=1, lattices=(4, 8, 16, 32, 64, 128)) + 6
    rugged = noise_terrain(n, 14.5, seed=2, lattices=(4, 8, 16, 32, 64, 128, 256, 512), persistence=0.62) + 10
    padded = hills.copy()
    rng = np.random.default_rng(7)
    for _ in range(6):
        cx, cz = rng.integers(30, n - 30, 2)
        padded[cz - 12:cz + 12, cx - 12:cx + 12] = padded[cz, cx]
    padded[n // 2 - 3:n // 2 + 3, :] = 4.0

    rows = []
    for name, h in (("flat (a new map)", np.zeros((n, n))), ("rolling hills", hills),
                    ("the same, six pads and a road", padded), ("rugged", rugged)):
        q = quantize(h)
        raw = q.tobytes()
        rows.append((name, len(raw), len(zlib.compress(raw, 6)), len(zlib.compress(plane_predict(q), 6))))
    return rows


def codec():
    print("1. The height chunk: int16 samples in 1/256 m, as stored")
    print("   zlib is the algorithm behind .NET's ZLibStream, so these are the sizes to expect from it.\n")
    print(f"   {'terrain':34s} {'raw':>9s} {'deflate':>9s} {'predict + deflate':>18s}")
    for name, raw, deflated, predicted in height_chunks():
        print(f"   {name:34s} {raw:9,d} {deflated:9,d} {predicted:18,d}")

    # every representable height is exact in a float32, so every peer builds the same collider
    q = np.arange(-32768, 32768, dtype=np.int32)
    exact = bool(((q / Q).astype(np.float32).astype(np.float64) * Q == q).all())
    print(f"\n   every int16 / 256 is exact as a float32: {exact}; one step is {1000 / Q:.5f} mm")


# ---- 2. the sculpt brush ----------------------------------------------------------------------

def brush():
    print("2. The sculpt brush: a dab adds  rate * dt * w(r / R),  w = 1 inside core * R, a smoothstep to 0 at R")
    print("   claim: once the centre has risen H, the steepest grade is 1.5 * H / ((1 - core) * R)\n")
    n = 129
    zz, xx = np.mgrid[0:n, 0:n]
    r = np.hypot(zz - n // 2, xx - n // 2)
    print(f"   {'radius':>7s} {'core':>5s} {'H':>4s} {'on the grid':>12s} {'formula':>9s}")
    for R, core, H in ((4, 0.0, 2), (8, 0.0, 2), (16, 0.0, 2), (32, 0.0, 2), (16, 0.5, 2), (16, 0.8, 2),
                       (16, 0.0, 6), (16, 0.5, 6), (8, 0.8, 6)):
        t = np.clip((r - core * R) / ((1 - core) * R), 0, 1)
        h = H * (1 - t * t * (3 - 2 * t))
        print(f"   {R:5d} m {core:5.1f} {H:2d} m {degrees(edge_grade(h)):10.2f} deg {degrees(1.5 * H / ((1 - core) * R)):7.2f} deg")


# ---- 3. the grade-locked brush ----------------------------------------------------------------

def locked_dab(h, cx, cz, R, k, step, lower, rho):
    """One dab. The cone's tip is rounded over rho metres; the apex is held so the cone is at or past the ground on the rim."""
    n = h.shape[0]
    zz, xx = np.mgrid[0:n, 0:n]
    r = np.hypot(zz - cz, xx - cx)
    disc = r <= R
    rim = disc & (r > R - RIM)
    phi = np.sqrt(r * r + rho * rho) - rho
    centre = h[int(round(cz)), int(round(cx))]
    if lower:
        apex = max(centre - step, (h[rim] - k * phi[rim]).max())
        return np.where(disc, np.minimum(h, apex + k * phi), h)
    apex = min(centre + step, (h[rim] + k * phi[rim]).min())
    return np.where(disc, np.maximum(h, apex - k * phi), h)


def locked():
    print("3. The grade-locked brush:  h' = max(h, apex - k * phi(r))  inside the disc, phi(r) = sqrt(r^2 + rho^2) - rho")
    print("   claim: no collider edge ends steeper than max(k, what it was); nothing on or outside the rim moves\n")
    n = 129
    rng = np.random.default_rng(5)
    worst_edge, worst_face, moved, tallest = -1e9, 0.0, 0.0, 0.0
    zz, xx = np.mgrid[0:n, 0:n]
    for stroke in range(200):
        base = noise_terrain(n, rng.uniform(0, 4), seed=1000 + stroke)
        k = math.tan(math.radians(rng.choice([5, 10, 20, 30, 45, 60])))
        R = int(rng.choice([4, 8, 16, 24]))
        lower = bool(rng.integers(0, 2))
        h = base.copy()
        cx, cz = rng.uniform(40, 88, 2)
        heading = rng.uniform(0, 2 * math.pi)
        for _ in range(80):
            before = h
            h = locked_dab(h, cx, cz, R, k, 0.25, lower, rho=2.0)
            moved = max(moved, float(np.abs(h - before)[np.hypot(zz - cz, xx - cx) > R - RIM].max()))
            heading += rng.normal(0, 0.3)
            cx = float(np.clip(cx + 0.5 * math.cos(heading), 30, 98))
            cz = float(np.clip(cz + 0.5 * math.sin(heading), 30, 98))
        worst_edge = max(worst_edge, edge_grade(h) - max(k, edge_grade(base)))
        worst_face = max(worst_face, float(face_field(h).max()) / max(k, float(face_field(base).max())))
        tallest = max(tallest, float(np.abs(h - base).max()))
    print("   200 strokes of 80 dabs over rough ground, raising and lowering, 5-60 deg, radii 4-24 m, rho 2 m:")
    print(f"     steepest edge after, less max(k, steepest edge before), at worst  {worst_edge:+.1e}")
    print(f"     largest change on or outside the rim                             {moved:.1e} m")
    print(f"     largest change a stroke made                                     {tallest:.2f} m")
    print(f"     steepest face after / max(k, steepest face before), at worst     {worst_face:.3f}  (the bound is sqrt(2))")

    print("\n   one dab held on level ground until it stops growing; the steepest triangle face it leaves:")
    print(f"   {'setting':>8s} {'radius':>7s} {'height':>8s} {'sharp tip':>10s} {'rho 1 m':>9s} {'rho 2 m':>9s} {'rho 3 m':>9s}")
    for deg, R in ((10, 8), (20, 8), (30, 16), (40, 16), (45, 16), (60, 8)):
        k = math.tan(math.radians(deg))
        cells, height = [], 0.0
        for rho in (0.0, 1.0, 2.0, 3.0):
            h = np.zeros((n, n))
            for _ in range(6000):
                new = locked_dab(h, n // 2, n // 2, R, k, 0.25, False, rho)
                if np.array_equal(new, h):
                    break
                h = new
            cells.append(f"{degrees(float(face_field(h).max())):6.2f} deg")
            if rho == 2.0:
                height = float(h.max())
        print(f"   {deg:4d} deg {R:5d} m {height:6.2f} m " + " ".join(f"{c:>10s}" for c in cells))


# ---- 4. the conform operator: pads, roads, bridgeheads ------------------------------------------

def rect_distance(n, cx, cz, half_w, half_d, yaw):
    """Distance from every sample to a rectangle, flat. Zero on or inside it."""
    zz, xx = np.mgrid[0:n, 0:n]
    dx, dz = xx - cx, zz - cz
    lx = dx * math.cos(yaw) - dz * math.sin(yaw)
    lz = dx * math.sin(yaw) + dz * math.cos(yaw)
    return np.hypot(np.maximum(np.abs(lx) - half_w, 0), np.maximum(np.abs(lz) - half_d, 0))


def conform_cells(h, cells, targets, k):
    """clamp(h, L, U) for anchor samples with their own targets: the general form, as a road uses it."""
    n = h.shape[0]
    zz, xx = np.mgrid[0:n, 0:n]
    low = np.full(h.shape, -np.inf)
    high = np.full(h.shape, np.inf)
    for (az, ax), s in zip(cells, targets):
        d = np.hypot(zz - az, xx - ax)
        low = np.maximum(low, s - k * d)
        high = np.minimum(high, s + k * d)
    return np.clip(h, low, high), low, high


def conform():
    print("4. The conform operator:  h' = clamp(h, L, U),  L = max_q(S(q) - k d(p, q)),  U = min_q(S(q) + k d(p, q))")
    print("   for anchors q with targets S that are themselves no steeper than k\n")
    n = 96
    rng = np.random.default_rng(11)
    worst = dict(anchor=0.0, untouched=0.0, pair=-1e9, twice=0.0, reach=-1e9)
    for trial in range(40):
        h = noise_terrain(n, rng.uniform(2, 8), seed=100 + trial, lattices=(3, 6, 12, 24))
        k = math.tan(math.radians(rng.choice([20, 30, 35, 45])))
        if trial % 2 == 0:  # a pad: every sample within 1.5 m of a rotated rectangle, levelled to the mean under it
            d = rect_distance(n, rng.uniform(30, 66), rng.uniform(30, 66), rng.uniform(1, 15), rng.uniform(1, 15), rng.uniform(0, math.pi))
            cells = np.argwhere(d <= 1.5)
            targets = np.full(len(cells), h[d <= 1.5].mean())
        else:               # a road: a 7 m ribbon along a sine, climbing at 8 %
            xs = np.arange(10, 86)
            zc = 48 + 14 * np.sin(xs / 12.0)
            along = np.concatenate([[0], np.cumsum(np.hypot(np.diff(xs), np.diff(zc)))])
            profile = h[int(zc[0]), xs[0]] + 0.08 * along
            zz, xx = np.mgrid[0:n, 0:n]
            d2 = (zz[..., None] - zc) ** 2 + (xx[..., None] - xs) ** 2
            cells = np.argwhere(np.sqrt(d2.min(axis=2)) <= 3.5)
            targets = profile[d2.argmin(axis=2)[cells[:, 0], cells[:, 1]]]
        out, low, high = conform_cells(h, cells, targets, k)
        assert (low <= high + 1e-9).all()
        worst['anchor'] = max(worst['anchor'], float(np.abs(out[cells[:, 0], cells[:, 1]] - targets).max()))
        inside = (h >= low) & (h <= high)
        worst['untouched'] = max(worst['untouched'], float(np.abs(out - h)[inside].max()))
        worst['pair'] = max(worst['pair'], pair_grade(out) - max(k, pair_grade(h)))
        worst['twice'] = max(worst['twice'], float(np.abs(conform_cells(out, cells, targets, k)[0] - out).max()))
        changed = np.argwhere(np.abs(out - h) > 1e-12)
        if len(changed):
            nearest = np.sqrt(((changed[:, None, :] - cells[None, :, :]) ** 2).sum(axis=2)).min(axis=1)
            bound = np.abs(h[changed[:, 0], changed[:, 1]][None, :] - targets[:, None]).max() / k
            worst['reach'] = max(worst['reach'], float(nearest.max() - bound))
    print("   40 random terrains, pads and roads in turn:")
    print(f"     h' off its target on an anchor, at worst                              {worst['anchor']:.1e} m")
    print(f"     change where the ground was already between L and U, at worst         {worst['untouched']:.1e} m")
    print(f"     steepest sample pair after, less max(k, steepest before), at worst    {worst['pair']:+.1e}")
    print(f"     change made by applying it a second time, at worst                    {worst['twice']:.1e} m")
    print(f"     reach past max|h - S| / k from the anchors, at worst                  {worst['reach']:+.1e} cells  (negative: inside)")

    print("\n   pads with the skirt measured from the footprint itself, d = max(0, distance to the rectangle - 1.5 m):")
    n = 129
    for lo, hi, label in ((1, 8, "rough ground"), (0.2, 1.5, "gentle ground")):
        rng = np.random.default_rng(33)
        ratios, over, changed_cells, off_level = [], 0, 0, 0.0
        for trial in range(300):
            base = noise_terrain(n, rng.uniform(lo, hi), seed=5000 + trial)
            k = math.tan(math.radians(rng.choice([20, 30, 35, 45])))
            dr = rect_distance(n, *rng.uniform(40, 88, 2), rng.uniform(0.4, 15), rng.uniform(0.4, 15), rng.uniform(0, math.pi))
            under = dr <= 0 if (dr <= 0).any() else dr <= 0.75
            level = round(float(base[under].mean()) * Q) / Q
            d = np.maximum(dr - 1.5, 0)
            out = np.clip(base, level - k * d, level + k * d)
            limit = max(k, float(face_field(base).max()))
            faces = face_field(out)
            ratios.append(float(faces.max()) / limit)
            moved = np.abs(out - base)[:-1, :-1] > 0
            over += int(((faces > limit * 1.0001) & moved).sum())
            changed_cells += int(moved.sum())
            off_level = max(off_level, float(np.abs(out[dr <= 1.5] - level).max()))
        print(f"     300 pads on {label:14s}: steepest face / max(k, before) at worst {max(ratios):.3f}; "
              f"cells with a face over it {over:,d} of {changed_cells:,d} changed ({100 * over / changed_cells:.2f} %); "
              f"pad off level by {off_level:.0e} m")

    print("\n   the same pads with the skirt measured from the nearest levelled sample instead:")
    rng = np.random.default_rng(33)
    ratios = []
    for trial in range(120):
        base = noise_terrain(n, rng.uniform(1, 8), seed=5000 + trial)
        k = math.tan(math.radians(rng.choice([20, 30, 35, 45])))
        dr = rect_distance(n, *rng.uniform(40, 88, 2), rng.uniform(0.4, 15), rng.uniform(0.4, 15), rng.uniform(0, math.pi))
        cells = np.argwhere(dr <= 1.5)
        level = round(float(base[dr <= 1.5].mean()) * Q) / Q
        out, _, _ = conform_cells(base, cells, np.full(len(cells), level), k)
        ratios.append(float(face_field(out).max()) / max(k, float(face_field(base).max())))
    print(f"     120 pads on rough ground  : steepest face / max(k, before) at worst {max(ratios):.3f}; "
          f"pads that leave a face over it {sum(r > 1.0001 for r in ratios)} of 120")

    print("\n   the steepest face a skirt can leave, atan(sqrt(2) * tan(skirt)):")
    print("     " + "   ".join(f"{deg:g} deg -> {degrees(math.sqrt(2) * math.tan(math.radians(deg))):.2f}" for deg in (25, 30, 35, 40, 45)))


# ---- 5. a road's profile --------------------------------------------------------------------------

def road():
    print("5. A road's profile:  z = (m + M) / 2,  m(s) = min_t(f(t) + g|s - t|),  M(s) = max_t(f(t) - g|s - t|)")
    print("   claim: z is no steeper than g, and its deepest cut or fill is E / 2,")
    print("          E = max over s, t of |f(s) - f(t)| - g|s - t|, which no profile of grade g can beat\n")
    rng = np.random.default_rng(3)
    worst_grade, worst_gap, rows = -1e9, 0.0, []
    for trial in range(200):
        n = int(rng.integers(40, 400))
        s = np.arange(n, dtype=float)
        f = np.cumsum(rng.normal(0, rng.uniform(0.05, 0.6), n)) + rng.uniform(0, 6) * np.sin(s / rng.uniform(8, 40))
        g = float(rng.choice([0.05, 0.08, 0.10, 0.15, 0.25]))
        span = np.abs(s[:, None] - s[None, :])
        z = 0.5 * ((f[None, :] + g * span).min(axis=1) + (f[None, :] - g * span).max(axis=1))
        excess = max(float((np.abs(f[:, None] - f[None, :]) - g * span).max()), 0.0)
        worst_grade = max(worst_grade, float(np.abs(np.diff(z)).max()) - g)
        worst_gap = max(worst_gap, abs(float(np.abs(z - f).max()) - excess / 2))
        if trial < 4:
            rows.append((n, g, float(np.abs(np.diff(f)).max()), float(np.abs(z - f).max()), excess / 2))
    print("   200 random centrelines:")
    print(f"     steepest grade of z, less g, at worst        {worst_grade:+.1e}")
    print(f"     | deepest cut or fill - E / 2 |, at worst    {worst_gap:.1e} m")
    for n, g, raw, cut, bound in rows:
        print(f"     {n:3d} m of ground at up to {100 * raw:5.1f} %: a {100 * g:4.1f} % road cuts or fills"
              f" {cut:.2f} m at most (E / 2 = {bound:.2f} m)")

    n = 200
    s = np.arange(n, dtype=float)
    f = 6 * np.sin(s / 25) + np.cumsum(rng.normal(0, 0.2, n))
    g = 0.10
    span = np.abs(s[:, None] - s[None, :])
    z = 0.5 * ((f[None, :] + g * span).min(axis=1) + (f[None, :] - g * span).max(axis=1))
    deck = f[0] + 4.0
    pinned = np.clip(z, deck - g * s, deck + g * s)
    rejoins = int(np.argmax(np.abs(pinned - z) < 1e-12))
    print(f"\n   an end pinned 4 m above the ground, z' = clamp(z, z0 - g s, z0 + g s): off z0 by {pinned[0] - deck:+.0e} m, "
          f"steepest grade {100 * np.abs(np.diff(pinned)).max():.1f} %, back on the free profile after {rejoins} m")

    # Round a bend the ribbon's targets are steeper than the profile: two points on the inside edge,
    # an angle theta apart, differ in target by g * rho * theta and are 2 * r_in * sin(theta / 2) apart.
    # With the corner rounded to the road's width, rho = w and r_in = w / 2.
    skirt = math.tan(math.radians(30))
    print("\n   the bend at which the targets across the inside of the tightest corner pass the 30 deg skirt:")
    for g in (0.10, 0.15, 0.20):
        theta = next((t for t in np.arange(0.001, 2 * math.pi, 0.001) if g * t / math.sin(t / 2) > skirt), None)
        on_straight = f"{100 * g:.0f} % on a straight, {200 * g:.0f} % on the inside edge"
        print(f"     {on_straight}: " + (f"{math.degrees(theta):.0f} deg" if theta else "never"))


# ---- 6. the texture bands -------------------------------------------------------------------------

BAND_NAMES = ("water", "sand", "mud", "grass", "stone")
BAND_EDGES = (1.5, 4.0, 22.0)     # metres of u at which sand gives way to mud, mud to grass, grass to stone
SLOPE_FREE = 15.0                 # degrees of slope that cost nothing
SLOPE_GAIN = 0.6                  # metres of u per degree past that


def band(above_water, slope_degrees):
    if above_water < 0:
        return 0
    u = above_water + SLOPE_GAIN * max(0.0, slope_degrees - SLOPE_FREE)
    return 1 + sum(u >= edge for edge in BAND_EDGES)


def bands():
    print("6. The texture bands:  u = (y - water) + 0.6 * max(0, slope - 15 deg);  under the water is water;")
    print("   then sand below u = 1.5, mud below 4, grass below 22, stone above\n")
    slopes = (0, 10, 15, 20, 25, 30, 35, 40, 45, 50, 60)
    print("   above the water \\ slope " + "".join(f"{d:>6d}" for d in slopes))
    for e in (-2, 0.5, 1.0, 2.0, 3.0, 5.0, 8.0, 12.0, 16.0, 20.0, 25.0):
        print(f"   {e:12.1f} m           " + "".join(f"{BAND_NAMES[band(e, d)]:>6s}" for d in slopes))
    grid = np.array([[band(e, d) for d in np.linspace(0, 89, 179)] for e in np.linspace(-5, 60, 261)])
    monotone = bool((np.diff(grid, axis=0) >= 0).all() and (np.diff(grid, axis=1) >= 0).all())
    print(f"\n   higher or steeper never gives an earlier band, over a 261 x 179 sweep: {monotone}")
    at_waterline = SLOPE_FREE + BAND_EDGES[2] / SLOPE_GAIN
    four_up = SLOPE_FREE + (BAND_EDGES[2] - 4) / SLOPE_GAIN
    print(f"   level ground at the waterline turns to stone at {at_waterline:.1f} deg; ground 4 m up at {four_up:.1f} deg")


# ---- 7. sending one ---------------------------------------------------------------------------------

def transfer():
    print("7. Sending a map: 1,024-byte chunks, one a tick at 60 Hz = 61,440 B/s to a peer\n")
    samples = 241 * 241
    print(f"   {samples:,d} samples, {240 * 240:,d} cells, {240 * 240 * 2:,d} triangles, {(240 // 16) ** 2} mesh chunks of 16 x 16 cells")
    sizes = [predicted for _, _, _, predicted in height_chunks()]
    for size, label in ((samples * 2, "the height chunk, stored raw"), (max(sizes), "the largest in section 1"), (min(sizes), "a flat new map")):
        print(f"   {label:30s} {size:8,d} B  {math.ceil(size / 1024):4d} chunks  {size / 61_440:5.2f} s")
    print(f"   the navigation bake reads {240 * 240 * 6:,d} vertices from the collider; its grid stays 1,200 x 1,200 columns at 0.2 m")

    # The largest a 1 m map's file can be, chunk by chunk (docs/LEVEL_EDITOR.md, section 6.8).
    raw = samples * 2
    deflate_bound = raw + (raw >> 12) + (raw >> 14) + (raw >> 25) + 13       # zlib's compressBound
    noise = np.random.default_rng(9).integers(0, 256, raw, dtype=np.uint8).tobytes()
    assert len(zlib.compress(noise, 6)) <= deflate_bound
    header = 8 + 1 + 1
    chunk_headers = 4 * (4 + 4)
    meta = 16 + 4 + 8 + 8 + 16 + 32 + 2 + (1 + 48) + (1 + 32) + (1 + 255)
    terrain = 2 + 2 + 2 + 2 + 6 * 2 + deflate_bound
    objects = 2 + 512 * (2 + 6 + 2 + 1 + 1 + 8 * 5)
    roads = 1 + 32 * (4 + 64 * 4)
    largest = header + chunk_headers + meta + terrain + objects + roads
    print(f"\n   the largest file a 1 m map can be: header {header} + chunk headers {chunk_headers} + META {meta} + TERR {terrain:,d}"
          f" + OBJS {objects:,d} + ROAD {roads:,d}")
    print(f"     = {largest:,d} B, {math.ceil(largest / 1024)} chunks, {largest / 61_440:.2f} s;"
          f" a file at the 1 MiB limit, {1_048_576 / 61_440:.1f} s")


SECTIONS = dict(codec=codec, brush=brush, locked=locked, conform=conform, road=road, bands=bands, transfer=transfer)

if __name__ == "__main__":
    wanted = sys.argv[1:] or list(SECTIONS)
    unknown = [name for name in wanted if name not in SECTIONS]
    if unknown:
        sys.exit(f"unknown section {unknown[0]!r}; one of: {' '.join(SECTIONS)}")
    for index, name in enumerate(wanted):
        if index:
            print()
        SECTIONS[name]()
