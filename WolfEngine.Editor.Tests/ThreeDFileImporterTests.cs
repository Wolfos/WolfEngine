using System.Numerics;
using Silk.NET.Assimp;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class ThreeDFileImporterTests
{
    [TestCase(false)]
    [TestCase(true)]
    public unsafe void AppendNode_PreservesModelBindSpaceAcrossAncestors(bool boneParent)
    {
        // These structs contain already-converted left-handed transforms. Constructing them
        // directly exercises our traversal without invoking Assimp's native parser.
        var rootTransform = Matrix4x4.CreateTranslation(0, 3, 0);
        var meshTransform = Matrix4x4.CreateRotationX(-MathF.PI / 2);
        uint meshIndex = 0;
        var child = new Node
        {
            MName = new AssimpString("Mesh"),
            MTransformation = Matrix4x4.Transpose(meshTransform),
            MNumMeshes = 1,
            MMeshes = &meshIndex
        };
        Node* childPointer = &child;
        var root = new Node
        {
            MName = new AssimpString("Root"),
            MTransformation = Matrix4x4.Transpose(rootTransform),
            MNumChildren = 1,
            MChildren = &childPointer
        };
        var mesh = new global::WolfEngine.Mesh(
            [new Vector4(0, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1)],
            [0, 1, 2]);
        var output = new List<ImportedNode>();

        ThreeDFileImporter.AppendNode(&root, [("Mesh", mesh, 0, 0)], output, -1,
            "Fallback", boneParent ? new HashSet<string> { "Root" } : null);

        var node = output.Single(n => n.Meshes.Count > 0);
        Assert.That(node.SkinBindToRig, Is.Not.Null);
        var expected = meshTransform * rootTransform;
        foreach (var point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            Assert.That(Vector3.Distance(Vector3.Transform(point, node.SkinBindToRig!.Value),
                Vector3.Transform(point, expected)), Is.LessThan(.00001f));
        }
        Assert.That(node.LocalTransform, Is.EqualTo(meshTransform));
        Assert.That(node.ParentIndex, Is.EqualTo(boneParent ? -1 : 0));
        Assert.That(node.BoneParentName, Is.EqualTo(boneParent ? "Root" : null));
    }

    [Test]
    public unsafe void PackSkinWeights_MapsSkeletonIndicesAndNormalizesEachVertex()
    {
        // LimitBoneWeights belongs to Assimp. Test our conversion of its bone-major output
        // into the vertex-major shader layout with a deliberately different skeleton order.
        var weightsA = stackalloc VertexWeight[2];
        weightsA[0] = new VertexWeight { MVertexId = 0, MWeight = .2f };
        weightsA[1] = new VertexWeight { MVertexId = 1, MWeight = .4f };
        var weightsB = stackalloc VertexWeight[1];
        weightsB[0] = new VertexWeight { MVertexId = 0, MWeight = .6f };
        var boneA = new Bone { MName = new AssimpString("A"), MNumWeights = 2, MWeights = weightsA };
        var boneB = new Bone { MName = new AssimpString("B"), MNumWeights = 1, MWeights = weightsB };
        var bones = stackalloc Bone*[2];
        bones[0] = &boneA;
        bones[1] = &boneB;
        var mesh = new Silk.NET.Assimp.Mesh { MNumVertices = 2, MNumBones = 2, MBones = bones };

        var packed = SkinWeightPacker.TryPack(&mesh, new Dictionary<string, int> { ["A"] = 3, ["B"] = 1 },
            2, "Mesh", out var indices, out var weights);

        Assert.That(packed, Is.True);
        Assert.That(indices, Is.EqualTo(new uint[] { 3, 1, 0, 0, 3, 0, 0, 0 }));
        Assert.That(weights, Is.EqualTo(new float[] { .25f, .75f, 0, 0, 1, 0, 0, 0 }).Within(.00001f));
    }
}
