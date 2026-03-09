using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public interface IMaterialAssetRuntimeBuilder
{
	Material Build(Guid materialAssetId);
}

public sealed class MaterialAssetRuntimeBuilder : IMaterialAssetRuntimeBuilder
{
	private readonly IEditorProjectService _projectService;
	private readonly IMaterialTypeRegistry _materialTypeRegistry;
	private readonly ITextureFactory _textureFactory;
	private readonly IMaterialFactory _materialFactory;

	public MaterialAssetRuntimeBuilder(
		IEditorProjectService projectService,
		IMaterialTypeRegistry materialTypeRegistry,
		ITextureFactory textureFactory,
		IMaterialFactory materialFactory)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_materialTypeRegistry = materialTypeRegistry ?? throw new ArgumentNullException(nameof(materialTypeRegistry));
		_textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
		_materialFactory = materialFactory ?? throw new ArgumentNullException(nameof(materialFactory));
	}

	public Material Build(Guid materialAssetId)
	{
		var materialAsset = AssetDatabase.GetInstance<MaterialAssetFile>(materialAssetId)
			?? throw new InvalidOperationException($"Material asset '{materialAssetId}' was not found in the current project.");
		var descriptor = _materialTypeRegistry.GetDescriptor(materialAsset.MaterialType);
		var properties = materialAsset.GetActiveProperties();

		return _materialFactory.GetMaterial(
			shader: descriptor.ShaderPath,
			color: properties.BaseColor,
			metallicFactor: properties.MetallicFactor,
			roughnessFactor: properties.RoughnessFactor,
			albedoTexture: ResolveTexture(properties.Textures.Albedo, isSrgb: true),
			metallicRoughnessTexture: ResolveTexture(properties.Textures.MetallicRoughness, isSrgb: false),
			normalTexture: ResolveTexture(properties.Textures.Normal, isSrgb: false),
			emissiveTexture: ResolveTexture(properties.Textures.Emissive, isSrgb: true),
			occlusionTexture: ResolveTexture(properties.Textures.Occlusion, isSrgb: false),
			alphaMode: descriptor.RuntimeAlphaMode,
			alphaCutoff: properties switch
			{
				AlphaTestMaterialProperties alphaTest => alphaTest.AlphaCutoff,
				AlphaBlendMaterialProperties alphaBlend => alphaBlend.AlphaCutoff,
				_ => 0.5f
			});
	}

	private Texture? ResolveTexture(Guid? textureAssetId, bool isSrgb)
	{
		if (textureAssetId.HasValue == false)
		{
			return null;
		}

		if (_projectService.TryGetAsset(textureAssetId.Value, out var asset) == false || asset.Type != AssetType.Texture2D)
		{
			return null;
		}

		var absoluteTexturePath = _projectService.GetAbsolutePath(asset.RelativeAssetPath);
		if (File.Exists(absoluteTexturePath) == false)
		{
			return null;
		}

		return _textureFactory.LoadFromFile(absoluteTexturePath, isSrgb);
	}
}
