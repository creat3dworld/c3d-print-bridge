# product_render.py — Blender 5.2 LTS
# Render a clean studio product shot of a mesh (GLB / OBJ / STL / PLY):
# transparent background, three-point area lighting, a shadow-catcher
# floor, Cycles on the GPU (OptiX on the RTX 5080) with a CPU fallback.
# Output is a PNG with alpha, ready for a listing.
#
#   D:\C3D-Tools\Blender\blender.exe --background --python product_render.py ^
#       -- --in "D:\models\dragon.glb" --out "D:\renders\dragon.png" --size 1200
#   --size : output image size in pixels (square). Default 1200.

import bpy
import math
import sys
import os
from mathutils import Vector


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:]
    args = {"in": "", "out": "", "size": 1200}
    i = 0
    while i < len(argv):
        if argv[i] in ("--in", "--out", "--size") and i + 1 < len(argv):
            args[argv[i][2:]] = argv[i + 1]
            i += 2
        else:
            i += 1
    args["size"] = int(args["size"]) if args["size"] else 1200
    if not args["in"] or not args["out"]:
        raise ValueError("Need --in <file> and --out <png>")
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


def look_at(obj, target):
    direction = Vector(target) - obj.location
    obj.rotation_euler = direction.to_track_quat('-Z', 'Y').to_euler()


def area_light(name, location, energy, size, color=(1.0, 1.0, 1.0)):
    bpy.ops.object.light_add(type='AREA', location=location)
    light = bpy.context.active_object
    light.name = name
    light.data.energy = energy
    light.data.shape = 'DISK'
    light.data.size = size
    light.data.color = color
    return light


def main():
    args = parse_args()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    import_file(args["in"])
    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    if not meshes:
        raise RuntimeError("No mesh found in " + args["in"])
    bpy.ops.object.select_all(action='DESELECT')
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

    # Centre the model on the origin, base on Z=0.
    xs = [v.co.x for v in obj.data.vertices]
    ys = [v.co.y for v in obj.data.vertices]
    zs = [v.co.z for v in obj.data.vertices]
    cx = (min(xs) + max(xs)) / 2
    cy = (min(ys) + max(ys)) / 2
    min_z = min(zs)
    obj.location = (-cx, -cy, -min_z)
    size = max(obj.dimensions)  # metres

    # Shadow-catcher floor (invisible except for the shadow it catches).
    bpy.ops.mesh.primitive_plane_add(size=size * 10, location=(0, 0, 0))
    floor = bpy.context.active_object
    floor.name = "ShadowCatcher"
    floor.is_shadow_catcher = True

    # Three-point lighting. Distances and sizes scale with the model.
    d = size
    key = area_light("Key", (d * 0.8, -d * 1.0, d * 1.2), 1500.0, d * 1.5)
    look_at(key, (0, 0, d * 0.3))
    fill = area_light("Fill", (-d * 1.0, -d * 0.4, d * 0.5), 600.0, d * 1.2)
    look_at(fill, (0, 0, d * 0.3))
    rim = area_light("Rim", (-d * 0.3, d * 1.0, d * 1.0), 1200.0, d)
    look_at(rim, (0, 0, d * 0.3))

    # Soft ambient so shadows are never pure black.
    world = bpy.data.worlds['World']
    world.use_nodes = True
    bg = world.node_tree.nodes['Background']
    bg.inputs['Color'].default_value = (1.0, 1.0, 1.0, 1.0)
    bg.inputs['Strength'].default_value = 0.3

    # Camera: three-quarter view, slightly above, 100 mm lens.
    bpy.ops.object.camera_add(location=(d * 0.9, -d * 1.1, d * 0.75))
    cam = bpy.context.active_object
    cam.name = "ProductCamera"
    cam.data.lens = 100.0
    look_at(cam, (0, 0, d * 0.35))
    bpy.context.scene.camera = cam

    # Render settings: Cycles, GPU (OptiX) with CPU fallback.
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 128
    scene.cycles.use_denoising = True
    try:
        cp = bpy.context.preferences.addons['cycles'].preferences
        cp.compute_device_type = 'OPTIX'
        cp.get_devices()
        for dev in cp.devices:
            dev.use = (dev.type == 'GPU')
        scene.cycles.device = 'GPU'
        print("RENDER DEVICE: GPU (OptiX)")
    except Exception as e:
        scene.cycles.device = 'CPU'
        print("RENDER DEVICE: CPU fallback (%s)" % e)
    scene.render.resolution_x = args["size"]
    scene.render.resolution_y = args["size"]
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.render.film_transparent = True
    scene.render.filepath = args["out"]
    bpy.ops.render.render(write_still=True)
    print("RENDERED:", args["out"])


main()
