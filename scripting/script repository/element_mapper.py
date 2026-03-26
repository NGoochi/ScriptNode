#! python 3
# NODE_INPUTS: site_def:str, target_surfaces:list[Brep], element_width:float | "Element width mm" min=50 max=2000 step=10 decimals=0 default=250, element_height:float | "Element height mm" min=10 max=500 step=5 decimals=0 default=80, element_depth:float | "Element depth mm" min=10 max=500 step=5 decimals=0 default=80, mapping_density:float | "Elements per face" min=1 max=200 step=1 decimals=0 default=8, mapping_method:str, attractor_points:list[Point3d], attractor_mode:str, attractor_falloff:float | "Attractor falloff mm" min=100 max=50000 step=100 decimals=0 default=8000, scale_min:float | "Min scale factor" min=0.1 max=2.0 step=0.05 decimals=2 default=0.7, scale_max:float | "Max scale factor" min=0.1 max=3.0 step=0.05 decimals=2 default=1.5, normal_offset:float | "Normal offset mm" min=-1000 max=1000 step=10 decimals=0 default=0
# NODE_OUTPUTS: elements, element_count, site_def, log
#
# element_mapper — map box elements onto any Brep face set.
# element_width/height/depth control the base box size; attractor_points
# modulate per-element scale between scale_min and scale_max.

import math
import random
import Rhino.Geometry as rg


def _f(v, d):
    try:
        return float(v)
    except Exception:
        return float(d)


def _clamp(v, lo, hi):
    return max(lo, min(hi, v))


def _nearest_dist(pt, attractors):
    if not attractors:
        return None
    dmin = None
    for a in attractors:
        d = pt.DistanceTo(a)
        if dmin is None or d < dmin:
            dmin = d
    return dmin


def _point_field_samples(face, target_count):
    udom = face.Domain(0)
    vdom = face.Domain(1)
    rng = random.Random(1337)
    pts = []
    trials = max(200, target_count * 20)
    for _ in range(trials):
        u = rng.uniform(udom.T0, udom.T1)
        v = rng.uniform(vdom.T0, vdom.T1)
        if face.IsPointOnFace(u, v) != rg.PointFaceRelation.Exterior:
            pts.append((u, v))
            if len(pts) >= target_count:
                break
    return pts


try: element_width
except NameError: element_width = None
try: element_height
except NameError: element_height = None
try: element_depth
except NameError: element_depth = None
try: scale_min
except NameError: scale_min = None
try: scale_max
except NameError: scale_max = None

elements = []
element_count = 0
log = ""

if site_def is None or not str(site_def).strip():
    site_def = "{}"
    log = "Warning: site_def empty; continuing."

base_w = max(10.0, _f(element_width, 250.0))
base_h = max(10.0, _f(element_height, 80.0))
base_d = max(10.0, _f(element_depth, 80.0))

dens = max(1.0, _f(mapping_density, 8.0))
method = str(mapping_method).strip().lower() if mapping_method is not None else "point_field"
if method not in ("uv_grid", "point_field"):
    method = "point_field"

atr_mode = str(attractor_mode).strip().lower() if attractor_mode is not None else "grow"
if atr_mode not in ("grow", "shrink"):
    atr_mode = "grow"

falloff = max(1.0, _f(attractor_falloff, 8000.0))
min_s = _clamp(_f(scale_min, 0.7), 0.1, 5.0)
max_s = _clamp(_f(scale_max, 1.5), 0.1, 5.0)
if max_s < min_s:
    min_s, max_s = max_s, min_s
normal_off = _f(normal_offset, 0.0)

attractors = []
if attractor_points is not None:
    try:
        for ap in attractor_points:
            if ap is not None:
                attractors.append(ap)
    except TypeError:
        attractors.append(attractor_points)

breps = []
if target_surfaces is not None:
    try:
        for b in target_surfaces:
            if b is not None:
                breps.append(b)
    except TypeError:
        breps.append(target_surfaces)

for bi, brep in enumerate(breps):
    if brep is None:
        continue
    for fi, face in enumerate(brep.Faces):
        if face is None:
            continue

        udom = face.Domain(0)
        vdom = face.Domain(1)
        ulen = max(1e-9, udom.Length)
        vlen = max(1e-9, vdom.Length)
        approx = max(1, int(round(dens)))

        uv_samples = []
        if method == "uv_grid":
            u_count = max(1, int(round(math.sqrt(approx * (ulen / vlen)))))
            v_count = max(1, int(round(float(approx) / float(max(1, u_count)))))
            for ui in range(u_count):
                for vi in range(v_count):
                    u = udom.T0 + ulen * ((ui + 0.5) / float(u_count))
                    v = vdom.T0 + vlen * ((vi + 0.5) / float(v_count))
                    if face.IsPointOnFace(u, v) != rg.PointFaceRelation.Exterior:
                        uv_samples.append((u, v))
        else:
            uv_samples = _point_field_samples(face, max(4, approx * 3))

        for (u, v) in uv_samples:
            ok, frame = face.FrameAt(u, v)
            if not ok:
                continue

            pt = frame.Origin + frame.ZAxis * normal_off
            nearest = _nearest_dist(pt, attractors)
            t = 0.0
            if nearest is not None:
                t = max(0.0, min(1.0, 1.0 - (nearest / falloff)))
            if atr_mode == "shrink":
                t = 1.0 - t
            scale = min_s + (max_s - min_s) * t

            w = base_w * scale
            h = base_h * scale
            d = base_d * scale

            xf = rg.Plane(frame)
            xf.Origin = pt
            box = rg.Box(
                xf,
                rg.Interval(-w * 0.5, w * 0.5),
                rg.Interval(-d * 0.5, d * 0.5),
                rg.Interval(0.0, h),
            )
            b = box.ToBrep()
            if b is not None:
                elements.append(b)

element_count = len(elements)
log = "element_mapper | method: {} | surfaces: {} | elements: {}".format(
    method, len(breps), element_count
)
