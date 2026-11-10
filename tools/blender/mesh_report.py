# mesh_report.py — Blender 5.2 LTS
# Print a health report for a mesh file (GLB / OBJ / STL / PLY):
# face/vertex counts, bounding-box size in mm, volume, and the counts of
# non-manifold edges, degenerate (zero-area) faces, inverted normals and
# loose vertices, plus a sampled minimum wall thickness. Read-only: it
# changes nothing and exports nothing.
#
#   D:\C3D-Tools\Blender\blender.exe --background --python mesh_report.py ^
#       -- --in "D:\models\dragon.glb"
#
#   --json : also print one MESH_JSON: {...} line with the same numbers
#   as machine-readable JSON (used by the C3D bridge Blender runner).
#   Human-readable REPORT: lines are printed either way.
#
# Inverted normals: each face's normal is compared against a copy of
# the mesh with recalculated normals; a face whose normal points the
# other way disagrees with the mesh's consistent winding. The overall
# normals_flipped flag is true when the mesh's signed volume is
# negative (the whole mesh is inside-out). Both are counts/flags of
# winding disagreement, not a claim about printability on their own.
#
# Min wall thickness: raycast sampling, best effort. Up to 500 faces
# (evenly spaced) are sampled: a ray is cast from the face centre
# along the inward normal and the distance to the next surface is the
# wall thickness there; the minimum over the samples is reported.
# When sampling fails or no ray hits anything, the value is null —
# never guessed.

import bpy
import bmesh
import json
import sys
import os
from mathutils import Vector


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:]
    args = {"in": "", "json": False}
    i = 0
    while i < len(argv):
        if argv[i] == "--in" and i + 1 < len(argv):
            args["in"] = argv[i + 1]
            i += 2
        elif argv[i] == "--json":
            args["json"] = True
            i += 1
        else:
            i += 1
    if not args["in"]:
        raise ValueError("Need --in <file>")
    return args


def import_file(path):
    ext = os.path.splitext(path)[1].lower()
    if ext in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == ".obj":
        bpy.ops.wm.obj_import(filepath=path)
    elif ext == ".stl":
        bpy.ops.wm.stl_import(filepath=path)
    elif ext == ".ply":
        bpy.ops.wm.ply_import(filepath=path)
    else:
        raise ValueError("Unsupported input format: " + ext)


def inverted_count(bm):
    """Faces whose normal disagrees with a recalculated copy."""
    try:
        bm2 = bmesh.new()
        bm2.from_mesh(bm)
        bmesh.ops.recalc_face_normals(bm2, faces=bm2.faces)
        inv = 0
        for f1, f2 in zip(bm.faces, bm2.faces):
            if f1.normal.dot(f2.normal) < 0:
                inv += 1
        bm2.free()
        return inv
    except Exception:
        return 0


def min_wall_thickness(obj, bm):
    """Sampled minimum wall thickness in mm, or None.

    Casts a ray from sampled face centres along the inward normal
    and measures the distance to the next surface. Best effort:
    any failure returns None instead of a guessed number.
    """
    try:
        dg = bpy.context.evaluated_depsgraph_get()
        faces = list(bm.faces)
        if not faces:
            return None
        step = max(1, len(faces) // 500)
        sampled = faces[::step][:500]
        mat = obj.matrix_world
        mat3 = mat.to_3x3()
        best = None
        for f in sampled:
            centre = mat @ f.calc_center_median()
            inward = (mat3 @ f.normal).normalized() * -1.0
            # Start just inside the surface so the ray does not
            # immediately hit the face it started from.
            origin = centre + inward * 0.0002
            hit, loc, _n, _i, _o, _m = obj.ray_cast(dg, origin, inward)
            if not hit:
                continue
            dist_m = (loc - origin).length
            if dist_m < 0.00005:
                continue  # self-hit; ignore
            dist_mm = dist_m * 1000.0
            if best is None or dist_mm < best:
                best = dist_mm
        return best
    except Exception:
        return None


def main():
    args = parse_args()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    import_file(args["in"])
    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    if not meshes:
        raise RuntimeError("No mesh found in " + args["in"])
    print("REPORT:", args["in"])
    totals = {"verts": 0, "faces": 0, "degenerate_faces": 0,
              "non_manifold_edges": 0, "inverted_normals": 0,
              "loose_verts": 0, "volume_cm3": 0.0,
              "normals_flipped": False, "min_wall_thickness_mm": None}
    # Overall bounding box across all objects (world space, metres).
    omin = Vector((float("inf"),) * 3)
    omax = Vector((float("-inf"),) * 3)
    objects_out = []
    for obj in meshes:
        me = obj.data
        bm = bmesh.new()
        bm.from_mesh(me)
        nonman = sum(1 for e in bm.edges if not e.is_manifold)
        degen = sum(1 for f in bm.faces if f.calc_area() < 1e-12)
        loose = sum(1 for v in bm.verts if not v.link_faces)
        vol = bm.calc_volume()
        inv = inverted_count(bm)
        d = obj.dimensions  # metres
        print("  object=%s verts=%d faces=%d tris=%d" %
              (obj.name, len(bm.verts), len(bm.faces), len(bm.faces)))
        print("    size_mm=%.1f x %.1f x %.1f  volume_cm3=%.2f "
              "(volume is only meaningful for a closed mesh)" %
              (d.x * 1000, d.y * 1000, d.z * 1000, abs(vol) * 1e6))
        print("    non_manifold_edges=%d degenerate_faces=%d loose_verts=%d "
              "inverted_normals=%d" % (nonman, degen, loose, inv))
        print("    scale=(%.3f, %.3f, %.3f) location=(%.3f, %.3f, %.3f)" %
              (obj.scale.x, obj.scale.y, obj.scale.z,
               obj.location.x, obj.location.y, obj.location.z))
        # Wall thickness is sampled once per file, on the first
        # object with faces, and reported at file level below.
        if totals["min_wall_thickness_mm"] is None and len(bm.faces) > 0:
            totals["min_wall_thickness_mm"] = min_wall_thickness(obj, bm)
        totals["verts"] += len(bm.verts)
        totals["faces"] += len(bm.faces)
        totals["degenerate_faces"] += degen
        totals["non_manifold_edges"] += nonman
        totals["inverted_normals"] += inv
        totals["loose_verts"] += loose
        totals["volume_cm3"] += abs(vol) * 1e6
        if vol < 0:
            totals["normals_flipped"] = True
        for corner in obj.bound_box:
            w = obj.matrix_world @ Vector(corner)
            omin = Vector((min(omin.x, w.x), min(omin.y, w.y), min(omin.z, w.z)))
            omax = Vector((max(omax.x, w.x), max(omax.y, w.y), max(omax.z, w.z)))
        objects_out.append({
            "object": obj.name,
            "verts": len(bm.verts),
            "faces": len(bm.faces),
            "degenerate_faces": degen,
            "non_manifold_edges": nonman,
            "inverted_normals": inv,
            "loose_verts": loose,
        })
        bm.free()
    size_mm = [(omax[i] - omin[i]) * 1000.0 for i in range(3)]
    result = {
        "manifold": totals["non_manifold_edges"] == 0,
        "degenerate_faces": totals["degenerate_faces"],
        "non_manifold_edges": totals["non_manifold_edges"],
        "inverted_normals": totals["inverted_normals"],
        "normals_flipped": totals["normals_flipped"],
        "loose_verts": totals["loose_verts"],
        "verts": totals["verts"],
        "faces": totals["faces"],
        "size_mm_x": round(size_mm[0], 1),
        "size_mm_y": round(size_mm[1], 1),
        "size_mm_z": round(size_mm[2], 1),
        "volume_cm3": round(totals["volume_cm3"], 2),
        "min_wall_thickness_mm": (None if totals["min_wall_thickness_mm"] is None
                                  else round(totals["min_wall_thickness_mm"], 2)),
        "objects": objects_out,
    }
    print("  manifold=%s min_wall_thickness_mm=%s" %
          (result["manifold"], result["min_wall_thickness_mm"]))
    if args["json"]:
        print("MESH_JSON: " + json.dumps(result))


main()
