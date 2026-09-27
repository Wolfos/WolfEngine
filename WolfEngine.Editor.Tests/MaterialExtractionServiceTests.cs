using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
[NonParallelizable]
public sealed class MaterialExtractionServiceTests
{
	private string _root = null!;
	private IEditorProjectService _project = null!;
	private IEditorPlaySession _play = null!;
	private MaterialAssetStore _store = null!;
	private AssetMetadataStore _metadata = null!;
	private AssetDatabaseEntry _imported = null!;
	private EditorScene _scene = null!;
	private MaterialExtractionService _service = null!;
	private Material _resolved = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "WolfMaterialExtraction-" + Guid.NewGuid());
		Directory.CreateDirectory(Path.Combine(_root, "Assets", "Models"));
		Directory.CreateDirectory(Path.Combine(_root, "Library"));
		_project = Substitute.For<IEditorProjectService>();
		_project.HasOpenProject.Returns(true);
		_project.ProjectRootPath.Returns(_root);
		_project.AssetsPath.Returns(Path.Combine(_root, "Assets"));
		_project.GetAbsolutePath(Arg.Any<string>()).Returns(call => Path.Combine(_root, call.Arg<string>()));
		_project.GetAbsoluteAssetPath(Arg.Any<Guid>(), Arg.Any<string>()).Returns(call => Path.Combine(_root, call.ArgAt<string>(1)));
		_project.When(project => project.DeleteAssetSource(Arg.Any<string>())).Do(call =>
		{
			var path = Path.Combine(_root, call.Arg<string>());
			File.Delete(path);
			File.Delete(path + ".meta");
		});
		_imported = new AssetDatabaseEntry
		{
			Id = Guid.NewGuid(), Type = AssetType.Material, IsGenerated = true, Name = "Material 17",
			RelativeSourcePath = "Assets/Models/model.gltf", RelativeAssetPath = "Library/material_17.mat.json"
		};
		_store = new MaterialAssetStore();
		_metadata = new AssetMetadataStore();
		_store.SaveAsset(Path.Combine(_root, _imported.RelativeAssetPath), new MaterialAsset
		{
			MaterialType = MaterialAssetType.AlphaTest, BaseColor = new ColorRGBA(0.2f, 0.4f, 0.6f, 0.8f),
			UvOffsetScale = new Vector4(0.25f, 0.5f, 2, 3), MetallicFactor = 0.3f, RoughnessFactor = 0.7f,
			NormalScale = 0.9f, EmissiveFactor = new Vector3(1, 2, 3), EmissiveIntensity = 4,
			DoubleSided = true, AlphaCutoff = 0.35f,
			Textures = new MaterialTextureAssignments { Albedo = new AssetRef<Texture> { NodeId = Guid.NewGuid() } }
		});
		_scene = new EditorScene { Name = "Unsaved edits", RelativeAssetPath = "Assets/test.scene.json" };
		_play = Substitute.For<IEditorPlaySession>();
		_play.AuthoringScene.Returns(_scene);
		_play.ActiveScene.Returns(_scene);
		_service = new MaterialExtractionService(_project, _store, _metadata, _play);
		_resolved = new Material("gbuffer.slang");
		var registry = Substitute.For<IAssetInstanceRegistry>();
		registry.GetInstance(Arg.Any<Guid>(), typeof(Material)).Returns(_resolved);
		AssetDatabase.SetInstanceRegistry(registry);
	}

	[TearDown]
	public void TearDown()
	{
		AssetDatabase.ClearInstanceRegistry();
		Directory.Delete(_root, true);
	}

	[Test]
	public void ExtractCopiesEveryPropertyBesideSourceAndUsesUniqueName()
	{
		var occupied = Path.Combine(_root, "Assets/Models/model Material 17.mat.json");
		File.WriteAllText(occupied, "do not overwrite");
		var result = _service.Extract(_imported);
		Assert.That(result.Success, Is.True, result.ErrorMessage);
		var extracted = occupied.Replace(".mat.json", " 1.mat.json");
		Assert.That(File.ReadAllText(extracted), Is.EqualTo(File.ReadAllText(Path.Combine(_root, _imported.RelativeAssetPath))));
		Assert.That(File.ReadAllText(occupied), Is.EqualTo("do not overwrite"));
		var meta = _metadata.Load(extracted + ".meta");
		Assert.That(meta.SubAssets.Single().NodeId, Is.EqualTo(result.AssetId));
		_project.Received().RefreshAssetSource("Assets/Models/model Material 17 1.mat.json");
	}

	[Test]
	public void ReplacesCellsAndPrefabsIncludingNestedReferencesAndKeepsInheritedOverrides()
	{
		var cellPath = Path.Combine(_root, "Assets/global.cell.json");
		var prefabPath = Path.Combine(_root, "Assets/example.prefab.json");
		var document = CreateSavedDocument();
		var unrelated = Guid.NewGuid();
		document["Entities"]![0]!["Components"]![0]!["Data"]!["Other"] = new JsonObject { ["NodeId"] = unrelated.ToString() };
		File.WriteAllText(cellPath, document.ToJsonString());
		File.WriteAllText(prefabPath, document.ToJsonString());
		var result = _service.Extract(_imported);
		Assert.That(result.Success, Is.True, result.ErrorMessage);
		foreach (var path in new[] { cellPath, prefabPath })
		{
			var saved = JsonNode.Parse(File.ReadAllText(path))!;
			var entity = saved["Entities"]![0]!;
			var data = entity["Components"]![0]!["Data"]!;
			Assert.That(data["Nested"]![0]!["Material"]!["NodeId"]!.GetValue<string>(), Is.EqualTo(result.AssetId.ToString()));
			Assert.That(data["Other"]!["NodeId"]!.GetValue<string>(), Is.EqualTo(unrelated.ToString()));
			Assert.That(data["Text"]!.GetValue<string>(), Is.EqualTo(_imported.Id.ToString()));
			Assert.That(entity["PrefabOverrides"]!["ComponentTypeIds"]!.AsArray().Select(id => id!.GetValue<string>()),
				Is.EquivalentTo(new[] { "existing", "test-component" }));
		}
		_project.Received().RefreshAssetSource("Assets/global.cell.json");
		_project.Received().RefreshAssetSource("Assets/example.prefab.json");
	}

	[TestCase(false)]
	[TestCase(true)]
	public void ReplacesAuthoringAndPlaySceneInMemoryAndLaterSaveKeepsNewReferenceAndUnsavedEdits(bool hasConsumedRenderNotification)
	{
		var entity = AddMesh(_scene);
		var runtime = new EditorScene { World = new World(WorldTag.Game) };
		var runtimeEntity = AddMesh(runtime);
		foreach (var pair in new[] { (_scene, entity), (runtime, runtimeEntity) })
		{
			pair.Item1.World.AddComponent<WorldTransform>(pair.Item2);
			if (hasConsumedRenderNotification)
				pair.Item1.World.AddComponent(pair.Item2, new DirtyWorldTransform { Consumed = 2 });
		}
		_play.RuntimeScene.Returns(runtime);
		_play.ActiveScene.Returns(runtime);
		var result = _service.Extract(_imported);
		Assert.That(result.Success, Is.True, result.ErrorMessage);
		foreach (var pair in new[] { (_scene, entity), (runtime, runtimeEntity) })
		{
			var renderer = pair.Item1.World.GetComponent<MeshRenderer>(pair.Item2);
			Assert.That(renderer.MaterialAsset.NodeId, Is.EqualTo(result.AssetId));
			Assert.That(renderer.Material, Is.SameAs(_resolved));
			Assert.That(pair.Item1.World.HasComponent<DirtyWorldTransform>(pair.Item2), Is.True,
				"Replacing a material must notify the renderer to refresh its cached draw entry.");
			Assert.That(pair.Item1.World.GetComponent<DirtyWorldTransform>(pair.Item2).Consumed, Is.Zero);
		}
		var factory = new EditorSceneFactory(_project, Substitute.For<IProjectAssetPipelineService>());
		factory.Save(_scene);
		var saved = JsonSerializer.Deserialize<Cell>(File.ReadAllText(Path.Combine(_root, "Assets/global.cell.json")), AssetJson.SerializerOptions)!;
		Assert.That(saved.Entities.Single().Name, Is.EqualTo("Unsaved entity name"));
		var mesh = saved.Entities.Single().Components.Single(component => component.Type.Contains(nameof(MeshRenderer)));
		Assert.That(mesh.Data.GetProperty("MaterialAsset").GetProperty("NodeId").GetGuid(), Is.EqualTo(result.AssetId));
	}

	[Test]
	public void ReplacedInheritedComponentSurvivesPrefabMergeOnReload()
	{
		var source = new SavedEntity
		{
			Components = [new SavedComponent
			{
				Type = "MeshRenderer", TypeId = "mesh",
				Data = JsonSerializer.SerializeToElement(new { MaterialAsset = new { NodeId = _imported.Id } })
			}]
		};
		var instance = new SavedEntity
		{
			PrefabSourcePath = [new SavedPrefabLink { PrefabAssetId = Guid.NewGuid(), PrefabEntityId = Guid.NewGuid() }],
			Components = source.Components
		};
		var document = JsonSerializer.SerializeToNode(new Cell { Entities = [instance] }, AssetJson.SerializerOptions)!;
		var newId = Guid.NewGuid();
		Assert.That(MaterialExtractionService.ReplaceSavedReferences(document, _imported.Id, newId), Is.True);
		instance = document.Deserialize<Cell>(AssetJson.SerializerOptions)!.Entities.Single();
		var merged = EditorPrefabUtility.MergePrefabSourceEntity(instance, source);
		Assert.That(merged.Components.Single().Data.GetProperty("MaterialAsset").GetProperty("NodeId").GetGuid(), Is.EqualTo(newId));
	}

	[Test]
	public void InMemoryReplacementPreservesEntityLinksAndNestedMaterialReferences()
	{
		var target = _scene.World.CreateEntity("Target");
		var owner = _scene.World.CreateEntity("Owner");
		_scene.World.AddComponent(owner, new MaterialHolder
		{
			Target = target, Materials = [new AssetRef<Material> { NodeId = _imported.Id }], Notes = "Unsaved notes"
		});
		var result = _service.Extract(_imported);
		Assert.That(result.Success, Is.True, result.ErrorMessage);
		var holder = _scene.World.GetComponent<MaterialHolder>(owner);
		Assert.That(holder.Target, Is.EqualTo(target));
		Assert.That(holder.Notes, Is.EqualTo("Unsaved notes"));
		Assert.That(holder.Materials.Single().NodeId, Is.EqualTo(result.AssetId));
	}

	[Test]
	public void FailureRestoresSourceFilesAndLiveReferencesAndRemovesExtractedMaterial()
	{
		var entity = AddMesh(_scene);
		var path = Path.Combine(_root, "Assets/example.prefab.json");
		var before = CreateSavedDocument().ToJsonString();
		File.WriteAllText(path, before);
		var failedOnce = false;
		_project.When(project => project.RefreshAssetSource("Assets/example.prefab.json")).Do(_ =>
		{
			if (!failedOnce) { failedOnce = true; throw new IOException("Test import failure"); }
		});
		var result = _service.Extract(_imported);
		Assert.That(result.Success, Is.False);
		Assert.That(result.ErrorMessage, Does.Contain("Test import failure"));
		Assert.That(File.ReadAllText(path), Is.EqualTo(before));
		Assert.That(_scene.World.GetComponent<MeshRenderer>(entity).MaterialAsset.NodeId, Is.EqualTo(_imported.Id));
		Assert.That(Directory.GetFiles(Path.Combine(_root, "Assets/Models")), Is.Empty);
	}

	[Test]
	public void ReadOnlyMountIsRejectedBeforeWriting()
	{
		_project.IsAssetReadOnly(_imported.Id).Returns(true);
		Assert.That(_service.Extract(_imported).Success, Is.False);
		Assert.That(Directory.GetFiles(Path.Combine(_root, "Assets/Models")), Is.Empty);
	}

	private Entity AddMesh(EditorScene scene)
	{
		var entity = scene.World.CreateEntity("Unsaved entity name");
		scene.World.AddComponent(entity, new MeshRenderer
		{
			MaterialAsset = new AssetRef<Material> { NodeId = _imported.Id }, Material = new Material("old.slang")
		});
		return entity;
	}

	private JsonNode CreateSavedDocument() => JsonNode.Parse(JsonSerializer.Serialize(new
	{
		Entities = new[] { new
		{
			PrefabSourcePath = new[] { new { PrefabAssetId = Guid.NewGuid(), PrefabEntityId = Guid.NewGuid() } },
			PrefabOverrides = new { ComponentTypeIds = new[] { "existing" } },
			Components = new[] { new
			{
				TypeId = "test-component", Data = new
				{
					Nested = new[] { new { Material = new { NodeId = _imported.Id } } }, Text = _imported.Id.ToString()
				}
			} }
		} }
	}))!;

	public struct MaterialHolder : IEntityComponent
	{
		public Entity Target;
		public AssetRef<Material>[] Materials;
		public string Notes;
	}
}
