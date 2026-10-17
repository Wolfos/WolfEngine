using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class RuntimeMaterialPreservationTests
{
	[Test]
	public void MaterialTypeChangeAndSubsequentEditsKeepTheSceneMaterialInstance()
	{
		var root = Path.Combine(Path.GetTempPath(), "wolf-material-preservation-" + Guid.NewGuid());
		Directory.CreateDirectory(Path.Combine(root, "Assets"));
		Directory.CreateDirectory(Path.Combine(root, "Library"));
		File.WriteAllText(Path.Combine(root, "Gameplay.csproj"), "<Project />");
		EditorProjectManifestFile.Save(root, new EditorProjectManifest { GameplayProjectRelativePath = "Gameplay.csproj" });
		try
		{
			const string relativePath = "Assets/extracted.mat.json";
			var id = Guid.NewGuid();
			var store = new MaterialAssetStore();
			var asset = new MaterialAsset { MaterialType = MaterialAssetType.AlphaTest };
			store.SaveAsset(Path.Combine(root, relativePath), asset);
			AssetDatabase Database() => new()
			{
				Assets = [new AssetDatabaseEntry
				{
					Id = id, SourceId = id, Type = AssetType.Material,
					RelativeAssetPath = relativePath, RelativeSourcePath = relativePath,
					SummaryJson = JsonSerializer.Serialize(new MaterialAssetSummary { MaterialType = asset.MaterialType }, AssetJson.SerializerOptions)
				}]
			};
			var pipeline = Substitute.For<IProjectAssetPipelineService>();
			pipeline.RefreshProjectIncremental(root).Returns(_ => Database());
			pipeline.LoadDatabase(root).Returns(_ => Database());
			pipeline.ExpandInvalidationClosure(root, Arg.Any<IEnumerable<Guid>>()).Returns([id]);
			var factory = Substitute.For<IMaterialFactory>();
			factory.GetMaterial(default!, default).ReturnsForAnyArgs(_ => new Material("gbuffer.slang"));
			using var services = new ServiceCollection()
				.AddSingleton<IMaterialRuntimeAssetResolver>(new MaterialRuntimeAssetResolver(store, new MaterialTypeRegistry(), factory))
				.BuildServiceProvider();
			var registry = new EditorAssetInstanceRegistry(services);
			var project = new EditorProjectService(pipeline, registry);
			Assert.That(project.OpenProject(root, out var error), Is.True, error);
			var renderer = new MeshRenderer { Material = (Material)registry.GetInstance(id, typeof(Material))! };

			// This is the save path's ordering: update the live object, then refresh the
			// source while preserving that already-synchronized runtime instance.
			asset.MaterialType = MaterialAssetType.AlphaBlend;
			var live = (Material)registry.GetInstance(id, typeof(Material))!;
			live.AlphaMode = AlphaMode.AlphaBlend;
			store.SaveAsset(Path.Combine(root, relativePath), asset);
			project.RefreshAssetSource(relativePath, id);
			Assert.That(registry.GetInstance(id, typeof(Material)), Is.SameAs(renderer.Material),
				"Refreshing a changed material summary must preserve the instance that the scene draws.");

			for (var edit = 1; edit <= 3; edit++)
			{
				asset.BaseColor = new ColorRGBA(edit * 0.1f, 0.4f, 0.7f, 1);
				asset.MetallicFactor = edit * 0.2f;
				live = (Material)registry.GetInstance(id, typeof(Material))!;
				live.Color = asset.BaseColor;
				live.MetallicFactor = asset.MetallicFactor;
				store.SaveAsset(Path.Combine(root, relativePath), asset);
				project.RefreshAssetSource(relativePath, id);
				Assert.That(registry.GetInstance(id, typeof(Material)), Is.SameAs(renderer.Material));
				Assert.That(renderer.Material!.Color, Is.EqualTo(asset.BaseColor));
				Assert.That(renderer.Material.MetallicFactor, Is.EqualTo(asset.MetallicFactor));
			}
			// Undoing the type change also changes the summary and must keep the same instance.
			asset.MaterialType = MaterialAssetType.AlphaTest;
			live.AlphaMode = AlphaMode.AlphaTest;
			store.SaveAsset(Path.Combine(root, relativePath), asset);
			project.RefreshAssetSource(relativePath, id);
			Assert.That(registry.GetInstance(id, typeof(Material)), Is.SameAs(renderer.Material));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
