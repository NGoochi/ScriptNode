#! python 3
# NODE_INPUTS: base_pt:Point3d, align_vec:Vector3d, ground_pln:Plane, voxel_size:Vector3d, grid_size:Vector3d, geo_mode:int, max_geo:int, include_json_voxels:bool, include_center_pts:bool
# NODE_OUTPUTS: grid_data, voxel_breps, wireframe, corner_pts, center_pts, log
#
# Base Voxel Grid — Spatial Database (v1)
# Generates a 3D voxel grid centred on base_pt.
# Primary output is grid_data (JSON string) that downstream scripts
# parse, enrich with analysis fields, and pass along the pipeline.
#
# Inputs:
#   base_pt      — Grid origin override. Default: 0,0,0
#   align_vec    — Aligns grid X-axis to this vector. Default: World X
#   ground_pln   — Base plane for the grid (supports sloped sites). Default: WorldXY
#   voxel_size   — XYZ dimensions per voxel in mm. Default: 1000,1000,1000
#   grid_size    — Number of voxels in X,Y,Z. Default: 100,100,100
#   geo_mode     — 0=no geometry, 1=wireframe, 2=breps, 3=both. Default: 0
#   max_geo      — Skip geometry if voxel count exceeds this. Default: 10000
#   include_json_voxels — If False, grid_data omits per-voxel records (huge speedup). Default: True
#   include_center_pts  — If False, center_pts is empty (faster GH). Default: True
#
# Outputs:
#   grid_data    — JSON spatial database string
#   voxel_breps  — Box breps (geo_mode 2 or 3)
#   wireframe    — LineCurves, 12 edges per voxel (geo_mode 1 or 3)
#   corner_pts   — Deduplicated corner lattice points
#   center_pts   — All voxel centre points
#   log          — Status text for a panel

import Rhino.Geometry as rg
import json

# ─── DEFENSIVE DEFAULTS ─────────────────────────────────────────────

if base_pt is None:
    base_pt = rg.Point3d(0, 0, 0)

if align_vec is None:
    align_vec = rg.Vector3d(1, 0, 0)

if ground_pln is None:
    ground_pln = rg.Plane.WorldXY

if voxel_size is None:
    sx, sy, sz = 1000.0, 1000.0, 1000.0
else:
    sx = voxel_size.X if voxel_size.X > 0 else 1000.0
    sy = voxel_size.Y if voxel_size.Y > 0 else 1000.0
    sz = voxel_size.Z if voxel_size.Z > 0 else 1000.0

if grid_size is None:
    nx, ny, nz = 100, 100, 100
else:
    nx = max(1, int(round(grid_size.X)))
    ny = max(1, int(round(grid_size.Y)))
    nz = max(1, int(round(grid_size.Z)))

if geo_mode is None:
    geo_mode = 0
geo_mode = max(0, min(3, geo_mode))

if max_geo is None:
    max_geo = 10000
max_geo = max(0, max_geo)

if include_json_voxels is None:
    include_json_voxels = True
if include_center_pts is None:
    include_center_pts = True

# ─── GRID PLANE CONSTRUCTION ────────────────────────────────────────
# Start from ground_pln, relocate origin to base_pt,
# then rotate X-axis to match align_vec (projected onto ground plane).

grid_plane = rg.Plane(ground_pln)
grid_plane.Origin = rg.Point3d(base_pt)

av = rg.Vector3d(align_vec)
# Project align_vec onto the ground plane's horizontal
# by removing the component along the plane's Z-axis
dot = av.X * grid_plane.ZAxis.X + av.Y * grid_plane.ZAxis.Y + av.Z * grid_plane.ZAxis.Z
av = rg.Vector3d(
    av.X - dot * grid_plane.ZAxis.X,
    av.Y - dot * grid_plane.ZAxis.Y,
    av.Z - dot * grid_plane.ZAxis.Z,
)
if av.Length > 0.001:
    av.Unitize()
    y_dir = rg.Vector3d.CrossProduct(grid_plane.ZAxis, av)
    if y_dir.Length > 0.001:
        y_dir.Unitize()
        grid_plane = rg.Plane(rg.Point3d(base_pt), av, y_dir)

# ─── PRECOMPUTE AXIS VECTORS ────────────────────────────────────────
# Extracting floats avoids per-voxel RhinoCommon method calls.

ox = grid_plane.Origin.X
oy = grid_plane.Origin.Y
oz = grid_plane.Origin.Z
xx = grid_plane.XAxis.X
xy = grid_plane.XAxis.Y
xz = grid_plane.XAxis.Z
yx = grid_plane.YAxis.X
yy = grid_plane.YAxis.Y
yz = grid_plane.YAxis.Z
zx = grid_plane.ZAxis.X
zy = grid_plane.ZAxis.Y
zz = grid_plane.ZAxis.Z

# ─── BUILD VOXEL DATA + CENTRES ─────────────────────────────────────

total_count = nx * ny * nz
half_nx = nx / 2.0
half_ny = ny / 2.0
half_nz = nz / 2.0

generate_geo = geo_mode > 0 and total_count <= max_geo
need_voxel_json = include_json_voxels
need_center_list = include_center_pts
# Geo can rebuild centres from (ix,iy,iz) so we skip million Point3d allocs when unwired.
need_main_loop = need_voxel_json or need_center_list

voxel_records = []
center_pts = []

if need_main_loop:
    rec_append = voxel_records.append
    ctr_append = center_pts.append
    for ix in range(nx):
        lx = (ix - half_nx + 0.5) * sx
        for iy in range(ny):
            ly = (iy - half_ny + 0.5) * sy
            for iz in range(nz):
                lz = (iz - half_nz + 0.5) * sz

                cx = ox + xx * lx + yx * ly + zx * lz
                cy = oy + xy * lx + yy * ly + zy * lz
                cz = oz + xz * lx + yz * ly + zz * lz

                if need_voxel_json:
                    rec_append({
                        "i": ix, "j": iy, "k": iz,
                        "cx": round(cx, 2), "cy": round(cy, 2), "cz": round(cz, 2),
                    })
                if need_center_list:
                    ctr_append(rg.Point3d(cx, cy, cz))

# ─── JSON SERIALISATION ─────────────────────────────────────────────

_grid_payload = {
    "version": 1,
    "base_pt": [round(base_pt.X, 2), round(base_pt.Y, 2), round(base_pt.Z, 2)],
    "align_vec": [round(align_vec.X, 4), round(align_vec.Y, 4), round(align_vec.Z, 4)],
    "ground_pln_origin": [
        round(ground_pln.Origin.X, 2),
        round(ground_pln.Origin.Y, 2),
        round(ground_pln.Origin.Z, 2),
    ],
    "ground_pln_normal": [
        round(ground_pln.ZAxis.X, 4),
        round(ground_pln.ZAxis.Y, 4),
        round(ground_pln.ZAxis.Z, 4),
    ],
    "voxel_size": [sx, sy, sz],
    "grid_size": [nx, ny, nz],
    "count": total_count,
    "voxels": voxel_records,
}
if not need_voxel_json:
    _grid_payload["implicit_voxels"] = True

grid_data = json.dumps(_grid_payload, separators=(",", ":"))

# ─── GEOMETRY GENERATION (gated) ────────────────────────────────────

voxel_breps = []
wireframe = []
corner_pts_geo = []

if generate_geo:
    hsx = sx / 2.0
    hsy = sy / 2.0
    hsz = sz / 2.0

    # Precompute half-dimension vectors along each axis
    dx_pos = rg.Vector3d(xx * hsx, xy * hsx, xz * hsx)
    dx_neg = rg.Vector3d(-xx * hsx, -xy * hsx, -xz * hsx)
    dy_pos = rg.Vector3d(yx * hsy, yy * hsy, yz * hsy)
    dy_neg = rg.Vector3d(-yx * hsy, -yy * hsy, -yz * hsy)
    dz_pos = rg.Vector3d(zx * hsz, zy * hsz, zz * hsz)
    dz_neg = rg.Vector3d(-zx * hsz, -zy * hsz, -zz * hsz)

    x_ivl = rg.Interval(-hsx, hsx)
    y_ivl = rg.Interval(-hsy, hsy)
    z_ivl = rg.Interval(-hsz, hsz)

    # Edge index pairs for wireframe (8 corners of a box)
    edge_pairs = [
        (0, 1), (1, 2), (2, 3), (3, 0),  # bottom face
        (4, 5), (5, 6), (6, 7), (7, 4),  # top face
        (0, 4), (1, 5), (2, 6), (3, 7),  # verticals
    ]

    xax = grid_plane.XAxis
    yax = grid_plane.YAxis

    for idx in range(total_count):
        iz = idx % nz
        t = idx // nz
        iy = t % ny
        ix = t // ny
        lx = (ix - half_nx + 0.5) * sx
        ly = (iy - half_ny + 0.5) * sy
        lz = (iz - half_nz + 0.5) * sz
        cx = ox + xx * lx + yx * ly + zx * lz
        cy = oy + xy * lx + yy * ly + zy * lz
        cz = oz + xz * lx + yz * ly + zz * lz
        cpt = rg.Point3d(cx, cy, cz)

        # ── Breps (mode 2 or 3) ──
        if geo_mode >= 2:
            vox_pln = rg.Plane(cpt, xax, yax)
            box = rg.Box(vox_pln, x_ivl, y_ivl, z_ivl)
            brep = box.ToBrep()
            if brep:
                voxel_breps.append(brep)

        # ── Wireframe (mode 1 or 3) ──
        if geo_mode == 1 or geo_mode == 3:
            # 8 corners: -x-y-z, +x-y-z, +x+y-z, -x+y-z,
            #            -x-y+z, +x-y+z, +x+y+z, -x+y+z
            corners = [
                rg.Point3d(cpt.X + dx_neg.X + dy_neg.X + dz_neg.X,
                           cpt.Y + dx_neg.Y + dy_neg.Y + dz_neg.Y,
                           cpt.Z + dx_neg.Z + dy_neg.Z + dz_neg.Z),
                rg.Point3d(cpt.X + dx_pos.X + dy_neg.X + dz_neg.X,
                           cpt.Y + dx_pos.Y + dy_neg.Y + dz_neg.Y,
                           cpt.Z + dx_pos.Z + dy_neg.Z + dz_neg.Z),
                rg.Point3d(cpt.X + dx_pos.X + dy_pos.X + dz_neg.X,
                           cpt.Y + dx_pos.Y + dy_pos.Y + dz_neg.Y,
                           cpt.Z + dx_pos.Z + dy_pos.Z + dz_neg.Z),
                rg.Point3d(cpt.X + dx_neg.X + dy_pos.X + dz_neg.X,
                           cpt.Y + dx_neg.Y + dy_pos.Y + dz_neg.Y,
                           cpt.Z + dx_neg.Z + dy_pos.Z + dz_neg.Z),
                rg.Point3d(cpt.X + dx_neg.X + dy_neg.X + dz_pos.X,
                           cpt.Y + dx_neg.Y + dy_neg.Y + dz_pos.Y,
                           cpt.Z + dx_neg.Z + dy_neg.Z + dz_pos.Z),
                rg.Point3d(cpt.X + dx_pos.X + dy_neg.X + dz_pos.X,
                           cpt.Y + dx_pos.Y + dy_neg.Y + dz_pos.Y,
                           cpt.Z + dx_pos.Z + dy_neg.Z + dz_pos.Z),
                rg.Point3d(cpt.X + dx_pos.X + dy_pos.X + dz_pos.X,
                           cpt.Y + dx_pos.Y + dy_pos.Y + dz_pos.Y,
                           cpt.Z + dx_pos.Z + dy_pos.Z + dz_pos.Z),
                rg.Point3d(cpt.X + dx_neg.X + dy_pos.X + dz_pos.X,
                           cpt.Y + dx_neg.Y + dy_pos.Y + dz_pos.Y,
                           cpt.Z + dx_neg.Z + dy_pos.Z + dz_pos.Z),
            ]
            for a, b in edge_pairs:
                wireframe.append(rg.LineCurve(corners[a], corners[b]))

    # ── Corner lattice (deduplicated) ──
    for cix in range(nx + 1):
        clx = (cix - half_nx) * sx
        for ciy in range(ny + 1):
            cly = (ciy - half_ny) * sy
            for ciz in range(nz + 1):
                clz = (ciz - half_nz) * sz
                px = ox + xx * clx + yx * cly + zx * clz
                py = oy + xy * clx + yy * cly + zy * clz
                pz = oz + xz * clx + yz * cly + zz * clz
                corner_pts_geo.append(rg.Point3d(px, py, pz))

corner_pts = corner_pts_geo if corner_pts_geo else []

# ─── LOG ─────────────────────────────────────────────────────────────

dims_x = nx * sx
dims_y = ny * sy
dims_z = nz * sz

log_lines = [
    "Voxel Grid DB v1",
    "Grid: {} x {} x {} = {} voxels".format(nx, ny, nz, total_count),
    "Voxel size: {} x {} x {} mm".format(sx, sy, sz),
    "Total extent: {} x {} x {} mm".format(dims_x, dims_y, dims_z),
    "Geo mode: {} | Max geo: {}".format(geo_mode, max_geo),
    "JSON voxels: {} | Center pts: {}".format(
        "on" if need_voxel_json else "off (implicit layout)",
        "on" if need_center_list else "off",
    ),
]
if geo_mode > 0 and total_count > max_geo:
    log_lines.append(
        "GEO SKIPPED: {} voxels exceeds max_geo ({})".format(total_count, max_geo)
    )
if generate_geo:
    log_lines.append(
        "Breps: {} | Lines: {} | Corners: {}".format(
            len(voxel_breps), len(wireframe), len(corner_pts)
        )
    )
log_lines.append("JSON size: {} chars".format(len(grid_data)))
log = "\n".join(log_lines)
