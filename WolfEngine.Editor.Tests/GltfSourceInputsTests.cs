using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class GltfSourceInputsTests
{
	private string _folder = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_folder = Path.Combine(Path.GetTempPath(), "WolfEngineGltfInputsTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_folder);
	}

	[TearDown]
	public void TearDown()
	{
		Directory.Delete(_folder, recursive: true);
	}

	[Test]
	public void InputsHash_ChangesWhenOnlyTheBufferChanges()
	{
		var gltf = Path.Combine(_folder, "mesh.gltf");
		File.WriteAllText(gltf, "{\"buffers\": [{\"uri\": \"mesh.bin\", \"byteLength\": 4}]}");
		File.WriteAllBytes(Path.Combine(_folder, "mesh.bin"), [0, 1, 2, 3]);
		var before = GltfSourceInputs.ComputeInputsHash(gltf);

		File.WriteAllBytes(Path.Combine(_folder, "mesh.bin"), [0, 2, 1, 3]);

		Assert.That(GltfSourceInputs.ComputeInputsHash(gltf), Is.Not.EqualTo(before));
	}

	[Test]
	public void InputsHash_IgnoresEmbeddedData()
	{
		var gltf = Path.Combine(_folder, "embedded.gltf");
		File.WriteAllText(gltf, "{\"buffers\": [{\"uri\": \"data:application/octet-stream;base64,AAECAw==\"}]}");

		Assert.That(GltfSourceInputs.ComputeInputsHash(gltf), Is.EqualTo(GltfSourceInputs.ComputeInputsHash(gltf)));
	}

	[Test]
	public void OnlyGltfHasExternalInputs()
	{
		Assert.Multiple(() =>
		{
			Assert.That(GltfSourceInputs.HasExternalInputs("/models/tree.gltf"), Is.True);
			Assert.That(GltfSourceInputs.HasExternalInputs("/models/tree.GLTF"), Is.True);
			Assert.That(GltfSourceInputs.HasExternalInputs("/models/tree.glb"), Is.False);
			Assert.That(GltfSourceInputs.HasExternalInputs("/models/tree.fbx"), Is.False);
		});
	}
}
