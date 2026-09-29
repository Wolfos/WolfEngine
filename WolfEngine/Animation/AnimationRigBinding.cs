using System.Numerics;
using WolfEngine.Importing;

namespace WolfEngine.Animation;

/// <summary>Bind animation-only source nodes to an explicitly chosen rig, without retargeting.</summary>
public static class AnimationRigBinding
{
    public static ImportedScene Bind(ImportedScene scene, ImportedSkeleton target, IReadOnlySet<string>? ignoredChannels = null)
    {
        var nodes = new Dictionary<string, ImportedNode>(StringComparer.Ordinal);
        foreach (var node in scene.Nodes)
            if (!nodes.TryAdd(node.Name, node)) throw new InvalidOperationException($"Ambiguous animation node '{node.Name}'.");
        var targetIndices = target.BoneNames.Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
        var rest = (BoneTransform[])target.BindPoseLocal.Clone();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var animation in scene.Animations)
            foreach (var track in animation.TransformTracks)
                if (targetIndices.ContainsKey(track.Binding.Path)) matched.Add(track.Binding.Path);
        if (matched.Count == 0) throw new InvalidOperationException($"No animation channels match rig '{target.Name}'. Check AnimationSkeletonId.");
        foreach (var name in matched)
        {
            if (!nodes.TryGetValue(name, out var source)) throw new InvalidOperationException($"Source rest transform for '{name}' is missing.");
            var index = targetIndices[name];
            var value = BoneTransform.FromMatrix(source.LocalTransform);
            var expected = target.BindPoseLocal[index];
            // Animation-only exports store their first animated pose in node transforms, not a
            // bind pose. Compare invariant joint offsets/units; rotations and animated root
            // translation cannot be used as rest-pose evidence. The target remains authoritative.
            var tracks = scene.Animations.SelectMany(a => a.TransformTracks).Where(t => t.Binding.Path == name).ToArray();
            var animatedTranslation = tracks.Any(t => t.Position is { Times.Length: > 1 });
            var animatedRotation = tracks.Any(t => t.Rotation is { Times.Length: > 0 });
            var animatedScale = tracks.Any(t => t.Scale.Values.Length > 1 && t.Scale.Values.Any(v => Vector3.DistanceSquared(v, t.Scale.Values[0]) > 1e-8f));
            if ((!animatedScale && Vector3.Distance(value.Scale, expected.Scale) > 0.005f) ||
                (!animatedRotation && 1 - MathF.Abs(Quaternion.Dot(value.Rotation, expected.Rotation)) > 0.005f) ||
                (!animatedTranslation && Vector3.Distance(value.Position, expected.Position) > Math.Max(0.01f, expected.Position.Length() * 0.02f)))
                throw new InvalidOperationException($"Rest transform for '{name}' differs from rig '{target.Name}'; check source units, scale and rig. Retargeting is not supported.");
            var parent = source.ParentIndex;
            while (parent >= 0 && !matched.Contains(scene.Nodes[parent].Name)) parent = scene.Nodes[parent].ParentIndex;
            var targetParent = target.ParentIndices[index];
            while (targetParent >= 0 && !matched.Contains(target.BoneNames[targetParent])) targetParent = target.ParentIndices[targetParent];
            if ((parent >= 0 ? scene.Nodes[parent].Name : null) != (targetParent >= 0 ? target.BoneNames[targetParent] : null))
                throw new InvalidOperationException($"Hierarchy mismatch at '{name}'.");

        }
        var skeletonIndex = scene.Skeletons.Count;
        var skeletons = new List<ImportedSkeleton>(scene.Skeletons) { target with { BindPoseLocal = rest } };
        var animations = scene.Animations.Select(animation => animation with
        {
            SkeletonIndex = skeletonIndex,
            TransformTracks = animation.TransformTracks.Where(track => ignoredChannels?.Contains(track.Binding.Path) != true).Select(track => targetIndices.ContainsKey(track.Binding.Path)
                ? new TransformTrack(AnimationBinding.ForBone(track.Binding.Path), track.Position, track.Rotation, track.Scale)
                : track).ToArray()
        }).ToList();
        return scene with { Skeletons = skeletons, Animations = animations };
    }
}
