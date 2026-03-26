#! python 3
# NODE_INPUTS: site_def:str, level_planes:list[Plane], level_elevations:list[float], noise_seed:int | "Random seed" min=1 max=9999 default=42, num_masses:int | "Max mass tracks" min=1 max=6 default=3, masses_per_level:list[int] | "Active masses per level" min=1 max=6 default=1, num_holes:int | "Max hole tracks" min=0 max=6 default=2, holes_per_level:list[int] | "Active holes per level" min=0 max=6 default=0, mass_spread:float | "Mass separation mm" min=0 max=30000 step=500 decimals=0 default=8000, hole_spread:float | "Hole separation mm" min=0 max=20000 step=500 decimals=0 default=5000, envelope_inset:list[float] | "Boundary inset mm" min=0 max=10000 step=100 decimals=0 default=2000, mass_scale:list[float] | "Per-level mass scale" min=0.1 max=2.0 step=0.05 decimals=2 default=0.9, noise_amplitude:list[float] | "Curve noise mm" min=0 max=5000 step=50 decimals=0 default=800, hole_radius:list[float] | "Hole radius mm" min=50 max=8000 step=50 decimals=0 default=3000, mass_cx:list[float] | "Mass centre X offset" min=-20000 max=20000 step=200 decimals=0 default=0, mass_cy:list[float] | "Mass centre Y offset" min=-20000 max=20000 step=200 decimals=0 default=0, hole_cx:list[float] | "Hole centre X offset" min=-20000 max=20000 step=200 decimals=0 default=0, hole_cy:list[float] | "Hole centre Y offset" min=-20000 max=20000 step=200 decimals=0 default=0, cap_height:float | "Cap dome height mm" min=0 max=10000 step=100 decimals=0 default=3000, cap_bulge:float | "Cap dome shape" min=0 max=1 step=0.05 decimals=2 default=0.5, cap_resolution:int | "Cap ring count" min=1 max=8 default=4
# NODE_OUTPUTS: envelope_surface, void_surfaces, floor_plates, envelope_curves, hole_curves, level_elevations, site_def, log
#
# envelope_v2 — multi-mass / multi-hole envelope with per-level track activation,
# per-level centrepoint offsets, dome/taper caps, and boolean-merged output.

import math
import random
import json
import Rhino.Geometry as rg


# ---------------------------------------------------------------------------
# Utility helpers
# ---------------------------------------------------------------------------

def _f(v, d):
    try:
        return float(v)
    except Exception:
        return float(d)


def _i(v, d):
    try:
        return int(round(float(v)))
    except Exception:
        return int(d)


def _clamp(v, lo, hi):
    return max(lo, min(hi, v))


def _pad_floats(raw, fallback, count):
    vals = []
    if raw is not None:
        try:
            for rv in raw:
                vals.append(_f(rv, fallback))
        except TypeError:
            vals.append(_f(raw, fallback))
    if not vals:
        return [float(fallback)] * count
    while len(vals) < count:
        vals.append(vals[-1])
    return vals[:count]


def _pad_ints(raw, fallback, count):
    vals = []
    if raw is not None:
        try:
            for rv in raw:
                try:
                    vals.append(int(round(float(rv))))
                except Exception:
                    vals.append(fallback)
        except TypeError:
            try:
                vals.append(int(round(float(raw))))
            except Exception:
                vals.append(fallback)
    if not vals:
        return [fallback] * count
    while len(vals) < count:
        vals.append(vals[-1])
    return vals[:count]


def _world_pt(org, xax, yax, lx, ly, z):
    return rg.Point3d(
        org.X + xax.X * lx + yax.X * ly,
        org.Y + xax.Y * lx + yax.Y * ly,
        z,
    )


def _smooth_closed(points):
    if len(points) < 4:
        return None
    pts = list(points) + [points[0]]
    return rg.Curve.CreateInterpolatedCurve(pts, 3, rg.CurveKnotStyle.ChordPeriodic)


def _curve_centroid_xy(curve, fallback):
    amp = rg.AreaMassProperties.Compute(curve)
    if amp:
        c = amp.Centroid
        return rg.Point3d(c.X, c.Y, fallback.Z)
    return rg.Point3d(fallback)


def _angular_positions(n, radius, base_angle=0.0):
    if n <= 0:
        return []
    if n == 1:
        return [(0.0, 0.0)]
    out = []
    for k in range(n):
        a = base_angle + (2.0 * math.pi * k) / float(n)
        out.append((math.cos(a) * radius, math.sin(a) * radius))
    return out


def _make_blob_curve(org, xax, yax, cx, cy, rx, ry, z, n_pts, rng, noise):
    pts = []
    for si in range(n_pts):
        t = (2.0 * math.pi * float(si)) / float(n_pts)
        lx = cx + rx * math.cos(t)
        ly = cy + ry * math.sin(t)
        wobble = (rng.random() * 2.0 - 1.0) * noise
        lx += math.cos(t) * wobble
        ly += math.sin(t) * wobble
        pts.append(_world_pt(org, xax, yax, lx, ly, z))
    return _smooth_closed(pts)


def _safe_boolean_union(breps):
    valid = [b for b in breps if b is not None]
    if len(valid) == 0:
        return []
    if len(valid) == 1:
        return valid
    result = rg.Brep.CreateBooleanUnion(valid, 0.1)
    if result and len(result) > 0:
        return list(result)
    return valid


def _safe_boolean_diff(base_breps, cutter_breps):
    bases = [b for b in base_breps if b is not None]
    cutters = [b for b in cutter_breps if b is not None]
    if not bases:
        return []
    if not cutters:
        return bases
    result = rg.Brep.CreateBooleanDifference(bases, cutters, 0.1)
    if result and len(result) > 0:
        return list(result)
    return bases


def _cap_brep(brep, tol=0.1):
    if brep is None:
        return brep
    capped = brep.CapPlanarHoles(tol)
    if capped is not None:
        return capped
    return brep


def _make_cap_curves(boundary_curve, centre, direction, height, bulge, resolution):
    """Generate dome/taper cap curves from a boundary curve.

    direction: +1 = upward cap, -1 = downward cap (underside).
    bulge: 0 = linear taper, 1 = hemisphere-like dome.
    Returns list of curves ordered from boundary outward (boundary NOT included).
    """
    if height < 1.0 or resolution < 1:
        return []
    curves = []
    exponent = 1.0 / max(0.1, 1.0 - bulge * 0.9)
    for k in range(resolution):
        t = float(k + 1) / float(resolution + 1)
        scale = max(0.02, (1.0 - t) ** exponent)
        dz = height * t * direction
        ring = boundary_curve.DuplicateCurve()
        ring.Transform(rg.Transform.Scale(centre, scale))
        ring.Transform(rg.Transform.Translation(0, 0, dz))
        curves.append(ring)
    # tiny closing ring at full cap height
    tip = boundary_curve.DuplicateCurve()
    tip.Transform(rg.Transform.Scale(centre, 0.02))
    tip.Transform(rg.Transform.Translation(0, 0, height * direction))
    curves.append(tip)
    return curves


# ---------------------------------------------------------------------------
# NameError guards for params that may not exist during parameter rebuild
# ---------------------------------------------------------------------------

try: num_masses
except NameError: num_masses = None
try: masses_per_level
except NameError: masses_per_level = None
try: num_holes
except NameError: num_holes = None
try: holes_per_level
except NameError: holes_per_level = None
try: mass_spread
except NameError: mass_spread = None
try: hole_spread
except NameError: hole_spread = None
try: envelope_inset
except NameError: envelope_inset = None
try: mass_scale
except NameError: mass_scale = None
try: noise_amplitude
except NameError: noise_amplitude = None
try: hole_radius
except NameError: hole_radius = None
try: mass_cx
except NameError: mass_cx = None
try: mass_cy
except NameError: mass_cy = None
try: hole_cx
except NameError: hole_cx = None
try: hole_cy
except NameError: hole_cy = None
try: cap_height
except NameError: cap_height = None
try: cap_bulge
except NameError: cap_bulge = None
try: cap_resolution
except NameError: cap_resolution = None

# ---------------------------------------------------------------------------
# Output defaults
# ---------------------------------------------------------------------------

envelope_surface = None
void_surfaces = []
floor_plates = []
envelope_curves = []
hole_curves = []
log = ""

# ---------------------------------------------------------------------------
# Main logic
# ---------------------------------------------------------------------------

if site_def is None or not str(site_def).strip():
    site_def = "{}"
    log = "Error: site_def is empty."
else:
    try:
        sd = json.loads(str(site_def))
    except Exception:
        sd = None
        log = "Error: site_def is not valid JSON."

    if sd is not None:
        gd = sd.get("grid_def", {})
        origin = gd.get("origin", [0.0, 0.0, 0.0])
        x_axis = gd.get("x_axis", [1.0, 0.0, 0.0])
        y_axis = gd.get("y_axis", [0.0, 1.0, 0.0])
        site_extents = sd.get("site_extents", [20000, 20000, 20000, 20000, 0, 40000])

        org = rg.Point3d(_f(origin[0], 0), _f(origin[1], 0), _f(origin[2], 0))
        xax = rg.Vector3d(_f(x_axis[0], 1), _f(x_axis[1], 0), 0)
        yax = rg.Vector3d(_f(y_axis[0], 0), _f(y_axis[1], 1), 0)
        if xax.Length < 1e-9:
            xax = rg.Vector3d(1, 0, 0)
        if yax.Length < 1e-9:
            yax = rg.Vector3d(0, 1, 0)
        xax.Unitize()
        yax.Unitize()

        neg_x = _f(site_extents[0] if len(site_extents) > 0 else 20000, 20000)
        pos_x = _f(site_extents[1] if len(site_extents) > 1 else 20000, 20000)
        neg_y = _f(site_extents[2] if len(site_extents) > 2 else 20000, 20000)
        pos_y = _f(site_extents[3] if len(site_extents) > 3 else 20000, 20000)
        half_x = (neg_x + pos_x) * 0.5
        half_y = (neg_y + pos_y) * 0.5

        # --- Resolve elevations ---
        elevs = []
        if level_elevations is not None:
            try:
                for ev in level_elevations:
                    try:
                        elevs.append(float(ev))
                    except Exception:
                        pass
            except TypeError:
                try:
                    elevs.append(float(level_elevations))
                except Exception:
                    pass
        if not elevs and level_planes is not None:
            try:
                for p in level_planes:
                    try:
                        if hasattr(p, "Origin"):
                            elevs.append(float(p.Origin.Z))
                    except Exception:
                        pass
            except TypeError:
                pass
        if not elevs:
            elevs = [org.Z, org.Z + 4000, org.Z + 8000, org.Z + 12000, org.Z + 16000]
        level_count = len(elevs)

        # --- Resolve scalar params ---
        n_seed = _i(noise_seed, 42)
        n_masses = _clamp(_i(num_masses, 3), 1, 6)
        n_holes = _clamp(_i(num_holes, 2), 0, 6)
        m_spread = max(0.0, _f(mass_spread, 8000.0))
        h_spread = max(0.0, _f(hole_spread, 5000.0))
        c_height = max(0.0, _f(cap_height, 3000.0))
        c_bulge = _clamp(_f(cap_bulge, 0.5), 0.0, 1.0)
        c_res = _clamp(_i(cap_resolution, 4), 1, 8)

        # --- Resolve per-level series ---
        def _default_bell(peak, count, lo=1):
            if count <= 1:
                return [peak]
            mid = (count - 1) / 2.0
            out = []
            for k in range(count):
                t = abs(k - mid) / mid
                v = int(round(lo + (peak - lo) * (1.0 - t * t)))
                out.append(max(lo, min(peak, v)))
            return out

        raw_mpl = _pad_ints(masses_per_level, None, level_count)
        if all(v is None or v == 0 for v in raw_mpl):
            mpl_series = _default_bell(n_masses, level_count, lo=1)
        else:
            mpl_series = [_clamp(v if v is not None else 1, 1, n_masses) for v in raw_mpl]

        raw_hpl = _pad_ints(holes_per_level, None, level_count)
        if n_holes == 0 or all(v is None or v == 0 for v in raw_hpl):
            if n_holes > 0:
                hpl_series = _default_bell(n_holes, level_count, lo=0)
            else:
                hpl_series = [0] * level_count
        else:
            hpl_series = [_clamp(v if v is not None else 0, 0, n_holes) for v in raw_hpl]

        inset_series = [max(0.0, v) for v in _pad_floats(envelope_inset, 2000.0, level_count)]
        scale_series = [_clamp(v, 0.05, 3.0) for v in _pad_floats(mass_scale, 0.9, level_count)]
        amp_series = [max(0.0, v) for v in _pad_floats(noise_amplitude, 800.0, level_count)]
        hrad_series = [max(50.0, v) for v in _pad_floats(hole_radius, 3000.0, level_count)]
        mcx_series = _pad_floats(mass_cx, 0.0, level_count)
        mcy_series = _pad_floats(mass_cy, 0.0, level_count)
        hcx_series = _pad_floats(hole_cx, 0.0, level_count)
        hcy_series = _pad_floats(hole_cy, 0.0, level_count)

        # --- Compute track base positions (site-local coords) ---
        mass_base_positions = _angular_positions(n_masses, m_spread, base_angle=0.0)
        hole_base_positions = _angular_positions(n_holes, h_spread, base_angle=math.pi / max(1, n_holes))

        mass_track_curves = [[] for _ in range(n_masses)]
        hole_track_curves = [[] for _ in range(n_holes)]

        random.seed(n_seed)

        # ---------------------------------------------------------------
        # Per-level loop
        # ---------------------------------------------------------------
        for li, z in enumerate(elevs):
            active_m = mpl_series[li]
            active_h = hpl_series[li]
            ex_inset = inset_series[li]
            m_scale = scale_series[li]
            n_amp = amp_series[li]
            h_rad = hrad_series[li]

            usable_hx = max(500.0, half_x - ex_inset)
            usable_hy = max(500.0, half_y - ex_inset)

            # --- Generate mass curves for active tracks ---
            level_env_curves = []
            for mi in range(active_m):
                rng = random.Random(n_seed + mi * 7919 + li * 3137)
                mcx_base, mcy_base = mass_base_positions[mi]
                mcx_off = mcx_base + mcx_series[li]
                mcy_off = mcy_base + mcy_series[li]
                track_rx = usable_hx * m_scale / max(1.0, math.sqrt(active_m))
                track_ry = usable_hy * m_scale / max(1.0, math.sqrt(active_m))
                c = _make_blob_curve(
                    org, xax, yax, mcx_off, mcy_off,
                    track_rx, track_ry, z, 24, rng, n_amp,
                )
                if c is not None:
                    level_env_curves.append(c)
                    mass_track_curves[mi].append((li, c))

            envelope_curves.extend(level_env_curves)

            # --- Generate hole curves for active tracks ---
            level_hole_curves = []
            for hi in range(active_h):
                hrng = random.Random(n_seed + hi * 1237 + li * 3253)
                hcx_base, hcy_base = hole_base_positions[hi]
                hcx_off = hcx_base + hcx_series[li]
                hcy_off = hcy_base + hcy_series[li]
                hc = _make_blob_curve(
                    org, xax, yax, hcx_off, hcy_off,
                    h_rad, h_rad, z, 18, hrng, n_amp * 0.2,
                )
                if hc is not None:
                    level_hole_curves.append(hc)
                    hole_track_curves[hi].append((li, hc))

            hole_curves.extend(level_hole_curves)

            # --- Floor plates: union envelope breps, diff hole breps ---
            env_breps = []
            for ec in level_env_curves:
                pb = rg.Brep.CreatePlanarBreps(ec, 0.1)
                if pb and len(pb) > 0:
                    env_breps.append(pb[0])

            floor_base = _safe_boolean_union(env_breps)

            hole_breps = []
            for hc in level_hole_curves:
                pb = rg.Brep.CreatePlanarBreps(hc, 0.1)
                if pb and len(pb) > 0:
                    hole_breps.append(pb[0])

            floor_final = _safe_boolean_diff(floor_base, hole_breps)
            floor_plates.extend(floor_final)

        # ---------------------------------------------------------------
        # Post-loop: loft mass tracks with dome caps, then boolean union
        # ---------------------------------------------------------------
        mass_lofts = []
        for mi in range(n_masses):
            curves_for_track = [c for (_, c) in mass_track_curves[mi]]
            if len(curves_for_track) < 2:
                continue

            first = curves_for_track[0]
            last = curves_for_track[-1]
            first_li = mass_track_curves[mi][0][0]
            last_li = mass_track_curves[mi][-1][0]
            first_z = elevs[first_li]
            last_z = elevs[last_li]

            loft_input = list(curves_for_track)

            # Bottom cap (downward dome) — applies whether at level 0 or above
            if c_height > 0:
                fctr = _curve_centroid_xy(first, rg.Point3d(org.X, org.Y, first_z))
                bottom_caps = _make_cap_curves(first, fctr, -1, c_height, c_bulge, c_res)
                bottom_caps.reverse()
                loft_input = bottom_caps + loft_input

            # Top cap (upward dome)
            if c_height > 0:
                lctr = _curve_centroid_xy(last, rg.Point3d(org.X, org.Y, last_z))
                top_caps = _make_cap_curves(last, lctr, +1, c_height, c_bulge, c_res)
                loft_input = loft_input + top_caps

            loft = rg.Brep.CreateFromLoft(
                loft_input, rg.Point3d.Unset, rg.Point3d.Unset,
                rg.LoftType.Normal, False,
            )
            if loft:
                for lb in loft:
                    if lb is not None:
                        mass_lofts.append(_cap_brep(lb))

        # Boolean union all mass lofts into merged envelope
        envelope_surface = _safe_boolean_union(mass_lofts)

        # ---------------------------------------------------------------
        # Post-loop: loft hole tracks with dome caps, then boolean union
        # ---------------------------------------------------------------
        void_lofts = []
        for hi in range(n_holes):
            curves_for_hole = [c for (_, c) in hole_track_curves[hi]]
            if len(curves_for_hole) < 2:
                continue

            h_first = curves_for_hole[0]
            h_last = curves_for_hole[-1]
            h_first_li = hole_track_curves[hi][0][0]
            h_last_li = hole_track_curves[hi][-1][0]
            h_first_z = elevs[h_first_li]
            h_last_z = elevs[h_last_li]

            hloft_input = list(curves_for_hole)

            if c_height > 0:
                hfctr = _curve_centroid_xy(h_first, rg.Point3d(org.X, org.Y, h_first_z))
                hbot_caps = _make_cap_curves(h_first, hfctr, -1, c_height, c_bulge, c_res)
                hbot_caps.reverse()
                hloft_input = hbot_caps + hloft_input

            if c_height > 0:
                hlctr = _curve_centroid_xy(h_last, rg.Point3d(org.X, org.Y, h_last_z))
                htop_caps = _make_cap_curves(h_last, hlctr, +1, c_height, c_bulge, c_res)
                hloft_input = hloft_input + htop_caps

            loft = rg.Brep.CreateFromLoft(
                hloft_input, rg.Point3d.Unset, rg.Point3d.Unset,
                rg.LoftType.Normal, False,
            )
            if loft:
                for vb in loft:
                    if vb is not None:
                        void_lofts.append(_cap_brep(vb))

        void_surfaces = _safe_boolean_union(void_lofts)

        # --- Boolean diff merged voids from merged envelope ---
        if envelope_surface and void_surfaces:
            diffed = _safe_boolean_diff(envelope_surface, void_surfaces)
            if diffed:
                envelope_surface = diffed

        # --- Log ---
        log_lines = [
            "envelope_v2",
            "levels: {} | masses: {} tracks, per-level: {}".format(
                level_count, n_masses, mpl_series[:min(6, level_count)],
            ),
            "holes: {} tracks, per-level: {}".format(
                n_holes, hpl_series[:min(6, level_count)],
            ),
            "envelope_curves: {} | hole_curves: {}".format(
                len(envelope_curves), len(hole_curves),
            ),
            "mass_lofts: {} | void_lofts: {}".format(
                len(mass_lofts), len(void_lofts),
            ),
            "floor_plates: {} | envelope_surface: {} | void_surfaces: {}".format(
                len(floor_plates),
                len(envelope_surface) if isinstance(envelope_surface, list) else ("ok" if envelope_surface else "none"),
                len(void_surfaces) if isinstance(void_surfaces, list) else ("ok" if void_surfaces else "none"),
            ),
            "cap: h={:.0f} bulge={:.2f} res={}".format(c_height, c_bulge, c_res),
        ]
        log = "\n".join(log_lines)
