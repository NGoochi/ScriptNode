#! python 3
# NODE_INPUTS: boundary_brep:Brep, ground_plane:Surface, num_levels:int, level_heights:list[float], level_z_offsets:list[float], floor_area_offsets:list[float], subtract_breps:list[Brep], room_divisions:list[int], room_mode:int, output_mode:int, seed:int
# NODE_OUTPUTS: output_geo, floors, level_volumes, room_volumes, room_surfaces, room_walls, level_planes, log
#
# Building levels generator — floor plates, rooms, envelope from boundary brep.
# room_mode: 0=rectangular grid, 1=voronoi
# output_mode: 0=wireframe preview, 1=floors+levels, 2=rooms, 3=everything

import Rhino
import Rhino.Geometry as rg
import math
import random

# ─── GH UNWRAP ───────────────────────────────────────────────────────
def unwrap(obj):
    if obj is None: return None
    return obj.Value if hasattr(obj, 'Value') else obj

def unwrap_list(lst):
    if not lst: return []
    return [v for v in (unwrap(item) for item in lst) if v is not None]

# ─── DEFENSIVE DEFAULTS ──────────────────────────────────────────────
boundary_brep = unwrap(boundary_brep)
ground_plane = unwrap(ground_plane)

if num_levels is None or num_levels < 1: num_levels = 5
if not level_heights: level_heights = []
if not level_z_offsets: level_z_offsets = []
if not floor_area_offsets: floor_area_offsets = []
subtract_breps = unwrap_list(subtract_breps) if subtract_breps else []
if not room_divisions: room_divisions = []
if room_mode is None: room_mode = 0
if output_mode is None: output_mode = 1
if seed is not None: random.seed(seed)

tol = 0.01
try:
    tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
except:
    pass

DEFAULT_HEIGHT = 3500.0  # mm

def get_val(lst, idx, default):
    """Get list value at index, or default."""
    if lst and idx < len(lst) and lst[idx] is not None:
        return float(lst[idx])
    return default

# ─── ORIGIN: brep × ground_plane intersection ────────────────────────
origin = rg.Point3d(0, 0, 0)
ground_z = 0.0

if boundary_brep is not None:
    bb = boundary_brep.GetBoundingBox(True)

    if ground_plane is not None:
        # Handle Surface or Brep input for ground_plane
        gp = ground_plane
        if isinstance(gp, rg.Brep):
            # Use first face
            if gp.Faces.Count > 0:
                gp = gp.Faces[0].UnderlyingSurface()
        elif isinstance(gp, rg.Extrusion):
            gp_brep = gp.ToBrep()
            if gp_brep and gp_brep.Faces.Count > 0:
                gp = gp_brep.Faces[0].UnderlyingSurface()

        if gp is not None and hasattr(gp, 'ClosestPoint'):
            rc, u, v = gp.ClosestPoint(bb.Center)
            if rc:
                ground_z = gp.PointAt(u, v).Z
    else:
        ground_z = bb.Min.Z

    # Origin = centroid at ground Z
    amp = rg.AreaMassProperties.Compute(boundary_brep)
    if amp:
        centroid = amp.Centroid
        origin = rg.Point3d(centroid.X, centroid.Y, ground_z)
    else:
        origin = rg.Point3d(bb.Center.X, bb.Center.Y, ground_z)

# ─── COMPUTE LEVEL Z POSITIONS ───────────────────────────────────────
# Level 0 = ground. Positive = up. We only go up for now (sub-levels
# can be added by setting negative z_offsets or negative level_heights).
level_z_bottoms = []
cumulative_z = ground_z

for i in range(num_levels):
    h = get_val(level_heights, i, DEFAULT_HEIGHT)
    z_shift = get_val(level_z_offsets, i, 0.0)
    level_z_bottoms.append(cumulative_z + z_shift)
    cumulative_z += h

# Level heights for reference
level_hs = [get_val(level_heights, i, DEFAULT_HEIGHT) for i in range(num_levels)]


# ─── HELPER: Section brep at Z → get floor curves ────────────────────
def section_at_z(brep, z):
    """Section a brep with a horizontal plane at z. Returns curves."""
    if brep is None or not brep.IsValid:
        return []
    pt_a = rg.Point3d(0, 0, z - 1)
    pt_b = rg.Point3d(0, 0, z + 1)
    crvs = rg.Brep.CreateContourCurves(brep, pt_a, pt_b, 10)
    if not crvs:
        return []
    return [c for c in crvs if c is not None and c.IsValid]


# ─── HELPER: Offset curve (floor area) ───────────────────────────────
def offset_floor_curve(crv, offset_mm, z_height, sub_breps):
    """Offset a closed curve inward (negative) or outward (positive).
    Also offsets around subtract_brep intersections if present."""
    if abs(offset_mm) < 0.1:
        result = [crv]
    else:
        normal = rg.Vector3d.ZAxis
        offsets = crv.Offset(rg.Plane(rg.Point3d(0, 0, z_height), normal),
                             offset_mm, tol, rg.CurveOffsetCornerStyle.Sharp)
        if offsets and len(offsets) > 0:
            result = list(offsets)
        else:
            result = [crv]

    # Subtract breps: section each at this Z, then boolean-difference
    if sub_breps:
        for sb in sub_breps:
            sub_curves = section_at_z(sb, z_height)
            for sc in sub_curves:
                # Boolean difference: remove subtract curve from floor curve
                new_result = []
                for flr_crv in result:
                    try:
                        diff = rg.Curve.CreateBooleanDifference(flr_crv, sc, tol)
                        if diff and len(diff) > 0:
                            new_result.extend(diff)
                        else:
                            new_result.append(flr_crv)
                    except:
                        new_result.append(flr_crv)
                result = new_result

    return result


# ─── HELPER: Create floor surface from curves ────────────────────────
def curves_to_surfaces(curves):
    """Create planar surfaces from closed curves."""
    surfaces = []
    for crv in curves:
        if crv is None or not crv.IsClosed:
            continue
        breps = rg.Brep.CreatePlanarBreps(crv, tol)
        if breps:
            surfaces.extend(breps)
    return surfaces


# ─── HELPER: Rectangular room subdivision ─────────────────────────────
def subdivide_rect(floor_curves, divisions, z_bottom, z_top):
    """Divide floor into rectangular grid cells.
    Returns: room_breps, room_surfs, room_walls"""
    room_breps = []
    room_surfs = []
    room_walls = []

    if divisions <= 0:
        return room_breps, room_surfs, room_walls

    wall_height = z_top - z_bottom
    # Use sqrt to make a roughly square grid
    nx = max(1, int(math.ceil(math.sqrt(divisions))))
    ny = max(1, int(math.ceil(divisions / float(nx))))

    for crv in floor_curves:
        if crv is None or not crv.IsClosed:
            continue

        bb = crv.GetBoundingBox(True)
        dx = (bb.Max.X - bb.Min.X) / nx
        dy = (bb.Max.Y - bb.Min.Y) / ny

        for ix in range(nx):
            for iy in range(ny):
                # Cell rectangle
                x0 = bb.Min.X + ix * dx
                y0 = bb.Min.Y + iy * dy
                x1 = x0 + dx
                y1 = y0 + dy

                cell_pts = [
                    rg.Point3d(x0, y0, z_bottom),
                    rg.Point3d(x1, y0, z_bottom),
                    rg.Point3d(x1, y1, z_bottom),
                    rg.Point3d(x0, y1, z_bottom),
                    rg.Point3d(x0, y0, z_bottom),
                ]
                cell_crv = rg.PolylineCurve(cell_pts)

                # Intersect cell with floor curve
                try:
                    intersected = rg.Curve.CreateBooleanIntersection(cell_crv, crv, tol)
                    if intersected and len(intersected) > 0:
                        for ic in intersected:
                            # Floor surface
                            srf = rg.Brep.CreatePlanarBreps(ic, tol)
                            if srf:
                                room_surfs.extend(srf)

                            # Walls: extrude cell edges upward
                            if wall_height > 0 and ic.IsClosed:
                                ext_vec = rg.Vector3d(0, 0, wall_height)
                                wall_srf = rg.Surface.CreateExtrusion(ic, ext_vec)
                                if wall_srf:
                                    room_walls.append(wall_srf.ToBrep())

                                # Full room volume (closed box)
                                top_crv = ic.DuplicateCurve()
                                top_crv.Translate(ext_vec)
                                top_srf = rg.Brep.CreatePlanarBreps(top_crv, tol)
                                bot_srf = srf

                                # Join into closed brep
                                all_faces = []
                                if bot_srf: all_faces.extend(bot_srf)
                                if top_srf: all_faces.extend(top_srf)
                                if wall_srf: all_faces.append(wall_srf.ToBrep())

                                joined = rg.Brep.JoinBreps(all_faces, tol)
                                if joined:
                                    for jb in joined:
                                        room_breps.append(jb)
                    else:
                        # No intersection — cell fully inside or outside
                        # Quick check: is cell center inside floor curve?
                        cell_center = rg.Point3d((x0 + x1)/2, (y0 + y1)/2, z_bottom)
                        if crv.Contains(cell_center, rg.Plane(rg.Point3d(0,0,z_bottom), rg.Vector3d.ZAxis), tol) == rg.PointContainment.Inside:
                            srf = rg.Brep.CreatePlanarBreps(cell_crv, tol)
                            if srf:
                                room_surfs.extend(srf)
                            if wall_height > 0:
                                ext_vec = rg.Vector3d(0, 0, wall_height)
                                wall_srf = rg.Surface.CreateExtrusion(cell_crv, ext_vec)
                                if wall_srf:
                                    room_walls.append(wall_srf.ToBrep())
                except:
                    pass

    return room_breps, room_surfs, room_walls


# ─── HELPER: Voronoi room subdivision ─────────────────────────────────
def subdivide_voronoi(floor_curves, num_cells, z_bottom, z_top, rng_seed):
    """Divide floor into Voronoi cells.
    Returns: room_breps, room_surfs, room_walls"""
    room_breps = []
    room_surfs = []
    room_walls = []

    if num_cells <= 0:
        return room_breps, room_surfs, room_walls

    wall_height = z_top - z_bottom

    for crv in floor_curves:
        if crv is None or not crv.IsClosed:
            continue

        bb = crv.GetBoundingBox(True)

        # Generate random seed points within the curve
        seed_pts = []
        attempts = 0
        while len(seed_pts) < num_cells and attempts < num_cells * 20:
            pt = rg.Point3d(
                random.uniform(bb.Min.X, bb.Max.X),
                random.uniform(bb.Min.Y, bb.Max.Y),
                z_bottom
            )
            contain = crv.Contains(pt, rg.Plane(rg.Point3d(0, 0, z_bottom), rg.Vector3d.ZAxis), tol)
            if contain == rg.PointContainment.Inside:
                seed_pts.append(rg.Point2d(pt.X, pt.Y))
            attempts += 1

        if len(seed_pts) < 2:
            continue

        # Create Voronoi diagram using Rhino's built-in
        # Voronoi cells bounded by the floor curve's bounding box
        outline = rg.Polyline([
            rg.Point2d(bb.Min.X - 100, bb.Min.Y - 100),
            rg.Point2d(bb.Max.X + 100, bb.Min.Y - 100),
            rg.Point2d(bb.Max.X + 100, bb.Max.Y + 100),
            rg.Point2d(bb.Min.X - 100, bb.Max.Y + 100),
        ])

        voronoi = rg.Voronoi.Solve_Connectivity(seed_pts, outline, False)
        if voronoi is None:
            continue

        # Process each Voronoi cell
        for cell_pts_2d in voronoi:
            if cell_pts_2d is None or len(cell_pts_2d) < 3:
                continue

            # Convert to 3D and close the curve
            pts_3d = [rg.Point3d(p.X, p.Y, z_bottom) for p in cell_pts_2d]
            pts_3d.append(pts_3d[0])  # close
            cell_crv = rg.PolylineCurve(pts_3d)

            if not cell_crv.IsClosed:
                continue

            # Intersect with floor curve to clip
            try:
                clipped = rg.Curve.CreateBooleanIntersection(cell_crv, crv, tol)
                if clipped and len(clipped) > 0:
                    for cc in clipped:
                        srf = rg.Brep.CreatePlanarBreps(cc, tol)
                        if srf:
                            room_surfs.extend(srf)

                        if wall_height > 0 and cc.IsClosed:
                            ext_vec = rg.Vector3d(0, 0, wall_height)
                            wall_srf = rg.Surface.CreateExtrusion(cc, ext_vec)
                            if wall_srf:
                                room_walls.append(wall_srf.ToBrep())

                            # Volume
                            top_crv = cc.DuplicateCurve()
                            top_crv.Translate(ext_vec)
                            top_srf = rg.Brep.CreatePlanarBreps(top_crv, tol)
                            bot_srf = srf

                            all_faces = []
                            if bot_srf: all_faces.extend(bot_srf)
                            if top_srf: all_faces.extend(top_srf)
                            if wall_srf: all_faces.append(wall_srf.ToBrep())

                            joined = rg.Brep.JoinBreps(all_faces, tol)
                            if joined:
                                for jb in joined:
                                    room_breps.append(jb)
            except:
                pass

    return room_breps, room_surfs, room_walls


# ─── MAIN: PROCESS EACH LEVEL ────────────────────────────────────────
output_geo = []     # wireframe overview
floors = []         # nested: one sub-list per level
level_volumes = []
room_volumes = []
room_surfaces = []
room_walls = []
level_planes = []

for i in range(num_levels):
    z_bot = level_z_bottoms[i] if i < len(level_z_bottoms) else ground_z
    h = level_hs[i] if i < len(level_hs) else DEFAULT_HEIGHT
    z_top = z_bot + h
    floor_offset = get_val(floor_area_offsets, i, 0.0)
    room_div = int(get_val(room_divisions, i, 0.0))

    # Level plane
    lv_plane = rg.Plane(rg.Point3d(origin.X, origin.Y, z_bot), rg.Vector3d.ZAxis)
    level_planes.append(lv_plane)

    # Section boundary brep at this Z to get floor outline
    if boundary_brep is not None:
        section_crvs = section_at_z(boundary_brep, z_bot)
    else:
        # Fallback: 20m square
        rect_pts = [
            rg.Point3d(-10000, -10000, z_bot), rg.Point3d(10000, -10000, z_bot),
            rg.Point3d(10000, 10000, z_bot), rg.Point3d(-10000, 10000, z_bot),
            rg.Point3d(-10000, -10000, z_bot),
        ]
        section_crvs = [rg.PolylineCurve(rect_pts)]

    if not section_crvs:
        floors.append([])
        level_volumes.append(None)
        room_volumes.append([])
        room_surfaces.append([])
        room_walls.append([])
        continue

    # Apply floor area offset + subtract
    offset_crvs = []
    for sc in section_crvs:
        offset_crvs.extend(offset_floor_curve(sc, floor_offset, z_bot, subtract_breps))

    # Floor surfaces
    floor_surfs = curves_to_surfaces(offset_crvs)
    floors.append(floor_surfs)

    # Level volume (floor-to-floor extrusion)
    lv_volume = None
    if output_mode >= 1:
        lv_breps = []
        for flr_crv in offset_crvs:
            if flr_crv.IsClosed:
                ext_vec = rg.Vector3d(0, 0, h)
                ext_srf = rg.Surface.CreateExtrusion(flr_crv, ext_vec)
                if ext_srf:
                    brep = ext_srf.ToBrep()
                    capped = brep.CapPlanarHoles(tol)
                    if capped:
                        lv_breps.append(capped)
                    else:
                        lv_breps.append(brep)
        if lv_breps:
            lv_volume = lv_breps
    level_volumes.append(lv_volume)

    # Room subdivision (mode 2+)
    if output_mode >= 2 and room_div > 0:
        z_next = z_top
        if room_mode == 1:
            rb, rs, rw = subdivide_voronoi(offset_crvs, room_div, z_bot, z_next, seed)
        else:
            rb, rs, rw = subdivide_rect(offset_crvs, room_div, z_bot, z_next)
        room_volumes.append(rb)
        room_surfaces.append(rs)
        room_walls.append(rw)
    else:
        room_volumes.append([])
        room_surfaces.append([])
        room_walls.append([])

    # Wireframe output (always)
    for crv in offset_crvs:
        output_geo.append(crv)
        # Top edge
        top_crv = crv.DuplicateCurve()
        top_crv.Translate(rg.Vector3d(0, 0, h))
        output_geo.append(top_crv)


# ─── ENVELOPE (full building outline) ─────────────────────────────────
# Loft the floor outlines to create the envelope
if output_mode >= 1 and boundary_brep is not None:
    # Simple: just output the bounding brep as envelope
    output_geo.insert(0, boundary_brep)

# ─── LOG ─────────────────────────────────────────────────────────────
room_count = sum(len(rv) for rv in room_volumes if rv)
floor_count = sum(len(fs) for fs in floors if fs)
log = "Levels: {} | Floors: {} | Rooms: {} | Mode: {} | Room type: {}".format(
    num_levels, floor_count, room_count, output_mode,
    ["rectangular", "voronoi"][room_mode]
)
