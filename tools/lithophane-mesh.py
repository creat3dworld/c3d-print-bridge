#!/usr/bin/env python3
"""Photo -> lithophane mesh (STL + 3MF).

Deterministic, offline, no browser: a greyscale heightmap is turned
into a watertight solid panel. Bright image areas print thin (more
light gets through); dark areas print thick (less light). The panel
is exported standing up: width along X, height along Z, thickness
along Y — the orientation the slicer prints it in, no rotation
needed at slice time.

Defaults match the C3D lithophane spec (Nov 7, 2026):
  100 mm wide, 0.8 mm thinnest / 3.0 mm thickest, 3 mm frame,
  flat shape, white PLA, 0.12 mm layers, 100% infill at slice time.

Usage:
  lithophane-mesh.py --photo in.jpg --out out.3mf [--stl out.stl]
      [--width 100] [--min-thickness 0.8] [--max-thickness 3.0]
      [--frame 3] [--max-pixels 200]

Prints a one-line JSON summary to stdout; exits non-zero on failure.
Requires: numpy, Pillow, trimesh.
"""

import argparse
import json
import sys

import numpy as np
from PIL import Image


def build_mesh(photo_path, width_mm=100.0, tmin=0.8, tmax=3.0,
               frame_mm=3.0, max_pixels=200):
    """Return (vertices, faces) for the lithophane panel."""
    import trimesh

    img = Image.open(photo_path).convert("L")
    # Cap the grid so the triangle count stays sliceable: one
    # heightmap sample per ~0.5 mm of panel width.
    px_w = min(max_pixels, img.width)
    px_h = max(1, round(px_w * img.height / img.width))
    img = img.resize((px_w, px_h), Image.LANCZOS)
    gray = np.asarray(img, dtype=np.float64) / 255.0

    # Bright -> thin, dark -> thick.
    thick = tmin + (1.0 - gray) * (tmax - tmin)

    # Frame: a border of max thickness around the picture, so the
    # panel has a solid rim and a stable bottom edge to stand on.
    frame_px = max(1, round(frame_mm / width_mm * px_w))
    thick = np.pad(thick, frame_px, mode="constant",
                   constant_values=tmax)

    ny, nx = thick.shape
    panel_w = width_mm
    panel_h = width_mm * ny / nx
    xs = np.linspace(0.0, panel_w, nx)
    zs = np.linspace(0.0, panel_h, ny)

    # Front grid (y = thickness) and back grid (y = 0). Rows are
    # Z (height), columns are X (width).
    xx, zz = np.meshgrid(xs, zs)
    front = np.stack([xx, thick, zz], axis=-1).reshape(-1, 3)
    back = np.stack([xx, np.zeros_like(thick), zz], axis=-1).reshape(-1, 3)
    verts = np.vstack([front, back])
    n = nx * ny

    def quad(a, b, c, d):
        return [(a, b, c), (a, c, d)]

    faces = []
    # Front faces (+Y outward): viewed from +Y, counter-clockwise.
    for r in range(ny - 1):
        for c in range(nx - 1):
            a = r * nx + c
            b = a + 1
            cc = a + nx
            d = cc + 1
            faces.extend(quad(a, cc, d, b))
    # Back faces (-Y outward): reverse winding, offset by n.
    for r in range(ny - 1):
        for c in range(nx - 1):
            a = n + r * nx + c
            b = a + 1
            cc = a + nx
            d = cc + 1
            faces.extend(quad(a, b, d, cc))
    # Sides: bottom, top, left, right strips joining front to back.
    for c in range(nx - 1):
        # Bottom edge (row 0): front c -> front c+1, back c+1 -> back c.
        faces.extend(quad(c, c + 1, n + c + 1, n + c))
        # Top edge (row ny-1).
        t = (ny - 1) * nx
        faces.extend(quad(t + c + 1, t + c, n + t + c, n + t + c + 1))
    for r in range(ny - 1):
        # Left edge (col 0).
        a = r * nx
        faces.extend(quad(a + nx, a, n + a, n + a + nx))
        # Right edge (col nx-1).
        a = r * nx + nx - 1
        faces.extend(quad(a, a + nx, n + a + nx, n + a))

    mesh = trimesh.Trimesh(vertices=verts,
                           faces=np.array(faces, dtype=np.int64),
                           process=True)
    return mesh, panel_w, panel_h


def write_stl_binary(mesh, path):
    """Binary STL, written by hand (no exporter dependency)."""
    import struct
    faces = np.asarray(mesh.faces)
    verts = np.asarray(mesh.vertices)
    tris = verts[faces]  # (n, 3, 3)
    # Per-triangle normals from the cross product.
    u = tris[:, 1] - tris[:, 0]
    v = tris[:, 2] - tris[:, 0]
    n = np.cross(u, v)
    ln = np.linalg.norm(n, axis=1, keepdims=True)
    ln[ln == 0] = 1.0
    n = n / ln
    with open(path, "wb") as fh:
        fh.write(b"\0" * 80)
        fh.write(struct.pack("<I", len(tris)))
        for i in range(len(tris)):
            fh.write(struct.pack("<12fH",
                                 float(n[i][0]), float(n[i][1]), float(n[i][2]),
                                 float(tris[i][0][0]), float(tris[i][0][1]), float(tris[i][0][2]),
                                 float(tris[i][1][0]), float(tris[i][1][1]), float(tris[i][1][2]),
                                 float(tris[i][2][0]), float(tris[i][2][1]), float(tris[i][2][2]),
                                 0))


def write_three_mf(mesh, path):
    """3MF (Core spec), written by hand: a ZIP with the model XML.

    No exporter dependency — the shop PC only needs numpy, Pillow
    and trimesh (for the watertight check), not a 3MF library.
    Units are millimetres; the mesh is one model object."""
    import zipfile
    import xml.etree.ElementTree as ET

    ns = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02"
    ET.register_namespace("", ns)
    model = ET.Element("{%s}model" % ns,
                       {"unit": "millimeter",
                        "xml:lang": "en-US"})
    resources = ET.SubElement(model, "{%s}resources" % ns)
    obj = ET.SubElement(resources, "{%s}object" % ns,
                        {"id": "1", "type": "model"})
    mesh_el = ET.SubElement(obj, "{%s}mesh" % ns)
    verts_el = ET.SubElement(mesh_el, "{%s}vertices" % ns)
    for v in np.asarray(mesh.vertices):
        ET.SubElement(verts_el, "{%s}vertex" % ns,
                      {"x": repr(float(v[0])),
                       "y": repr(float(v[1])),
                       "z": repr(float(v[2]))})
    tris_el = ET.SubElement(mesh_el, "{%s}triangles" % ns)
    for f in np.asarray(mesh.faces):
        ET.SubElement(tris_el, "{%s}triangle" % ns,
                      {"v1": str(int(f[0])),
                       "v2": str(int(f[1])),
                       "v3": str(int(f[2]))})
    build = ET.SubElement(model, "{%s}build" % ns)
    ET.SubElement(build, "{%s}item" % ns, {"objectid": "1"})
    model_xml = ET.tostring(
        model, encoding="UTF-8", xml_declaration=True)

    content_types = (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<Types xmlns="http://schemas.openxmlformats.org/'
        'package/2006/content-types">'
        '<Default Extension="rels" ContentType="application/vnd.'
        'openxmlformats-package.relationships+xml"/>'
        '<Default Extension="model" ContentType="application/vnd.'
        'ms-package.3dmanufacturing-3dmodel+xml"/>'
        '</Types>').encode("UTF-8")
    rels = (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<Relationships xmlns="http://schemas.openxmlformats.org/'
        'package/2006/relationships">'
        '<Relationship Type="http://schemas.microsoft.com/'
        '3dmanufacturing/2013/01/3dmodel" Target="/3D/3dmodel.model"'
        ' Id="rel0"/>'
        '</Relationships>').encode("UTF-8")

    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("[Content_Types].xml", content_types)
        zf.writestr("_rels/.rels", rels)
        zf.writestr("3D/3dmodel.model", model_xml)


def read_three_mf_counts(path):
    """Independent read-back of a 3MF: vertex/triangle counts."""
    import zipfile
    import xml.etree.ElementTree as ET
    with zipfile.ZipFile(path) as zf:
        names = zf.namelist()
        assert "3D/3dmodel.model" in names, "no 3dmodel.model in 3MF"
        root = ET.fromstring(zf.read("3D/3dmodel.model"))
    ns = {"m": "http://schemas.microsoft.com/3dmanufacturing/core/2015/02"}
    verts = root.findall(".//m:vertex", ns)
    tris = root.findall(".//m:triangle", ns)
    unit = root.get("unit")
    return len(verts), len(tris), unit


def main():
    ap = argparse.ArgumentParser(description="Photo to lithophane mesh")
    ap.add_argument("--photo", required=True)
    ap.add_argument("--out", required=True, help="Output 3MF path")
    ap.add_argument("--stl", default="", help="Optional STL output path")
    ap.add_argument("--width", type=float, default=100.0)
    ap.add_argument("--min-thickness", type=float, default=0.8)
    ap.add_argument("--max-thickness", type=float, default=3.0)
    ap.add_argument("--frame", type=float, default=3.0)
    ap.add_argument("--max-pixels", type=int, default=200)
    args = ap.parse_args()

    try:
        mesh, panel_w, panel_h = build_mesh(
            args.photo, args.width, args.min_thickness,
            args.max_thickness, args.frame, args.max_pixels)
    except Exception as exc:  # noqa: BLE001 - report and fail
        print(json.dumps({"ok": False, "error": str(exc)}))
        return 1

    if not mesh.is_watertight:
        print(json.dumps({"ok": False,
                          "error": "mesh is not watertight"}))
        return 1
    if not mesh.is_winding_consistent:
        try:
            mesh.fix_normals()
        except Exception:  # noqa: BLE001
            pass
        if not mesh.is_winding_consistent:
            print(json.dumps({"ok": False,
                              "error": "winding is inconsistent"}))
            return 1

    try:
        write_three_mf(mesh, args.out)
        nv, nt, unit = read_three_mf_counts(args.out)
        if nv != len(mesh.vertices) or nt != len(mesh.faces):
            raise ValueError("3MF read-back count mismatch")
        if unit != "millimeter":
            raise ValueError("3MF unit is not millimeter")
    except Exception as exc:  # noqa: BLE001
        print(json.dumps({"ok": False,
                          "error": "3MF write failed: " + str(exc)}))
        return 1

    stl_bytes = 0
    if args.stl:
        try:
            write_stl_binary(mesh, args.stl)
            import os
            stl_bytes = os.path.getsize(args.stl)
        except Exception as exc:  # noqa: BLE001
            print(json.dumps({"ok": False,
                              "error": "STL write failed: " + str(exc)}))
            return 1

    import os
    bounds = mesh.bounds
    print(json.dumps({
        "ok": True,
        "triangles": int(len(mesh.faces)),
        "watertight": bool(mesh.is_watertight),
        "volume_mm3": round(float(mesh.volume), 1),
        "size_x_mm": round(float(bounds[1][0] - bounds[0][0]), 2),
        "size_y_mm": round(float(bounds[1][1] - bounds[0][1]), 2),
        "size_z_mm": round(float(bounds[1][2] - bounds[0][2]), 2),
        "panel_w_mm": round(panel_w, 2),
        "panel_h_mm": round(panel_h, 2),
        "three_mf_bytes": os.path.getsize(args.out),
        "stl_bytes": stl_bytes,
    }))
    return 0


if __name__ == "__main__":
    sys.exit(main())
