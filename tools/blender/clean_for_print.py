# clean_for_print.py — Blender 5.2 LTS
# Take a raw AI-generated mesh (GLB / OBJ / STL / PLY) and turn it into a
# print-ready STL: join parts, merge duplicates, fix normals, scale to a
# target height, optionally decimate, set the origin to the bottom centre
# so the base sits flat on the build plate, and export binary STL.
#
# Run from 3d1PC (Blender installed at D:\C3D-Tools\Blender\):
#   D:\C3D-Tools\Blender\blender.exe --background --python clean_for_print.py ^
#       -- --in "D:\models\dragon.glb" --out "D:\models\dragon-print.stl" ^
#       --height 120 --ratio 0.3
#   --height : target height in millimetres (0 = keep the imported size)
#   --ratio  : Decimate Collapse ratio 0-1 (0 = skip decimation)
# Exit code: use --python-exit-code 1 on the command line so a script
# error fails the command instead of silently succeeding.

import bpy
import bmesh
import sys
import os
from mathutils import Matrix


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
        raise ValueError("Need --in <file> and --out <file>")
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


def main():
    args = parse_args()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    import_file(args["in"])

    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    if not meshes:
        raise RuntimeError("No mesh found in " + args["in"])

    # Join every mesh part into one object.
    bpy.ops.object.select_all(action='DESELECT')
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active

    # Bake rotation and scale into the mesh data.
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

    # --- bmesh cleanup -------------------------------------------------
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

    # --- scale to target height (given in mm; Blender works in metres) --
    if args["height"] > 0:
        tallest = max(obj.dimensions)
        if tallest > 0:
            s = (args["height"] / 1000.0) / tallest
            obj.scale = (s, s, s)
            bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)

    # --- decimate (optional) -------------------------------------------
    if 0 < args["ratio"] < 1:
        mod = obj.modifiers.new("Decimate", 'DECIMATE')
        mod.decimate_type = 'COLLAPSE'
        mod.ratio = args["ratio"]
        bpy.ops.object.modifier_apply(modifier=mod.name)

    # --- origin to bottom centre, base on Z=0 ---------------------------
    xs = [v.co.x for v in obj.data.vertices]
    ys = [v.co.y for v in obj.data.vertices]
    zs = [v.co.z for v in obj.data.vertices]
    cx = (min(xs) + max(xs)) / 2
    cy = (min(ys) + max(ys)) / 2
    min_z = min(zs)
    obj.data.transform(Matrix.Translation((-cx, -cy, -min_z)))

    # --- stats + export --------------------------------------------------
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    nonman = sum(1 for e in bm.edges if not e.is_manifold)
    vol = abs(bm.calc_volume())
    faces = len(bm.faces)
    bm.free()
    height_mm = (max(zs) - min_z) * 1000.0
    print("CLEAN: faces=%d non_manifold_edges=%d volume_cm3=%.2f height_mm=%.1f"
          % (faces, nonman, vol * 1e6, height_mm))
    if nonman:
        print("WARNING: mesh is still non-manifold — open it in Blender and "
              "run the 3D Print Toolbox 'Make Manifold' before slicing.")

    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.wm.stl_export(filepath=args["out"], ascii=False, use_selection=True)
    print("EXPORTED:", args["out"])


main()
