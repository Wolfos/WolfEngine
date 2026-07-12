import argparse
import bpy
import os
import sys


def parse_arguments():
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Export a deterministic Unity FBX for Wolfie")
    parser.add_argument("--output", required=True)
    return parser.parse_args(arguments)


def main():
    args = parse_arguments()
    output = os.path.abspath(args.output)
    os.makedirs(os.path.dirname(output), exist_ok=True)

    result = bpy.ops.export_scene.fbx(
        filepath=output,
        check_existing=False,
        use_selection=False,
        use_visible=False,
        use_active_collection=False,
        object_types={'EMPTY', 'ARMATURE', 'MESH'},
        use_mesh_modifiers=True,
        use_mesh_modifiers_render=True,
        mesh_smooth_type='OFF',
        colors_type='SRGB',
        use_subsurf=False,
        use_mesh_edges=False,
        use_tspace=True,
        use_custom_props=False,
        add_leaf_bones=False,
        primary_bone_axis='Y',
        secondary_bone_axis='X',
        use_armature_deform_only=False,
        armature_nodetype='NULL',
        bake_anim=False,
        path_mode='AUTO',
        embed_textures=False,
        batch_mode='OFF',
        use_batch_own_dir=True,
        axis_forward='-Z',
        axis_up='Y',
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options='FBX_SCALE_UNITS',
        use_space_transform=True,
        bake_space_transform=False,
    )
    if 'FINISHED' not in result or not os.path.isfile(output):
        raise RuntimeError("Blender FBX export did not finish successfully")


if __name__ == "__main__":
    main()
