#! python 3
# NODE_INPUTS: origin:Point3d, x_count:int, y_count:int, z_count:int, cell_size:Vector3d, gap_size:Vector3d, boundary_brep:Brep, subtract_brep:Brep, insert_mesh:Mesh, insert_brep:Brep, attractor_pt:Point3d, attr_radius:float, attr_strength:float, grid_rotation:Vector3d, voxel_rotation:Vector3d, align_to_boundary:bool, threshold:float, hollow:bool, shell_thickness:int, base_geometry:list[geometry], base_radius:float, base_strength:float, base_carve:bool, output_mode:int, seed:int
# NODE_OUTPUTS: voxels, centers, count, status_message
#
# Enhanced voxel grid v2. Adds: threshold, hollow/shell, base_geometry constraint.
# Output modes: 0=points, 1=mesh, 2=breps, 3=custom geo, 4=wireframe

import Rhino
import Rhino.Geometry as rg
import math
import random

# ─── GH UNWRAP ───────────────────────────────────────────────────────
def unwrap(obj):
    if obj is None: return None
    return obj.Value if hasattr(obj, 'Value') else obj

# ─── DISTANCE HELPER ─────────────────────────────────────────────────
def closest_dist(pt, geo):
    try:
        if isinstance(geo, rg.Point3d):
            return pt.DistanceTo(geo)
        if isinstance(geo, rg.Curve):
            rc, t = geo.ClosestPoint(pt)
            if rc:
                return pt.DistanceTo(geo.PointAt(t))
        elif isinstance(geo, rg.Brep):
            cp = geo.ClosestPoint(pt)
            return pt.DistanceTo(cp)
        elif isinstance(geo, rg.Mesh):
            cp = geo.ClosestPoint(pt)
            return pt.DistanceTo(cp)
        elif isinstance(geo, rg.Surface):
            rc, u, v = geo.ClosestPoint(pt)
            if rc:
                return pt.DistanceTo(geo.PointAt(u, v))
    except:
        pass
    return float('inf')

# ─── DEFENSIVE DEFAULTS ──────────────────────────────────────────────
origin = unwrap(origin)
if origin is None:
    origin = rg.Point3d(0, 0, 0)

boundary_brep = unwrap(boundary_brep)
subtract_brep = unwrap(subtract_brep)
insert_mesh = unwrap(insert_mesh)
insert_brep = unwrap(insert_brep)
attractor_pt = unwrap(attractor_pt)

if cell_size is None:
    cs_x, cs_y, cs_z = 5000.0, 5000.0, 3500.0
else:
    cs_x = cell_size.X if cell_size.X > 0 else 5000.0
    cs_y = cell_size.Y if cell_size.Y > 0 else 5000.0
    cs_z = cell_size.Z if cell_size.Z > 0 else 3500.0

if gap_size is None:
    gp_x, gp_y, gp_z = 0.0, 0.0, 0.0
else:
    gp_x = gap_size.X
    gp_y = gap_size.Y
    gp_z = gap_size.Z

if not x_count or x_count < 1: x_count = 4
if not y_count or y_count < 1: y_count = 4
if not z_count or z_count < 1: z_count = 3

if not attr_radius or attr_radius <= 0: attr_radius = 20000.0
if attr_strength is None: attr_strength = 0.0
if output_mode is None: output_mode = 1
if seed is not None: random.seed(seed)

if grid_rotation is None:
    gr_x, gr_y, gr_z = 0.0, 0.0, 0.0
else:
    gr_x, gr_y, gr_z = grid_rotation.X, grid_rotation.Y, grid_rotation.Z

if voxel_rotation is None:
    vr_x, vr_y, vr_z = 0.0, 0.0, 0.0
else:
    vr_x, vr_y, vr_z = voxel_rotation.X, voxel_rotation.Y, voxel_rotation.Z

if align_to_boundary is None: align_to_boundary = False

# v2 new params
if threshold is None: threshold = 0.0
if hollow is None: hollow = False
if shell_thickness is None or shell_thickness < 1: shell_thickness = 1
if not base_geometry: base_geometry = []
if base_radius is None or base_radius <= 0: base_radius = 20000.0
if base_strength is None: base_strength = 0.5
if base_carve is None: base_carve = False

tol = 0.01
try:
    tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
except:
    pass

# ─── GRID PLANE ──────────────────────────────────────────────────────
grid_plane = rg.Plane.WorldXY
grid_plane.Origin = rg.Point3d(origin)

if align_to_boundary and boundary_brep is not None:
    bb = boundary_brep.GetBoundingBox(True)
    mid_z = (bb.Min.Z + bb.Max.Z) / 2.0
    section_curves = rg.Brep.CreateContourCurves(
        boundary_brep,
        rg.Point3d(0, 0, mid_z - 1), rg.Point3d(0, 0, mid_z + 1), 10)
    if section_curves and len(section_curves) > 0:
        longest = max(section_curves, key=lambda c: c.GetLength())
        if longest.IsValid:
            pts = []
            divs = longest.DivideByCount(20, True)
            if divs:
                for t in divs:
                    pts.append(longest.PointAt(t))
            if len(pts) > 2:
                max_dist = 0
                pt_a, pt_b = pts[0], pts[1]
                for i in range(len(pts)):
                    for j in range(i + 1, len(pts)):
                        d = pts[i].DistanceTo(pts[j])
                        if d > max_dist:
                            max_dist = d
                            pt_a, pt_b = pts[i], pts[j]
                x_dir = rg.Vector3d(pt_b - pt_a)
                x_dir.Z = 0
                if x_dir.Length > 0.001:
                    x_dir.Unitize()
                    y_dir = rg.Vector3d.CrossProduct(rg.Vector3d.ZAxis, x_dir)
                    if y_dir.Length > 0.001:
                        y_dir.Unitize()
                        grid_plane = rg.Plane(origin, x_dir, y_dir)

if gr_x != 0.0:
    grid_plane.Rotate(math.radians(gr_x), grid_plane.XAxis, grid_plane.Origin)
if gr_y != 0.0:
    grid_plane.Rotate(math.radians(gr_y), grid_plane.YAxis, grid_plane.Origin)
if gr_z != 0.0:
    grid_plane.Rotate(math.radians(gr_z), grid_plane.ZAxis, grid_plane.Origin)


# ─── STEPS ────────────────────────────────────────────────────────────
step_x = cs_x + gp_x
step_y = cs_y + gp_y
step_z = cs_z + gp_z

inv_attr_r = 1.0 / attr_radius if attr_radius > 0.001 else 0.0
inv_base_r = 1.0 / base_radius if base_radius > 0.001 else 0.0
has_base = len(base_geometry) > 0
half_bs = base_strength * 0.5

# ─── FIRST PASS: SAMPLE DENSITY ──────────────────────────────────────
density = {}      # (ix,iy,iz) -> val
all_cells = {}    # (ix,iy,iz) -> center

for ix in range(x_count):
    for iy in range(y_count):
        for iz in range(z_count):
            local_x = ix * step_x
            local_y = iy * step_y
            local_z = iz * step_z

            world_xy = grid_plane.PointAt(local_x + cs_x / 2.0, local_y + cs_y / 2.0)
            center = rg.Point3d(world_xy.X, world_xy.Y, origin.Z + local_z + cs_z / 2.0)

            # Boundary check
            if boundary_brep is not None and not boundary_brep.IsPointInside(center, tol, False):
                continue
            # Subtract check
            if subtract_brep is not None and subtract_brep.IsPointInside(center, tol, False):
                continue

            val = 1.0  # base density

            # Attractor influence
            if attractor_pt is not None and attr_strength != 0.0:
                dist = center.DistanceTo(attractor_pt)
                if dist < attr_radius:
                    influence = 1.0 - (dist * inv_attr_r)
                    val += influence * attr_strength

            # Base geometry influence
            if has_base:
                min_d = float('inf')
                for geo in base_geometry:
                    geo_uw = unwrap(geo)
                    if geo_uw is None:
                        continue
                    d = closest_dist(center, geo_uw)
                    if d < min_d:
                        min_d = d
                if base_carve:
                    if min_d < base_radius:
                        val -= (1.0 - min_d * inv_base_r) * base_strength
                else:
                    if min_d < base_radius:
                        val += (1.0 - min_d * inv_base_r) * base_strength
                    else:
                        val -= half_bs

            # Clamp
            if val < 0.0: val = 0.0
            elif val > 1.0: val = 1.0

            density[(ix, iy, iz)] = val
            all_cells[(ix, iy, iz)] = center


# ─── THRESHOLD + HOLLOW FILTER ───────────────────────────────────────
face_dirs = ((-1,0,0),(1,0,0),(0,-1,0),(0,1,0),(0,0,-1),(0,0,1))
valid_voxels = []
centers = []

for key, center in all_cells.items():
    val = density[key]
    if val <= threshold:
        continue

    ix, iy, iz = key

    if hollow:
        is_interior = True
        for dx, dy, dz in face_dirs:
            nk = (ix + dx, iy + dy, iz + dz)
            if nk not in density or density[nk] <= threshold:
                is_interior = False
                break
        if is_interior:
            depth = min(ix, x_count - 1 - ix,
                        iy, y_count - 1 - iy,
                        iz, z_count - 1 - iz)
            if depth > shell_thickness:
                continue

    valid_voxels.append({'center': center, 'scale': val, 'key': key})
    centers.append(center)


# ─── CORNER BUILDER ──────────────────────────────────────────────────
def build_corners(center, sx, sy, sz, scale):
    hx = sx * scale * 0.5
    hy = sy * scale * 0.5
    hz = sz * scale * 0.5
    corners = [
        (-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz),
        (-hx, -hy,  hz), (hx, -hy,  hz), (hx, hy,  hz), (-hx, hy,  hz)
    ]
    if vr_x != 0.0 or vr_y != 0.0 or vr_z != 0.0:
        rotated = []
        for (dx, dy, dz) in corners:
            if vr_x != 0.0:
                rad = math.radians(vr_x)
                c_a, s_a = math.cos(rad), math.sin(rad)
                dy2 = dy * c_a - dz * s_a
                dz2 = dy * s_a + dz * c_a
                dy, dz = dy2, dz2
            if vr_y != 0.0:
                rad = math.radians(vr_y)
                c_a, s_a = math.cos(rad), math.sin(rad)
                dx2 = dx * c_a + dz * s_a
                dz2 = -dx * s_a + dz * c_a
                dx, dz = dx2, dz2
            if vr_z != 0.0:
                rad = math.radians(vr_z)
                c_a, s_a = math.cos(rad), math.sin(rad)
                dx2 = dx * c_a - dy * s_a
                dy2 = dx * s_a + dy * c_a
                dx, dy = dx2, dy2
            rotated.append((dx, dy, dz))
        corners = rotated
    pts = [rg.Point3d(center.X + dx, center.Y + dy, center.Z + dz)
           for (dx, dy, dz) in corners]
    return pts


# ─── OUTPUT ──────────────────────────────────────────────────────────
voxels = []

if output_mode == 0:
    pass  # points only

elif output_mode == 1:
    mesh = rg.Mesh()
    for v in valid_voxels:
        pts = build_corners(v['center'], cs_x, cs_y, cs_z, 1.0)
        b = mesh.Vertices.Count
        for pt in pts:
            mesh.Vertices.Add(pt)
        mesh.Faces.AddFace(b, b+1, b+2, b+3)
        mesh.Faces.AddFace(b+4, b+7, b+6, b+5)
        mesh.Faces.AddFace(b, b+4, b+5, b+1)
        mesh.Faces.AddFace(b+2, b+6, b+7, b+3)
        mesh.Faces.AddFace(b, b+3, b+7, b+4)
        mesh.Faces.AddFace(b+1, b+5, b+6, b+2)
    if mesh.Vertices.Count > 0:
        mesh.Normals.ComputeNormals()
        mesh.Compact()
        voxels.append(mesh)

elif output_mode == 2:
    for v in valid_voxels:
        c = v['center']
        box = rg.Box(rg.Plane(c, rg.Vector3d.ZAxis),
            rg.Interval(-cs_x/2, cs_x/2),
            rg.Interval(-cs_y/2, cs_y/2),
            rg.Interval(-cs_z/2, cs_z/2))
        brep = box.ToBrep()
        if brep:
            if vr_x != 0:
                brep.Transform(rg.Transform.Rotation(math.radians(vr_x), rg.Vector3d.XAxis, rg.Point3d.Origin))
            if vr_y != 0:
                brep.Transform(rg.Transform.Rotation(math.radians(vr_y), rg.Vector3d.YAxis, rg.Point3d.Origin))
            if vr_z != 0:
                brep.Transform(rg.Transform.Rotation(math.radians(vr_z), rg.Vector3d.ZAxis, rg.Point3d.Origin))
            brep.Translate(rg.Vector3d(c))
            voxels.append(brep)

elif output_mode == 3:
    if insert_mesh is not None:
        mesh = rg.Mesh()
        base_center = insert_mesh.GetBoundingBox(True).Center
        for v in valid_voxels:
            c, s = v['center'], v['scale']
            dup = insert_mesh.DuplicateMesh()
            dup.Translate(rg.Vector3d(-base_center.X, -base_center.Y, -base_center.Z))
            dup.Transform(rg.Transform.Scale(rg.Point3d.Origin, s))
            if vr_x != 0: dup.Transform(rg.Transform.Rotation(math.radians(vr_x), rg.Vector3d.XAxis, rg.Point3d.Origin))
            if vr_y != 0: dup.Transform(rg.Transform.Rotation(math.radians(vr_y), rg.Vector3d.YAxis, rg.Point3d.Origin))
            if vr_z != 0: dup.Transform(rg.Transform.Rotation(math.radians(vr_z), rg.Vector3d.ZAxis, rg.Point3d.Origin))
            dup.Translate(rg.Vector3d(c.X, c.Y, c.Z))
            mesh.Append(dup)
        if mesh.Vertices.Count > 0:
            mesh.Normals.ComputeNormals()
            mesh.Compact()
            voxels.append(mesh)
    elif insert_brep is not None:
        base_center = insert_brep.GetBoundingBox(True).Center
        for v in valid_voxels:
            c, s = v['center'], v['scale']
            dup = insert_brep.DuplicateBrep()
            dup.Translate(rg.Vector3d(-base_center.X, -base_center.Y, -base_center.Z))
            dup.Transform(rg.Transform.Scale(rg.Point3d.Origin, s))
            if vr_x != 0: dup.Transform(rg.Transform.Rotation(math.radians(vr_x), rg.Vector3d.XAxis, rg.Point3d.Origin))
            if vr_y != 0: dup.Transform(rg.Transform.Rotation(math.radians(vr_y), rg.Vector3d.YAxis, rg.Point3d.Origin))
            if vr_z != 0: dup.Transform(rg.Transform.Rotation(math.radians(vr_z), rg.Vector3d.ZAxis, rg.Point3d.Origin))
            dup.Translate(rg.Vector3d(c.X, c.Y, c.Z))
            voxels.append(dup)

elif output_mode == 4:
    for v in valid_voxels:
        pts = build_corners(v['center'], cs_x, cs_y, cs_z, 1.0)
        for a, b in [(0,1),(1,2),(2,3),(3,0),(4,5),(5,6),(6,7),(7,4),(0,4),(1,5),(2,6),(3,7)]:
            voxels.append(rg.LineCurve(pts[a], pts[b]))


# ─── FINAL ───────────────────────────────────────────────────────────
count = len(valid_voxels)
status_message = "Voxels v2: {} | Grid: {}x{}x{} | Mode: {} | Hollow: {} | Shell: {} | Thr: {:.2f} | Base: {} | Align: {}".format(
    count, x_count, y_count, z_count, output_mode, hollow,
    shell_thickness, threshold, len(base_geometry), align_to_boundary)
