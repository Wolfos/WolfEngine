using System;
using System.Linq;
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
		var runtimeTextureName = GetRuntimeTextureName(context.AssetId, context.Asset.Name);

		var targetArtifact = context.Asset.Artifacts
			.Where(artifact => string.Equals(artifact.Kind, "RuntimeTexture", StringComparison.Ordinal))
			.FirstOrDefault(artifact => string.Equals(artifact.Target, _targetProvider.CurrentTarget, StringComparison.OrdinalIgnoreCase));
		if (targetArtifact is not null)
		{
			var runtimeTexture = TextureArtifactSerializer.Read(
				context.GetAbsolutePath(targetArtifact.RelativePath),
				runtimeTextureName);
			return _textureFactory.GetTexture(runtimeTexture);
		}

		if (string.IsNullOrWhiteSpace(summary.RelativeRuntimeArtifactPath) == false)
		{
			var runtimeTexture = TextureArtifactSerializer.Read(
				context.GetAbsolutePath(summary.RelativeRuntimeArtifactPath),
				runtimeTextureName);
			return _textureFactory.GetTexture(runtimeTexture);
		}

		if (string.IsNullOrWhiteSpace(summary.RelativeImportedPath) == false)
		{
			var importedTexture = ImportedTextureSerializer.Read(
				context.GetAbsolutePath(summary.RelativeImportedPath),
				runtimeTextureName);
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

	private static string GetRuntimeTextureName(Guid assetId, string assetName)
	{
		return string.IsNullOrWhiteSpace(assetName)
			? assetId.ToString("D")
			: $"{assetId:D}:{assetName}";
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
