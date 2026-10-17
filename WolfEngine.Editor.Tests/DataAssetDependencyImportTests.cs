using Microsoft.Data.Sqlite;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class DataAssetDependencyImportTests
{
	private string _root = string.Empty;
	private ProjectAssetPipelineService _pipeline = null!;
	private AssetMetadataStore _metadata = null!;
	private DataAssetStore _store = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "WolfEngineDataDependencies", Guid.NewGuid().ToString("N"));
		_metadata = new AssetMetadataStore();
		_store = new DataAssetStore();
		_pipeline = new ProjectAssetPipelineService(
			new AssetPipelineIndex(),
			_metadata,
			Substitute.For<IImageLoader>(),
			_store,
			new MaterialAssetStore(),
			Substitute.For<IThreeDFileImporter>(),
			Substitute.For<ITextureGpuCompressionService>());
		_pipeline.InitializeProject(_root);
	}

	[TearDown]
	public void TearDown()
	{
		SqliteConnection.ClearAllPools();
		Directory.Delete(_root, recursive: true);
	}

	[Test]
	public void Reimport_RecordsNestedReferencesAndReplacesRemovedDependencies()
	{
		var albedoId = Guid.NewGuid();
		var normalId = Guid.NewGuid();
		var replacementId = Guid.NewGuid();
		var layers = new TerrainLayerSet
		{
			Layers =
			[
				new()
				{
					Albedo = new AssetRef<Texture> { NodeId = albedoId },
					Normal = new AssetRef<Texture> { NodeId = normalId }
				},
				new() { Albedo = new AssetRef<Texture> { NodeId = albedoId } }
			]
		};
		var nodeId = Import("Layers", layers);

		Assert.That(_pipeline.GetDependencies(_root).Select(edge => edge.ToNodeId),
			Is.EquivalentTo(new[] { albedoId, normalId }));
		Assert.That(_pipeline.GetDependencies(_root).All(edge =>
			edge.FromNodeId == nodeId && edge.IsHard && edge.Kind == "data-asset"), Is.True);
		Assert.That(_pipeline.ExpandInvalidationClosure(_root, [normalId]), Does.Contain(nodeId));

		layers.Layers[0].Normal = new AssetRef<Texture> { NodeId = replacementId };
		Assert.That(Import("Layers", layers), Is.EqualTo(nodeId));

		Assert.That(_pipeline.GetDependencies(_root).Select(edge => edge.ToNodeId),
			Is.EquivalentTo(new[] { albedoId, replacementId }));
		Assert.That(_pipeline.ExpandInvalidationClosure(_root, [normalId]), Does.Not.Contain(nodeId));
	}

	[Test]
	public void IncrementalRefresh_UpgradesExistingImporterVersionWithoutEditingSource()
	{
		var lookupId = Guid.NewGuid();
		var config = new RenderConfig();
		var colorGrading = config.ColorGrading;
		colorGrading.LookupTable = new AssetRef<ColorLookupTable> { NodeId = lookupId };
		config.ColorGrading = colorGrading;
		var nodeId = Import("RenderConfig", config);
		var sourcePath = Path.Combine(_root, "Assets", "RenderConfig.data.json");
		var sourceBefore = File.ReadAllText(sourcePath);
		var metadata = _metadata.Load(sourcePath + ".meta");
		metadata.ImporterVersion = 1;
		_metadata.Save(sourcePath + ".meta", metadata);

		var result = _pipeline.RefreshProjectIncrementalWithChanges(_root);

		Assert.That(result.ReimportedNodeIds, Does.Contain(nodeId));
		Assert.That(_metadata.Load(sourcePath + ".meta").ImporterVersion, Is.EqualTo(2));
		Assert.That(File.ReadAllText(sourcePath), Is.EqualTo(sourceBefore));
		Assert.That(_pipeline.GetDependencies(_root).Select(edge => edge.ToNodeId), Is.EqualTo(new[] { lookupId }));
	}

	private Guid Import<T>(string name, T asset) where T : IDataAsset
	{
		var relativePath = $"Assets/{name}.data.json";
		var absolutePath = Path.Combine(_root, relativePath);
		_store.SaveAsset(absolutePath, typeof(T), asset);
		_pipeline.ReimportSource(_root, relativePath);

		return _metadata.Load(absolutePath + ".meta").SubAssets.Single().NodeId;
	}
}
