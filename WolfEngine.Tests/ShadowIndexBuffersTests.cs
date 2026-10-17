using System.Numerics;
using WolfEngine.Importing;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class ShadowIndexBuffersTests
{
    private static Mesh SeamMesh(bool uvSeam = false, bool skinSeam = false)
    {
        var positions = new[] { new Vector4(0,0,0,1), new Vector4(1,0,0,1), new Vector4(0,1,0,1),
            new Vector4(0,0,0,1), new Vector4(0,1,0,1), new Vector4(-1,0,0,1) };
        var uv = new Vector2[6];
        if (uvSeam) uv[3] = Vector2.One;
        uint[]? bones = skinSeam ? new uint[24] : null;
        float[]? weights = skinSeam ? new float[24] : null;
        if (skinSeam) { bones![12] = 1; for (var i=0;i<6;i++) weights![i*4] = 1; }
        return ShadowMeshOptimization.Optimize(new Mesh(positions, new uint[] {0,1,2,3,4,5}, uvs: uv, boneIndices: bones, boneWeights: weights));
    }

    [Test]
    public void OpaqueWeldsHardSeamsAndAlphaPreservesUvSeams()
    {
        var mesh = SeamMesh(uvSeam: true);
        var buffers = new ShadowIndexBuffers(mesh, 128);
        Assert.That(buffers.Opaque.Distinct().Count(), Is.EqualTo(4));
        Assert.That(buffers.AlphaTest.Distinct().Count(), Is.EqualTo(5));
        Assert.That(buffers.EndOffsetBytes, Is.EqualTo(128 + 3*6*4));
        AssertGeometry(mesh, buffers.Opaque, false);
        AssertGeometry(mesh, buffers.AlphaTest, true);
    }

    [Test]
    public void EquivalentAlphaAndOpaqueRangesShareStorage()
    {
        var mesh = SeamMesh();
        var buffers = new ShadowIndexBuffers(mesh, 128);
        Assert.That(buffers.AlphaTest, Is.SameAs(buffers.Opaque));
        Assert.That(buffers.EndOffsetBytes, Is.EqualTo(128 + 2*6*4));
        buffers.AssignOffsets(mesh);
        Assert.That(mesh.GetPackedIndexOffsetBytes(MeshIndexStream.Main), Is.EqualTo(128));
        Assert.That(mesh.GetPackedIndexOffsetBytes(MeshIndexStream.ShadowOpaque), Is.EqualTo(152));
    }

    [Test]
    public void SkinInfluenceDifferencesPreventWelding()
    {
        var mesh = SeamMesh(skinSeam: true);
        var buffers = new ShadowIndexBuffers(mesh, 0);
        Assert.That(buffers.Opaque.Distinct().Count(), Is.EqualTo(5));
        foreach (var index in buffers.Opaque)
            Assert.That(index, Is.LessThan(mesh.Vertices.Length));
        Assert.That(buffers.Opaque, Does.Contain(3u), "Vertex at the same position driven by another bone must survive.");
    }

    [Test]
    public void MeshWithoutSeamsUsesOriginalStorage()
    {
        var mesh = new Mesh(new[] {Vector4.Zero, Vector4.UnitX, Vector4.UnitY}, new uint[] {0,1,2});
        var buffers = new ShadowIndexBuffers(mesh, 16);
        Assert.That(buffers.Opaque, Is.SameAs(mesh.Indices));
        Assert.That(buffers.AlphaTest, Is.SameAs(mesh.Indices));
        Assert.That(buffers.EndOffsetBytes, Is.EqualTo(28));
    }

    [Test]
    public void ChangingIndexStreamInvalidatesEncodedCommands()
    {
        var set = new SharedDrawIndirectCommandSet();
        set.MarkSlotEncoded(0,0,1,1,5);
        set.SetIndexStream(MeshIndexStream.Main);
        Assert.That(set.RequiresFullReencode(0,0,1,1), Is.False);
        set.SetIndexStream(MeshIndexStream.ShadowOpaque);
        Assert.That(set.RequiresFullReencode(0,0,1,1), Is.True);
    }

    [Test]
    public void TerrainAlwaysUsesOriginalIndicesAndCutoutsKeepUvs()
    {
        var resources = new SharedDrawIndirectEncodeResources(null, null, null, 0, null, MeshIndexStream.ShadowOpaque);
        foreach (var lane in GpuDrawExecutionLanes.Definitions)
        {
            var expected = lane.DrawKind != GpuDrawKind.Mesh ? MeshIndexStream.Main
                : lane.BucketId == GpuDrawBucketId.AlphaTest ? MeshIndexStream.ShadowAlphaTest : MeshIndexStream.ShadowOpaque;
            Assert.That(resources.ForLane(lane).IndexStream, Is.EqualTo(expected));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ImportedArtifactRoundTripsWeldedRanges(bool uvSeam)
    {
        var mesh = SeamMesh(uvSeam);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mesh.bin");
        try
        {
            ImportedMeshSerializer.Write(path, new ImportedMeshAssetFile
            {
                Vertices = mesh.Vertices, Indices = mesh.Indices, Normals = mesh.Normals,
                UVs = mesh.UVs, Tangents = mesh.Tangents,
                ShadowOpaqueIndices = mesh.ShadowOpaqueIndices, ShadowAlphaTestIndices = mesh.ShadowAlphaTestIndices
            });
            var artifact = ImportedMeshSerializer.Read(path);
            var loaded = new Mesh(artifact.Vertices, artifact.Indices, artifact.Normals, artifact.UVs, artifact.Tangents,
                shadowOpaqueIndices: artifact.ShadowOpaqueIndices, shadowAlphaTestIndices: artifact.ShadowAlphaTestIndices);
            Assert.That(loaded.ShadowOpaqueIndices, Is.EqualTo(mesh.ShadowOpaqueIndices));
            Assert.That(loaded.ShadowAlphaTestIndices, Is.EqualTo(mesh.ShadowAlphaTestIndices));
            if (!uvSeam) Assert.That(loaded.ShadowAlphaTestIndices, Is.SameAs(loaded.ShadowOpaqueIndices));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void ProceduralMeshesUseOriginalIndicesEvenWithHardSeams()
    {
        var imported = SeamMesh();
        var mesh = new Mesh(imported.Vertices, imported.Indices);
        Assert.That(mesh.ShadowOpaqueIndices, Is.SameAs(mesh.Indices));
        Assert.That(mesh.ShadowAlphaTestIndices, Is.SameAs(mesh.Indices));
    }

    [Test]
    public void VersionTwoArtifactsRetainSkinningAndFallBackToMainIndices()
    {
        var mesh = SeamMesh(skinSeam: true);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mesh.bin");
        try
        {
            ImportedMeshSerializer.Write(path, new ImportedMeshAssetFile
            {
                Vertices = mesh.Vertices, Indices = mesh.Indices, Normals = mesh.Normals,
                UVs = mesh.UVs, Tangents = mesh.Tangents,
                BoneIndices = mesh.BoneIndices!, BoneWeights = mesh.BoneWeights!
            });
            // Version 3's empty shadow section is two counts and one sharing flag.
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Write))
            {
                stream.Position = 4;
                using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                writer.Write(2);
                stream.SetLength(stream.Length - 9);
            }
            var artifact = ImportedMeshSerializer.Read(path);
            Assert.That(artifact.Version, Is.EqualTo(2));
            Assert.That(artifact.BoneIndices, Is.EqualTo(mesh.BoneIndices));
            Assert.That(artifact.BoneWeights, Is.EqualTo(mesh.BoneWeights));
            Assert.That(artifact.ShadowOpaqueIndices, Is.Empty);
            Assert.That(artifact.ShadowAlphaTestIndices, Is.Empty);
        }
        finally { File.Delete(path); }
    }

    private static void AssertGeometry(Mesh mesh, uint[] indices, bool uv)
    {
        string Triangle(uint[] source, int start) => string.Join(";", source.Skip(start).Take(3).Select(i =>
            mesh.Vertices[i].ToString() + (uv ? mesh.UVs[i].ToString() : "")));
        Assert.That(indices.Length, Is.EqualTo(mesh.Indices.Length));
        Assert.That(Enumerable.Range(0, indices.Length/3).Select(i=>Triangle(indices,i*3)),
            Is.EquivalentTo(Enumerable.Range(0, mesh.Indices.Length/3).Select(i=>Triangle(mesh.Indices,i*3))));
    }
}
