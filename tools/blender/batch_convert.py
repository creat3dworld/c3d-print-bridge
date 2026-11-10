# batch_convert.py — Blender 5.2 LTS
# Convert every .glb / .obj / .stl / .ply in a folder to a cleaned,
# print-ready STL in an output folder. Same cleanup as clean_for_print.py
# (join, merge by distance, degenerate dissolve, delete loose, recalculate
# normals, optional scale-to-height and decimate), run once per file.
# Nothing is bought, uploaded or overwritten in the input folder.
#
#   D:\C3D-Tools\Blender\blender.exe --background --python batch_convert.py ^
#       -- --in "D:\models\raw" --out "D:\models\print" --height 120 --ratio 0.3

import bpy
import bmesh
import sys
import os
from mathutils import Matrix

FORMATS = (".glb", ".gltf", ".obj", ".stl", ".ply")


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:]
    args = {"in": "", "out": "", "height": 0.0, "ratio": 0.0}
    i = 0
    while i < len(argv):
        if argv[i] in ("--in", "--out", "--height", "--ratio") and i + 1 < len(argv):
            args[argv[i][2:]] = argv[i + 1]
            i += 2
        else:
            i += 1
    args["height"] = float(args["height"]) if args["height"] else 0.0
    args["ratio"] = float(args["ratio"]) if args["ratio"] else 0.0
    if not args["in"] or not args["out"]:
        raise ValueError("Need --in <folder> and --out <folder>")
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


def clean_and_export(obj, out_path, height_mm, ratio):
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0001)
    bmesh.ops.dissolve_degenerate(bm, edges=bm.edges, dist=0.0001)
    bmesh.ops.delete_loose(bm)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    me.update()
    if height_mm > 0:
        tallest = max(obj.dimensions)
        if tallest > 0:
            s = (height_mm / 1000.0) / tallest
            obj.scale = (s, s, s)
            bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if 0 < ratio < 1:
        mod = obj.modifiers.new("Decimate", 'DECIMATE')
        mod.decimate_type = 'COLLAPSE'
        mod.ratio = ratio
        bpy.ops.object.modifier_apply(modifier=mod.name)
    xs = [v.co.x for v in obj.data.vertices]
    ys = [v.co.y for v in obj.data.vertices]
    zs = [v.co.z for v in obj.data.vertices]
    cx = (min(xs) + max(xs)) / 2
    cy = (min(ys) + max(ys)) / 2
    obj.data.transform(Matrix.Translation((-cx, -cy, -min(zs))))
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    nonman = sum(1 for e in bm.edges if not e.is_manifold)
    faces = len(bm.faces)
    bm.free()
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.wm.stl_export(filepath=out_path, ascii=False, use_selection=True)
    return faces, nonman


def main():
    args = parse_args()
    os.makedirs(args["out"], exist_ok=True)
    files = sorted(f for f in os.listdir(args["in"])
                   if os.path.splitext(f)[1].lower() in FORMATS)
    print("BATCH: %d files in %s" % (len(files), args["in"]))
    for fname in files:
        src = os.path.join(args["in"], fname)
        dst = os.path.join(args["out"], os.path.splitext(fname)[0] + ".stl")
        bpy.ops.wm.read_factory_settings(use_empty=True)
        try:
            import_file(src)
        except Exception as e:
            print("SKIP %s: import failed: %s" % (fname, e))
            continue
        meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
        if not meshes:
            print("SKIP %s: no mesh" % fname)
            continue
        bpy.ops.object.select_all(action='DESELECT')
        for o in meshes:
            o.select_set(True)
        bpy.context.view_layer.objects.active = meshes[0]
        if len(meshes) > 1:
            bpy.ops.object.join()
        obj = bpy.context.view_layer.objects.active
        try:
            faces, nonman = clean_and_export(obj, dst, args["height"], args["ratio"])
        except Exception as e:
            print("FAIL %s: %s" % (fname, e))
            continue
        flag = "  <-- STILL NON-MANIFOLD, check in Blender" if nonman else ""
        print("OK %s -> %s  faces=%d non_manifold=%d%s" %
              (fname, os.path.basename(dst), faces, nonman, flag))


main()
