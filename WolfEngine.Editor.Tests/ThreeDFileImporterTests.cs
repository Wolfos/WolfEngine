using System.Text.Json;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class ThreeDFileImporterTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => Directory.CreateDirectory(_directory = Path.Combine(Path.GetTempPath(), "WolfEngineModelImportTests", Guid.NewGuid().ToString("N")));

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void Import_StaticGeometry_StillJoinsDuplicateVertices()
    {
        var scene = ImportFixture(skinned: false);
        var mesh = scene.Nodes.SelectMany(node => node.Meshes).Single().Mesh;

        Assert.That(mesh.Vertices, Has.Length.EqualTo(4));
        Assert.That(mesh.Indices, Has.Length.EqualTo(6));
        Assert.That(mesh.IsSkinned, Is.False);
    }

    [Test]
    public void Import_CoincidentVerticesWithDifferentBones_PreservesBothBindings()
    {
        var scene = ImportFixture(skinned: true);
        var mesh = scene.Nodes.SelectMany(node => node.Meshes).Single().Mesh;

        Assert.That(mesh.Vertices, Has.Length.EqualTo(6), "Welding by position loses distinct skin bindings.");
        Assert.That(mesh.IsSkinned, Is.True);
        var sharedPositions = mesh.Vertices.Select((position, index) => (position, index))
            .GroupBy(vertex => vertex.position).Where(group => group.Count() == 2).ToArray();
        Assert.That(sharedPositions, Has.Length.EqualTo(2));
        foreach (var group in sharedPositions)
        {
            var vertices = group.ToArray();
            Assert.That(mesh.BoneIndices![vertices[0].index * Mesh.InfluencesPerVertex],
                Is.Not.EqualTo(mesh.BoneIndices[vertices[1].index * Mesh.InfluencesPerVertex]));
        }
        Assert.That(mesh.BoneWeights!.Where((_, index) => index % Mesh.InfluencesPerVertex == 0), Is.All.EqualTo(1));
    }

    [Test]
    public void Import_MoreThanFourInfluences_KeepsStrongestWeightsAndNormalizes()
    {
        var scene = ImportFixture(skinned: true, excessInfluences: true);
        var mesh = scene.Nodes.SelectMany(node => node.Meshes).Single().Mesh;
        var boneNames = scene.Skeletons.Single().BoneNames;
        Assert.That(mesh.IsSkinned, Is.True);
        for (var vertex = 0; vertex < mesh.Vertices.Length; vertex++)
        {
            var offset = vertex * Mesh.InfluencesPerVertex;
            var keptBones = mesh.BoneIndices!.Skip(offset).Take(Mesh.InfluencesPerVertex).Select(index => boneNames[index]);
            Assert.That(keptBones, Is.EquivalentTo(new[] { "BoneB", "BoneC", "BoneD", "BoneE" }));
            var weights = mesh.BoneWeights!.Skip(offset).Take(Mesh.InfluencesPerVertex).ToArray();
            Assert.That(weights, Is.All.GreaterThan(0));
            Assert.That(weights.Sum(), Is.EqualTo(1f).Within(0.00001f));
        }
    }

    private ImportedScene ImportFixture(bool skinned, bool excessInfluences = false)
    {
        var boneCount = excessInfluences ? 5 : 2;
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        var views = new List<object>();
        var accessors = new List<object>();
        int AddAccessor(Action write, int componentType, int count, string type, float[]? min = null, float[]? max = null)
        {
            while (data.Position % 4 != 0) writer.Write((byte)0);
            var offset = (int)data.Position;
            write();
            views.Add(new { buffer = 0, byteOffset = offset, byteLength = (int)data.Position - offset });
            accessors.Add(new { bufferView = views.Count - 1, componentType, count, type, min, max });
            return accessors.Count - 1;
        }

        var attributes = new Dictionary<string, int>();
        attributes["POSITION"] = AddAccessor(() =>
        {
            foreach (var value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0 }) writer.Write(value);
        }, 5126, 6, "VEC3", [0, 0, 0], [1, 1, 0]);
        attributes["NORMAL"] = AddAccessor(() =>
        {
            for (var i = 0; i < 6; i++) { writer.Write(0f); writer.Write(0f); writer.Write(1f); }
        }, 5126, 6, "VEC3");
        if (skinned)
        {
            attributes["JOINTS_0"] = AddAccessor(() =>
            {
                for (var i = 0; i < 6; i++)
                    foreach (var joint in excessInfluences ? new ushort[] { 0, 1, 2, 3 } : new ushort[] { (ushort)(i / 3), 0, 0, 0 }) writer.Write(joint);
            }, 5123, 6, "VEC4");
            attributes["WEIGHTS_0"] = AddAccessor(() =>
            {
                for (var i = 0; i < 6; i++)
                    foreach (var weight in excessInfluences ? new float[] { 0.05f, 0.15f, 0.25f, 0.3f } : new float[] { 1, 0, 0, 0 }) writer.Write(weight);
            }, 5126, 6, "VEC4");
            if (excessInfluences)
            {
                attributes["JOINTS_1"] = AddAccessor(() =>
                {
                    for (var i = 0; i < 6; i++)
                        foreach (var joint in new ushort[] { 4, 0, 0, 0 }) writer.Write(joint);
                }, 5123, 6, "VEC4");
                attributes["WEIGHTS_1"] = AddAccessor(() =>
                {
                    for (var i = 0; i < 6; i++)
                        foreach (var weight in new float[] { 0.25f, 0, 0, 0 }) writer.Write(weight);
                }, 5126, 6, "VEC4");
            }
        }
        var indices = AddAccessor(() => { for (ushort i = 0; i < 6; i++) writer.Write(i); }, 5123, 6, "SCALAR");
        var inverseBindMatrices = skinned ? AddAccessor(() =>
        {
            for (var bone = 0; bone < boneCount; bone++)
                for (var i = 0; i < 16; i++) writer.Write(i % 5 == 0 ? 1f : 0f);
        }, 5126, boneCount, "MAT4") : -1;
        var nodes = new List<object>();
        if (skinned)
        {
            nodes.Add(new { name = "Root", children = Enumerable.Range(1, boneCount + 1).ToArray() });
            nodes.Add(new { name = "Mesh", mesh = 0, skin = 0 });
            for (var bone = 0; bone < boneCount; bone++) nodes.Add(new { name = $"Bone{(char)('A' + bone)}" });
        }
        else nodes.Add(new { name = "Mesh", mesh = 0 });
        var document = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { 0 } } },
            ["nodes"] = nodes,
            ["meshes"] = new[] { new { primitives = new[] { new { attributes, indices } } } },
            ["buffers"] = new[] { new { byteLength = (int)data.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(data.ToArray()) } },
            ["bufferViews"] = views,
            ["accessors"] = accessors
        };
        if (skinned) document["skins"] = new[] { new { joints = Enumerable.Range(2, boneCount).ToArray(), inverseBindMatrices } };
        var path = Path.Combine(_directory, "duplicate-vertices.gltf");
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        return new ThreeDFileImporter(Substitute.For<IImageLoader>()).Import(path, new ModelImportSettings());
    }
}
