using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.Importing;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class AnimationRigBindingTests
{
    private static ImportedSkeleton Rig() => new("rig", ["root", "hand"], [-1, 0],
        [BoneTransform.Identity, new(Vector3.UnitY, Quaternion.Identity, Vector3.One)], [Matrix4x4.Identity, Matrix4x4.Identity]);
    private static ImportedScene Source() => new("animation-only", [], [],
        [new("root", Matrix4x4.Identity, [], -1), new("hand", Matrix4x4.CreateTranslation(Vector3.UnitY), [], 0), new("Door", Matrix4x4.Identity, [], -1)], [],
        [new("take", 1, 30, -1, [Track("root"), Track("hand"), Track("Door")], [])]);
    private static TransformTrack Track(string name) => new(AnimationBinding.ForProperty(name, ""), Vector3Curve.Empty, QuaternionCurve.Empty, Vector3Curve.Empty);
    [Test] public void AnimationOnlySource_BindsBonesAndPreservesEntityTracks()
    {
        var bound = AnimationRigBinding.Bind(Source(), Rig());
        Assert.That(bound.Skeletons, Has.Count.EqualTo(1));
        Assert.That(bound.Animations[0].TransformTracks.Select(t => t.Binding.Kind), Is.EqualTo(new[] {AnimationBindingKind.Bone, AnimationBindingKind.Bone, AnimationBindingKind.Property}));
        Assert.That(bound.Skeletons[0].BindPoseLocal[1].Position, Is.EqualTo(Vector3.UnitY));
    }
    [Test] public void Binding_RejectsHierarchyAndUnitMismatch()
    {
        var source = Source(); source.Nodes[1] = source.Nodes[1] with {ParentIndex=-1};
        Assert.Throws<InvalidOperationException>(() => AnimationRigBinding.Bind(source, Rig()));
        source = Source(); source.Nodes[1] = source.Nodes[1] with {LocalTransform=Matrix4x4.CreateScale(100) * Matrix4x4.CreateTranslation(0,100,0)};
        Assert.Throws<InvalidOperationException>(() => AnimationRigBinding.Bind(source, Rig()));
    }
    [Test] public void Binding_RejectsMissingNamesAndAmbiguity()
    {
        var source = Source(); source.Nodes.Add(source.Nodes[0]);
        Assert.Throws<InvalidOperationException>(() => AnimationRigBinding.Bind(source, Rig()));
        Assert.Throws<InvalidOperationException>(() => AnimationRigBinding.Bind(Source(), Rig() with {BoneNames=["other", "another"]}));
    }
}
