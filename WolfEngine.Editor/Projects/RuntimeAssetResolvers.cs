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

	private static Texture? ResolveTexture(AssetLink<Texture> link)
	{
		return link.Id == Guid.Empty ? null : link.Asset;
	}
}

public sealed class TextureRuntimeAssetResolver : ITextureRuntimeAssetResolver
{
	private readonly ITextureAssetStore _textureAssetStore;
	private readonly ITextureFactory _textureFactory;
	private readonly IRuntimeArtifactTargetProvider _targetProvider;

	public TextureRuntimeAssetResolver(
		ITextureAssetStore textureAssetStore,
		ITextureFactory textureFactory,
		IRuntimeArtifactTargetProvider targetProvider)
	{
		_textureAssetStore = textureAssetStore ?? throw new ArgumentNullException(nameof(textureAssetStore));
		_textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
		_targetProvider = targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));
	}

	public object Resolve(RuntimeAssetResolveContext context)
	{
		var textureAsset = _textureAssetStore.LoadAsset(context.GetAbsolutePath(context.Asset.RelativeAssetPath));
		var textureState = _textureAssetStore.LoadState(context.GetAbsolutePath(context.Asset.GetEffectiveRelativeStatePath()));
		var currentTarget = _targetProvider.CurrentTarget;

		var artifact = textureState.Artifacts
			.FirstOrDefault(candidate =>
				string.Equals(candidate.Kind, "RuntimeTexture", StringComparison.OrdinalIgnoreCase) &&
				string.Equals(candidate.Target, currentTarget, StringComparison.OrdinalIgnoreCase))
			?? textureState.Artifacts.FirstOrDefault(candidate =>
				string.Equals(candidate.Kind, "RuntimeTexture", StringComparison.OrdinalIgnoreCase) &&
				string.IsNullOrWhiteSpace(candidate.Target));

		if (artifact is not null && string.IsNullOrWhiteSpace(artifact.RelativePath) == false)
		{
			var importedTexture = TextureRawImageSerializer.Read(
				context.GetAbsolutePath(artifact.RelativePath),
				context.Asset.Name);
			return _textureFactory.GetTexture(importedTexture);
		}

		return _textureFactory.LoadFromFile(
			context.GetAbsolutePath(textureAsset.RelativeSourceAssetPath),
			textureAsset.ImportSettings.IsSrgb);
	}
}
