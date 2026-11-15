using System.Numerics;
using WolfEngine.Animation;

namespace WolfEngine.Tests;

public class SkinningBindSpaceTests
{
    [Test]
    public void DirectTrsCompositionMatchesGeneralMatricesWithNonUniformAndNegativeScale()
    {
        for (var i = 0; i < 40; i++)
        {
            var bone = new BoneTransform(new(i * .2f, -3, 8), Quaternion.CreateFromYawPitchRoll(i * .1f, .3f, -.7f), new(-.3f, i * .02f, 2));
            var reference = Matrix4x4.CreateScale(bone.Scale) * Matrix4x4.CreateFromQuaternion(bone.Rotation) * Matrix4x4.CreateTranslation(bone.Position);
            AssertClose(Vector3.Transform(new(.3f, -2, 4), bone.ToMatrix()), Vector3.Transform(new(.3f, -2, 4), reference));
        }
    }

    [Test]
    public void RotatedMeshMatchesRigSocketForCurrentAndPreviousPose()
    {
        var bind = Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, .3f, 0);
        var actor = Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(3, 0, 8);
        Matrix4x4[] current = [Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(0, 1.2f, 0)];
        Matrix4x4[] previous = [Matrix4x4.CreateTranslation(0, .8f, 0)];
        var renderer = new SkinnedMeshRenderer { MeshBindToRig = bind };
        renderer.PrepareSkinningPalette(current, previous, 1, bind);
        var vertex = new Vector3(.2f, .1f, -.4f);
        AssertClose(Vector3.Transform(vertex, renderer.LocalSkinningMatrices[0] * bind * actor),
            Vector3.Transform(vertex, bind * current[0] * actor));
        AssertClose(Vector3.Transform(vertex, renderer.PreviousLocalSkinningMatrices[0] * bind * actor),
            Vector3.Transform(vertex, bind * previous[0] * actor));
        Assert.That(current[0].Translation.Y, Is.EqualTo(1.2f), "Shared rig palette remains unchanged.");
    }


    [Test]
    public void SharedRigSupportsDifferentMeshBindSpacesWithoutSteadyAllocations()
    {
        Matrix4x4[] pose = [Matrix4x4.CreateTranslation(0, 1, 0)];
        var first = new SkinnedMeshRenderer { MeshBindToRig = Matrix4x4.Identity };
        var second = new SkinnedMeshRenderer { MeshBindToRig = Matrix4x4.CreateRotationX(MathF.PI / 2) };
        first.PrepareSkinningPalette(pose, pose, 1, Matrix4x4.Identity);
        second.PrepareSkinningPalette(pose, pose, 1, Matrix4x4.Identity);
        Assert.That(first.LocalSkinningMatrices, Is.Not.SameAs(second.LocalSkinningMatrices));
        Assert.That(first.LocalSkinningMatrices[0], Is.Not.EqualTo(second.LocalSkinningMatrices[0]));
        var cached = second.LocalSkinningMatrices;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (uint i = 1; i <= 100; i++) second.PrepareSkinningPalette(pose, pose, i, Matrix4x4.Identity);
        var allocations = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocations, Is.Zero);
        Assert.That(second.LocalSkinningMatrices, Is.SameAs(cached));
        var generation = second.LocalPoseGeneration;
        second.PrepareSkinningPalette(pose, pose, 100, Matrix4x4.Identity);
        Assert.That(second.LocalPoseGeneration, Is.EqualTo(generation));
        second.MeshBindToRig = Matrix4x4.Identity;
        second.PrepareSkinningPalette(pose, pose, 100, Matrix4x4.Identity);
        Assert.That(second.LocalPoseGeneration, Is.EqualTo(generation + 1));
        Assert.That(second.LocalSkinningMatrices[0], Is.EqualTo(pose[0]));
    }

    [Test]
    public void ConsecutivePaletteReusesLastCurrentButSkippedGenerationRebuildsPrevious()
    {
        var bind = Matrix4x4.CreateRotationX(MathF.PI / 2);
        var renderer = new SkinnedMeshRenderer { MeshBindToRig = bind };
        var current = new[] { Matrix4x4.CreateTranslation(0, 1, 0) };
        var previous = new[] { Matrix4x4.Identity };
        renderer.PrepareSkinningPalette(current, previous, 1, bind);
        var firstLocal = renderer.LocalSkinningMatrices[0];

        previous[0] = current[0];
        current[0] = Matrix4x4.CreateTranslation(0, 2, 0);
        renderer.PrepareSkinningPalette(current, previous, 2, bind);
        Assert.That(renderer.PreviousLocalSkinningMatrices[0], Is.EqualTo(firstLocal));

        previous[0] = Matrix4x4.CreateTranslation(0, 4, 0);
        current[0] = Matrix4x4.CreateTranslation(0, 5, 0);
        renderer.PrepareSkinningPalette(current, previous, 4, bind);
        Matrix4x4.Invert(bind, out var rigToMesh);
        Assert.That(renderer.PreviousLocalSkinningMatrices[0], Is.EqualTo(bind * previous[0] * rigToMesh));
    }

    [Test]
    public void SkinnedVerticesLandWhereTheSkeletonPutsThemWhereverTheMeshEntitySits()
    {
        // A mesh may sit at its bind, at the animator's origin, or under a socket that follows a bone. None of them
        // may move the skin, which belongs to the skeleton.
        var bind = Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, .3f, 0);
        var actor = Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(3, 0, 8);
        Matrix4x4[] current = [Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(0, 1.2f, 0)];
        var vertex = new Vector3(.2f, .1f, -.4f);
        var expected = Vector3.Transform(vertex, bind * current[0] * actor);

        foreach (var meshToAnimator in new[]
        {
            bind,
            Matrix4x4.Identity,
            Matrix4x4.CreateRotationY(1.1f) * Matrix4x4.CreateTranslation(0, 2, -1)
        })
        {
            var renderer = new SkinnedMeshRenderer { MeshBindToRig = bind };
            renderer.PrepareSkinningPalette(current, current, 1, meshToAnimator);

            AssertClose(Vector3.Transform(vertex, renderer.LocalSkinningMatrices[0] * meshToAnimator * actor), expected);
        }
    }

    [Test]
    public void MovingTheMeshRebuildsBothPalettesInsteadOfReusingTheLastFrame()
    {
        var renderer = new SkinnedMeshRenderer { MeshBindToRig = Matrix4x4.Identity };
        var current = new[] { Matrix4x4.CreateTranslation(0, 1, 0) };
        var previous = new[] { Matrix4x4.Identity };
        renderer.PrepareSkinningPalette(current, previous, 1, Matrix4x4.Identity);

        var moved = Matrix4x4.CreateTranslation(0, 0, 2);
        Matrix4x4.Invert(moved, out var animatorToMesh);
        previous[0] = current[0];
        current[0] = Matrix4x4.CreateTranslation(0, 2, 0);
        renderer.PrepareSkinningPalette(current, previous, 2, moved);

        Assert.That(renderer.LocalSkinningMatrices[0], Is.EqualTo(current[0] * animatorToMesh));
        Assert.That(renderer.PreviousLocalSkinningMatrices[0], Is.EqualTo(previous[0] * animatorToMesh));
    }

    private static void AssertClose(Vector3 actual, Vector3 expected) =>
        Assert.That(Vector3.Distance(actual, expected), Is.LessThan(.00001f));
}
