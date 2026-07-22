using System;
using System.IO;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Projects;

public interface IEditorAssetSnapshotService
{
	EditorAssetFileSnapshot CaptureMaterialAssetSnapshot(AssetDatabaseEntry asset);
	EditorAssetFileSnapshot CaptureMaterialAssetSnapshot(AssetDatabaseEntry asset, MaterialAsset materialAsset);
	EditorAssetFileSnapshot CaptureDataAssetSnapshot(AssetDatabaseEntry asset);
	EditorAssetFileSnapshot CaptureDataAssetSnapshot(AssetDatabaseEntry asset, Type dataAssetType, IDataAsset dataAsset);
	void SaveMaterialAsset(AssetDatabaseEntry asset, MaterialAsset materialAsset);
	void SaveDataAsset(AssetDatabaseEntry asset, Type dataAssetType, IDataAsset dataAsset);
	void ApplyMaterialAssetSnapshot(EditorAssetFileSnapshot snapshot);
	void ApplyDataAssetSnapshot(EditorAssetFileSnapshot snapshot);
}

public sealed class EditorAssetSnapshotService : IEditorAssetSnapshotService
{
	private readonly IEditorProjectService _projectService;
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly IDataAssetStore _dataAssetStore;
	private readonly IMaterialTypeRegistry _materialTypeRegistry;
	private readonly ITextureFactory _textureFactory;
	private readonly RenderGraph _renderGraph;

	public EditorAssetSnapshotService(
		IEditorProjectService projectService,
		IMaterialAssetStore materialAssetStore,
		IDataAssetStore dataAssetStore,
		IMaterialTypeRegistry materialTypeRegistry,
		ITextureFactory textureFactory,
		RenderGraph renderGraph)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_dataAssetStore = dataAssetStore ?? throw new ArgumentNullException(nameof(dataAssetStore));
		_materialTypeRegistry = materialTypeRegistry ?? throw new ArgumentNullException(nameof(materialTypeRegistry));
		_textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
	}

	public EditorAssetFileSnapshot CaptureMaterialAssetSnapshot(AssetDatabaseEntry asset)
	{
		return CaptureFileSnapshot(asset);
	}

	public EditorAssetFileSnapshot CaptureMaterialAssetSnapshot(AssetDatabaseEntry asset, MaterialAsset materialAsset)
	{
		ArgumentNullException.ThrowIfNull(materialAsset);
		return new EditorAssetFileSnapshot(asset.Id, asset.RelativeAssetPath, asset.RelativeSourcePath, SerializeMaterialAsset(materialAsset));
	}

	public EditorAssetFileSnapshot CaptureDataAssetSnapshot(AssetDatabaseEntry asset)
	{
		return CaptureFileSnapshot(asset);
	}

	public EditorAssetFileSnapshot CaptureDataAssetSnapshot(AssetDatabaseEntry asset, Type dataAssetType, IDataAsset dataAsset)
	{
		ArgumentNullException.ThrowIfNull(dataAssetType);
		ArgumentNullException.ThrowIfNull(dataAsset);

		var assetFile = new DataAssetFile
		{
			Version = DataAssetFile.CurrentVersion,
			AssetType = AssetType.DataAsset,
			DataAssetType = dataAssetType.AssemblyQualifiedName ?? dataAssetType.FullName ?? dataAssetType.Name,
			DataAssetTypeId = string.Empty,
			Data = JsonSerializer.SerializeToElement(dataAsset, dataAssetType, AssetJson.GetSerializerOptions(dataAssetType))
		};

		return new EditorAssetFileSnapshot(asset.Id, asset.RelativeAssetPath, asset.RelativeSourcePath, JsonSerializer.Serialize(assetFile, AssetJson.SerializerOptions));
	}

	public void SaveMaterialAsset(AssetDatabaseEntry asset, MaterialAsset materialAsset)
	{
		_materialAssetStore.SaveAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath), materialAsset);
		SynchronizeRuntimeMaterial(asset.Id, materialAsset);
		_projectService.RefreshAssetSource(asset.RelativeSourcePath, asset.Id);
	}

	public void SaveDataAsset(AssetDatabaseEntry asset, Type dataAssetType, IDataAsset dataAsset)
	{
		_dataAssetStore.SaveAsset(_projectService.GetAbsolutePath(asset.RelativeAssetPath), dataAssetType, dataAsset);
		_projectService.RefreshAssetSource(asset.RelativeSourcePath);
	}

	public void ApplyMaterialAssetSnapshot(EditorAssetFileSnapshot snapshot)
	{
		WriteFileAtomically(_projectService.GetAbsolutePath(snapshot.RelativeAssetPath), snapshot.Json);
		var materialAsset = _materialAssetStore.LoadAsset(_projectService.GetAbsolutePath(snapshot.RelativeAssetPath));
		SynchronizeRuntimeMaterial(snapshot.AssetId, materialAsset);
		_projectService.RefreshAssetSource(snapshot.RelativeSourcePath, snapshot.AssetId);
	}

	public void ApplyDataAssetSnapshot(EditorAssetFileSnapshot snapshot)
	{
		WriteFileAtomically(_projectService.GetAbsolutePath(snapshot.RelativeAssetPath), snapshot.Json);
		_projectService.RefreshAssetSource(snapshot.RelativeSourcePath);
	}

	private EditorAssetFileSnapshot CaptureFileSnapshot(AssetDatabaseEntry asset)
	{
		return new EditorAssetFileSnapshot(
			asset.Id,
			asset.RelativeAssetPath,
			asset.RelativeSourcePath,
			File.ReadAllText(_projectService.GetAbsolutePath(asset.RelativeAssetPath)));
	}

	private string SerializeMaterialAsset(MaterialAsset materialAsset)
	{
		materialAsset.Version = MaterialAsset.CurrentVersion;
		materialAsset.AssetType = AssetType.Material;
		materialAsset.Textures ??= new MaterialTextureAssignments();
		return JsonSerializer.Serialize(materialAsset, AssetJson.SerializerOptions);
	}

	private void SynchronizeRuntimeMaterial(Guid assetId, MaterialAsset materialAsset)
	{
		var runtimeMaterial = AssetDatabase.GetInstance<Material>(assetId);
		if (runtimeMaterial is null)
		{
			return;
		}

		var descriptor = _materialTypeRegistry.GetDescriptor(materialAsset.MaterialType);
		var properties = materialAsset.GetActiveProperties();
		runtimeMaterial.Color = properties.BaseColor;
		runtimeMaterial.MetallicFactor = properties.MetallicFactor;
		runtimeMaterial.RoughnessFactor = properties.RoughnessFactor;
		runtimeMaterial.NormalScale = properties.NormalScale;
		runtimeMaterial.EmissiveFactor = properties.EmissiveFactor;
		runtimeMaterial.EmissiveIntensity = properties.EmissiveIntensity;
		runtimeMaterial.AlbedoTexture = ResolveTexture(properties.Textures.Albedo) ?? _textureFactory.GetWhiteTexture();
		runtimeMaterial.OrmTexture = ResolveTexture(properties.Textures.Orm) ?? _textureFactory.GetWhiteTexture();
		runtimeMaterial.NormalTexture = ResolveTexture(properties.Textures.Normal) ?? _textureFactory.GetNeutralNormalTexture();
		runtimeMaterial.EmissiveTexture = ResolveTexture(properties.Textures.Emissive) ?? _textureFactory.GetWhiteTexture();
		runtimeMaterial.AlphaMode = descriptor.RuntimeAlphaMode;
		runtimeMaterial.AlphaCutoff = materialAsset.AlphaCutoff;
		_renderGraph.RefreshMaterialResources(runtimeMaterial);
	}

	private static Texture? ResolveTexture(AssetRef<Texture> reference)
	{
		return reference.NodeId == Guid.Empty ? null : AssetDatabase.GetInstance<Texture>(reference.NodeId);
	}

	private static void WriteFileAtomically(string path, string contents)
	{
		var directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var tempPath = path + ".tmp";
		File.WriteAllText(tempPath, contents);
		File.Move(tempPath, path, true);
	}
}
