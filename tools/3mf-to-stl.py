#!/usr/bin/env python3
"""3MF -> STL converter for the C3D bridge's Snapmaker Orca slice path.

Snapmaker Orca's CLI (2.4.1) crashes on 3MF input carrying plate data
(upstream issue #935: exit -1073741819), while STL input slices fine
(proven live Nov 8, 2026: mount STL exit 0, mount 3MF exit
-1073741819). The bridge therefore feeds STL to Snapmaker Orca and
keeps 3MF for Bambu Studio / OrcaSlicer, which take 3MF fine.

Usage:
  3mf-to-stl.py --in in.3mf --out out.stl

Prints a one-line JSON summary to stdout; exits non-zero on failure.
Requires: trimesh (numpy comes with it).

trimesh applies the 3MF build-item transforms and merges every object
in the file into one scene, so multi-part / multi-copy plates keep
their positions; the STL carries all triangles in model coordinates.
Units: 3MF is millimetres by default and trimesh preserves the file's
units; STL is unitless and the slicer reads it as millimetres, the
same as the 3MF it came from. Watertightness is NOT required — the
source 3MF may itself be non-watertight and the slicer handles that;
the only hard gates are "at least one triangle" and "output written".
"""

import argparse
import json
import sys


def main():
    parser = argparse.ArgumentParser(description="Convert a 3MF model to STL.")
    parser.add_argument("--in", dest="in_path", required=True,
                        help="input 3MF file")
    parser.add_argument("--out", dest="out_path", required=True,
                        help="output STL file")
    args = parser.parse_args()

    try:
        import trimesh
    except ImportError as exc:
        print(json.dumps({"ok": False,
                          "error": "trimesh is not installed: %s" % exc}),
              file=sys.stderr)
        return 2

    try:
        loaded = trimesh.load(args.in_path, force="scene")
    except Exception as exc:
        print(json.dumps({"ok": False,
                          "error": "could not load 3MF: %s" % exc}),
              file=sys.stderr)
        return 2

    # A 3MF with one object loads as a Trimesh; anything else
    # loads as a Scene. Either way, collapse to one merged mesh
    # so the STL carries every part at its placed position.
    if isinstance(loaded, trimesh.Scene):
        if len(loaded.geometry) == 0:
            print(json.dumps({"ok": False,
                              "error": "3MF contains no geometry"}),
                  file=sys.stderr)
            return 2
        mesh = loaded.to_geometry()
    else:
        mesh = loaded
    if not isinstance(mesh, trimesh.Trimesh) or len(mesh.faces) == 0:
        print(json.dumps({"ok": False,
                          "error": "3MF contains no triangles"}),
              file=sys.stderr)
        return 2

    try:
        mesh.export(args.out_path, file_type="stl")
    except Exception as exc:
        print(json.dumps({"ok": False,
                          "error": "could not write STL: %s" % exc}),
              file=sys.stderr)
        return 2

    import os
    bounds = mesh.bounds.tolist()
    print(json.dumps({"ok": True,
                      "triangles": int(len(mesh.faces)),
                      "bounds_mm": bounds,
                      "bytes": os.path.getsize(args.out_path)}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
