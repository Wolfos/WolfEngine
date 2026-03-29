using System;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public sealed class DataAssetRuntimeResolver : IDataAssetRuntimeResolver
{
	private readonly IDataAssetStore _dataAssetStore;

	public DataAssetRuntimeResolver(IDataAssetStore dataAssetStore)
	{
		_dataAssetStore = dataAssetStore ?? throw new ArgumentNullException(nameof(dataAssetStore));
	}

	public object? Resolve(RuntimeAssetResolveContext context)
	{
		var loadedAsset = _dataAssetStore.LoadAsset(context.GetAbsolutePath(context.Asset.RelativeAssetPath)).Asset;
		if (context.RuntimeType.IsInstanceOfType(loadedAsset) == false)
		{
			throw new InvalidOperationException(
				$"Data asset '{context.AssetId}' resolved to '{loadedAsset.GetType().FullName}', which cannot be assigned to '{context.RuntimeType.FullName}'.");
		}

		return loadedAsset;
	}
}

public sealed class MaterialRuntimeAssetResolver : IMaterialRuntimeAssetResolver
{
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly IMaterialTypeRegistry _materialTypeRegistry;
	private readonly IMaterialFactory _materialFactory;

	public MaterialRuntimeAssetResolver(
		IMaterialAssetStore materialAssetStore,
		IMaterialTypeRegistry materialTypeRegistry,
		IMaterialFactory materialFactory)
	{
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_materialTypeRegistry = materialTypeRegistry ?? throw new ArgumentNullException(nameof(materialTypeRegistry));
		_materialFactory = materialFactory ?? throw new ArgumentNullException(nameof(materialFactory));
	}

	public object Resolve(RuntimeAssetResolveContext context)
	{
		var materialAsset = _materialAssetStore.LoadAsset(context.GetAbsolutePath(context.Asset.RelativeAssetPath));
		var descriptor = _materialTypeRegistry.GetDescriptor(materialAsset.MaterialType);
		var properties = materialAsset.GetActiveProperties();

		return _materialFactory.GetMaterial(
			shader: descriptor.ShaderPath,
			color: properties.BaseColor,
			metallicFactor: properties.MetallicFactor,
			roughnessFactor: properties.RoughnessFactor,
			albedoTexture: ResolveTexture(properties.Textures.Albedo),
			metallicRoughnessTexture: ResolveTexture(properties.Textures.MetallicRoughness),
			normalTexture: ResolveTexture(properties.Textures.Normal),
			emissiveTexture: ResolveTexture(properties.Textures.Emissive),
			occlusionTexture: ResolveTexture(properties.Textures.Occlusion),
			alphaMode: descriptor.RuntimeAlphaMode,
			alphaCutoff: properties switch
			{
				AlphaTestMaterialProperties alphaTest => alphaTest.AlphaCutoff,
				AlphaBlendMaterialProperties alphaBlend => alphaBlend.AlphaCutoff,
				_ => 0.5f
			});
	}

	private static Texture? ResolveTexture(AssetRef<Texture> reference)
	{
		return reference.NodeId == Guid.Empty ? null : reference.Asset;
	}
}

public sealed class TextureRuntimeAssetResolver : ITextureRuntimeAssetResolver
{
	private readonly ITextureFactory _textureFactory;
	private readonly IRuntimeArtifactTargetProvider _targetProvider;

	public TextureRuntimeAssetResolver(
		ITextureFactory textureFactory,
		IRuntimeArtifactTargetProvider targetProvider)
	{
		_textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
		_targetProvider = targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));
	}

	public object Resolve(RuntimeAssetResolveContext context)
	{
		var summary = context.Asset.TextureSummary
		              ?? throw new InvalidOperationException($"Texture node '{context.AssetId}' is missing its texture summary.");
		if (string.IsNullOrWhiteSpace(summary.RelativeRuntimeArtifactPath) == false)
		{
			var importedTexture = TextureRawImageSerializer.Read(
				context.GetAbsolutePath(summary.RelativeRuntimeArtifactPath),
				context.Asset.Name);
			return _textureFactory.GetTexture(importedTexture);
		}

		if (string.IsNullOrWhiteSpace(summary.RelativeImportedPath) == false)
		{
			var importedTexture = TextureRawImageSerializer.Read(
				context.GetAbsolutePath(summary.RelativeImportedPath),
				context.Asset.Name);
			return _textureFactory.GetTexture(importedTexture);
		}

		if (string.IsNullOrWhiteSpace(summary.RelativeSourceAssetPath) == false)
		{
			return _textureFactory.LoadFromFile(
				context.GetAbsolutePath(summary.RelativeSourceAssetPath),
				summary.IsSrgb);
		}

		throw new InvalidOperationException(
			$"Texture node '{context.AssetId}' does not expose a runtime artifact, imported texture, or source file.");
	}
}

public sealed class MeshRuntimeAssetResolver : IMeshRuntimeAssetResolver
{
	public object Resolve(RuntimeAssetResolveContext context)
	{
		var summary = context.Asset.MeshSummary
		              ?? throw new InvalidOperationException($"Mesh node '{context.AssetId}' is missing its mesh summary.");
		var absoluteMeshPath = context.GetAbsolutePath(summary.RelativeImportedMeshPath);
		var meshFile = ImportedMeshSerializer.Read(absoluteMeshPath);
		return new Mesh(
			meshFile.Vertices,
			meshFile.Indices,
			meshFile.Normals,
			meshFile.UVs,
			meshFile.Tangents);
	}
}
