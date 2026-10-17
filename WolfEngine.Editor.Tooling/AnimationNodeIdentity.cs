using System.Numerics;
using Silk.NET.Assimp;

namespace WolfEngine.Importing;

/// <summary>Give duplicated FBX joints stable occurrence names before extracting skins or channels.</summary>
internal static class AnimationNodeIdentity
{
    internal static unsafe void Normalize(Scene* scene)
    {
        var groups = new Dictionary<string, List<(nint Node, Matrix4x4 Global)>>(StringComparer.Ordinal);
        void Walk(nint address, Matrix4x4 parent)
        {
            var node = (Node*)address;
            var global = ThreeDFileImporter.ConvertTransform(node->MTransformation) * parent;
            var name = node->MName.AsString;
            if (!groups.TryGetValue(name, out var list)) groups.Add(name, list = []);
            list.Add((address, global));
            for (var i = 0; i < node->MNumChildren; i++) Walk((nint)node->MChildren[i], global);
        }
        Walk((nint)scene->MRootNode, Matrix4x4.Identity);
        foreach (var (name, nodes) in groups)
        {
            if (nodes.Count < 2) continue;
            for (var i = 1; i < nodes.Count; i++) ((Node*)nodes[i].Node)->MName = new AssimpString(name + "#" + (i + 1));
            for (var a = 0; a < scene->MNumAnimations; a++)
            {
                var animation = scene->MAnimations[a];
                for (var c = 0; c < animation->MNumChannels; c++)
                {
                    var channel = animation->MChannels[c];
                    if (channel->MNodeName.AsString != name) continue;
                    if (channel->MNumPositionKeys == 0) throw new InvalidOperationException($"Duplicate animation joint '{name}' has no position evidence to resolve its identity.");
                    var position = channel->MPositionKeys[0].MValue;
                    var best = -1; var distance = float.PositiveInfinity;
                    for (var i = 0; i < nodes.Count; i++)
                    {
                        var local = ThreeDFileImporter.ConvertTransform(((Node*)nodes[i].Node)->MTransformation);
                        var error = Vector3.DistanceSquared(position, local.Translation);
                        if (error < distance) { best = i; distance = error; }
                    }
                    channel->MNodeName = ((Node*)nodes[best].Node)->MName;
                }
            }
            for (var m = 0; m < scene->MNumMeshes; m++)
            {
                var mesh = scene->MMeshes[m];
                for (var b = 0; b < mesh->MNumBones; b++)
                {
                    var bone = mesh->MBones[b];
                    if (bone->MName.AsString != name) continue;
                    var offset = ThreeDFileImporter.ConvertTransform(bone->MOffsetMatrix);
                    var best = 0; var distance = float.PositiveInfinity;
                    for (var i = 0; i < nodes.Count; i++)
                    {
                        var matrix = offset * nodes[i].Global;
                        var error = Vector3.DistanceSquared(matrix.Translation, Vector3.Zero) +
                            Vector3.DistanceSquared(new(matrix.M11,matrix.M12,matrix.M13),Vector3.UnitX) +
                            Vector3.DistanceSquared(new(matrix.M21,matrix.M22,matrix.M23),Vector3.UnitY) +
                            Vector3.DistanceSquared(new(matrix.M31,matrix.M32,matrix.M33),Vector3.UnitZ);
                        if (error < distance) { best = i; distance = error; }
                    }
                    bone->MName = ((Node*)nodes[best].Node)->MName;
                }
            }
        }
    }
}
