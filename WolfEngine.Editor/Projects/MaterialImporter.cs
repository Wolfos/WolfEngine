using System;
using System.IO;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StbImageSharp;
using WolfEngine.AssetPipeline;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Projects;

public enum MaterialNormalMapFormat
{
	RgbYPlus,
	RgbYMinus,
	RgYPlus,
	RgYMinus
}

public enum MaterialTextureChannel
{
	R,
	G,
	B,
	A
}

public sealed class MaterialImportRequest
{
	public string MaterialName { get; set; } = string.Empty;
	public MaterialAssetType MaterialType { get; set; } = MaterialAssetType.Opaque;
	public string? AlbedoPath { get; set; }
	public string? NormalPath { get; set; }
	public MaterialNormalMapFormat NormalFormat { get; set; } = MaterialNormalMapFormat.RgbYPlus;
	public string? MetallicPath { get; set; }
	public MaterialTextureChannel MetallicChannel { get; set; } = MaterialTextureChannel.B;
	public string? RoughnessPath { get; set; }
	public MaterialTextureChannel RoughnessChannel { get; set; } = MaterialTextureChannel.G;
	public bool InvertRoughness { get; set; }
	public string? OcclusionPath { get; set; }
	public MaterialTextureChannel OcclusionChannel { get; set; } = MaterialTextureChannel.R;
	public string? EmissivePath { get; set; }
	public ColorRGBA BaseColor { get; set; } = ColorRGBA.White;
	public Vector3 EmissiveFactor { get; set; } = Vector3.One;
	public float MetallicFactor { get; set; } = 1.0f;
	public float RoughnessFactor { get; set; } = 1.0f;
	public float EmissiveIntensity { get; set; } = 1.0f;
	public float AlphaCutoff { get; set; } = 0.5f;
}

public readonly record struct MaterialImportOperationResult(bool Success, string? ErrorMessage)
{
	public static MaterialImportOperationResult Succeeded() => new(true, null);
	public static MaterialImportOperationResult Failed(string errorMessage) => new(false, errorMessage);
}

public interface IMaterialImporter
{
	MaterialImportOperationResult ImportMaterial(MaterialImportRequest request);
}

public sealed class MaterialImporter : IMaterialImporter
{
	private readonly IEditorProjectService _projectService;
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly IAssetMetadataStore _metadataStore;

	public MaterialImporter(
		IEditorProjectService projectService,
		IMaterialAssetStore materialAssetStore,
		IAssetMetadataStore metadataStore)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
	}

	public MaterialImportOperationResult ImportMaterial(MaterialImportRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (_projectService.HasOpenProject == false)
		{
			return MaterialImportOperationResult.Failed("Open or create a project before importing materials.");
		}

		var materialName = request.MaterialName.Trim();
		if (string.IsNullOrWhiteSpace(materialName))
		{
			return MaterialImportOperationResult.Failed("Enter a material name.");
		}

		if (materialName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			return MaterialImportOperationResult.Failed("Material name contains invalid characters.");
		}

		var relativeFolderPath = $"{AssetPipelinePaths.AssetsFolderName}/{AssetPipelinePaths.ImportedFolderName}/{materialName}";
		var absoluteFolderPath = _projectService.GetAbsolutePath(relativeFolderPath);
		if (Directory.Exists(absoluteFolderPath) || File.Exists(absoluteFolderPath))
		{
			return MaterialImportOperationResult.Failed($"Import target '{relativeFolderPath}' already exists.");
		}

		var createdFolder = false;
		try
		{
			Directory.CreateDirectory(absoluteFolderPath);
			createdFolder = true;

			var albedoTextureId = WritePassthroughTexture(
				request.AlbedoPath,
				relativeFolderPath,
				materialName,
				"Albedo",
				GetAlbedoSemantic(request.MaterialType));
			var normalTextureId = WriteNormalTexture(
				request.NormalPath,
				relativeFolderPath,
				materialName,
				request.NormalFormat);
			var ormTextureId = WriteOrmTexture(
				request.OcclusionPath,
				request.OcclusionChannel,
				request.MetallicPath,
				request.MetallicChannel,
				request.RoughnessPath,
				request.RoughnessChannel,
				request.InvertRoughness,
				relativeFolderPath,
				materialName);
			var emissiveTextureId = WritePassthroughTexture(
				request.EmissivePath,
				relativeFolderPath,
				materialName,
				"Emissive",
				TextureSemantic.Emissive);

			var materialAsset = CreateMaterialAsset(
				request,
				albedoTextureId,
				normalTextureId,
				ormTextureId,
				emissiveTextureId);
			var relativeMaterialPath = $"{relativeFolderPath}/{materialName}{MaterialAsset.FileExtension}";
			var absoluteMaterialPath = _projectService.GetAbsolutePath(relativeMaterialPath);
			_materialAssetStore.SaveAsset(absoluteMaterialPath, materialAsset);
			_metadataStore.Save(
				AssetFileExtensions.GetMetaPath(absoluteMaterialPath),
				CreateMetadata(AssetImporterIds.Material, AssetType.Material, materialName, Guid.NewGuid()));

			_projectService.ReloadAssetDatabase();
			return MaterialImportOperationResult.Succeeded();
		}
		catch (Exception ex)
		{
			if (createdFolder)
			{
				TryDeleteDirectory(absoluteFolderPath);
			}

			return MaterialImportOperationResult.Failed($"Failed to import material: {ex.Message}");
		}
	}

	private Guid WritePassthroughTexture(
		string? sourcePath,
		string relativeFolderPath,
		string materialName,
		string suffix,
		TextureSemantic semantic)
	{
		if (string.IsNullOrWhiteSpace(sourcePath))
		{
			return Guid.Empty;
		}

		using var image = LoadSourceImage(sourcePath);
		return WriteTextureAsset(image, relativeFolderPath, $"{materialName}_{suffix}.png", semantic);
	}

	private Guid WriteNormalTexture(
		string? sourcePath,
		string relativeFolderPath,
		string materialName,
		MaterialNormalMapFormat normalFormat)
	{
		if (string.IsNullOrWhiteSpace(sourcePath))
		{
			return Guid.Empty;
		}

		using var source = LoadSourceImage(sourcePath);
		using var output = new Image<Rgba32>(source.Width, source.Height);
		var invertY = normalFormat is MaterialNormalMapFormat.RgbYMinus or MaterialNormalMapFormat.RgYMinus;
		var deriveZ = normalFormat is MaterialNormalMapFormat.RgYPlus or MaterialNormalMapFormat.RgYMinus;

		for (var y = 0; y < source.Height; y++)
		{
			for (var x = 0; x < source.Width; x++)
			{
				var sourcePixel = source[x, y];
				var nx = (sourcePixel.R / 255.0f) * 2.0f - 1.0f;
				var ny = (sourcePixel.G / 255.0f) * 2.0f - 1.0f;
				if (invertY)
				{
					ny = -ny;
				}

				var nz = deriveZ
					? MathF.Sqrt(MathF.Max(1.0f - ((nx * nx) + (ny * ny)), 0.0f))
					: (sourcePixel.B / 255.0f) * 2.0f - 1.0f;
				var normal = Vector3.Normalize(new Vector3(nx, ny, nz));
				output[x, y] = new Rgba32(
					PackSignedNormalChannel(normal.X),
					PackSignedNormalChannel(normal.Y),
					PackSignedNormalChannel(normal.Z),
					sourcePixel.A);
			}
		}

		return WriteTextureAsset(output, relativeFolderPath, $"{materialName}_Normal.png", TextureSemantic.Normal);
	}

	private Guid WriteOrmTexture(
		string? occlusionPath,
		MaterialTextureChannel occlusionChannel,
		string? metallicPath,
		MaterialTextureChannel metallicChannel,
		string? roughnessPath,
		MaterialTextureChannel roughnessChannel,
		bool invertRoughness,
		string relativeFolderPath,
		string materialName)
	{
		var hasOcclusion = string.IsNullOrWhiteSpace(occlusionPath) == false;
		var hasMetallic = string.IsNullOrWhiteSpace(metallicPath) == false;
		var hasRoughness = string.IsNullOrWhiteSpace(roughnessPath) == false;
		if (hasOcclusion == false && hasMetallic == false && hasRoughness == false)
		{
			return Guid.Empty;
		}

		using var occlusionImage = hasOcclusion ? LoadSourceImage(occlusionPath!) : null;
		using var metallicImage = hasMetallic ? LoadSourceImage(metallicPath!) : null;
		using var roughnessImage = hasRoughness ? LoadSourceImage(roughnessPath!) : null;
		var outputWidth = Math.Max(occlusionImage?.Width ?? 0, Math.Max(metallicImage?.Width ?? 0, roughnessImage?.Width ?? 0));
		var outputHeight = Math.Max(occlusionImage?.Height ?? 0, Math.Max(metallicImage?.Height ?? 0, roughnessImage?.Height ?? 0));
		using var occlusionSource = ResizeIfNeeded(occlusionImage, outputWidth, outputHeight);
		using var metallicSource = ResizeIfNeeded(metallicImage, outputWidth, outputHeight);
		using var roughnessSource = ResizeIfNeeded(roughnessImage, outputWidth, outputHeight);
		using var output = new Image<Rgba32>(outputWidth, outputHeight, new Rgba32(255, 255, 255, 255));

		for (var y = 0; y < outputHeight; y++)
		{
			for (var x = 0; x < outputWidth; x++)
			{
				var occlusionValue = occlusionSource is null ? (byte)255 : ReadChannel(occlusionSource[x, y], occlusionChannel);
				var metallicValue = metallicSource is null ? (byte)255 : ReadChannel(metallicSource[x, y], metallicChannel);
				var roughnessValue = roughnessSource is null ? (byte)255 : ReadChannel(roughnessSource[x, y], roughnessChannel);
				if (roughnessSource is not null && invertRoughness)
				{
					roughnessValue = (byte)(255 - roughnessValue);
				}

				output[x, y] = new Rgba32(occlusionValue, roughnessValue, metallicValue, 255);
			}
		}

		return WriteTextureAsset(output, relativeFolderPath, $"{materialName}_ORM.png", TextureSemantic.MetallicRoughness);
	}

	private Guid WriteTextureAsset(Image<Rgba32> image, string relativeFolderPath, string fileName, TextureSemantic semantic)
	{
		var relativeTexturePath = $"{relativeFolderPath}/{fileName}";
		var absoluteTexturePath = _projectService.GetAbsolutePath(relativeTexturePath);
		var textureName = Path.GetFileNameWithoutExtension(fileName);
		var nodeId = Guid.NewGuid();

		image.SaveAsPng(absoluteTexturePath);
		_metadataStore.Save(
			AssetFileExtensions.GetMetaPath(absoluteTexturePath),
			CreateTextureMetadata(textureName, nodeId, semantic));
		return nodeId;
	}

	private static AssetSourceMetaFile CreateTextureMetadata(string name, Guid nodeId, TextureSemantic semantic)
	{
		var metadata = CreateMetadata(AssetImporterIds.Texture, AssetType.Texture2D, name, nodeId);
		metadata.TextureImportSettings = new TextureImportSettings
		{
			TextureSemantic = semantic
		};
		return metadata;
	}

	private static TextureSemantic GetAlbedoSemantic(MaterialAssetType materialType)
	{
		return materialType == MaterialAssetType.Opaque
			? TextureSemantic.BaseColor
			: TextureSemantic.BaseColorTransparent;
	}

	private static AssetSourceMetaFile CreateMetadata(string importerId, AssetType assetType, string name, Guid nodeId)
	{
		return new AssetSourceMetaFile
		{
			SourceId = Guid.NewGuid(),
			ImporterId = importerId,
			ImporterVersion = 1,
			SubAssets =
			[
				new AssetSubAssetManifestEntry
				{
					Key = "main",
					NodeId = nodeId,
					Type = assetType,
					Name = name
				}
			]
		};
	}

	private MaterialAsset CreateMaterialAsset(
		MaterialImportRequest request,
		Guid albedoTextureId,
		Guid normalTextureId,
		Guid ormTextureId,
		Guid emissiveTextureId)
	{
		var materialAsset = _materialAssetStore.CreateDefault(request.MaterialType);
		ApplyMaterialProperties(materialAsset.Opaque, request, albedoTextureId, normalTextureId, ormTextureId, emissiveTextureId);
		ApplyMaterialProperties(materialAsset.AlphaTest, request, albedoTextureId, normalTextureId, ormTextureId, emissiveTextureId);
		ApplyMaterialProperties(materialAsset.AlphaBlend, request, albedoTextureId, normalTextureId, ormTextureId, emissiveTextureId);
		materialAsset.AlphaTest.AlphaCutoff = request.AlphaCutoff;
		materialAsset.AlphaBlend.AlphaCutoff = request.AlphaCutoff;
		return materialAsset;
	}

	private static void ApplyMaterialProperties(
		MaterialSurfaceProperties properties,
		MaterialImportRequest request,
		Guid albedoTextureId,
		Guid normalTextureId,
		Guid ormTextureId,
		Guid emissiveTextureId)
	{
		properties.BaseColor = request.BaseColor;
		properties.MetallicFactor = request.MetallicFactor;
		properties.RoughnessFactor = request.RoughnessFactor;
		properties.EmissiveFactor = request.EmissiveFactor;
		properties.EmissiveIntensity = MathF.Max(0.0f, request.EmissiveIntensity);
		properties.Textures.Albedo = new AssetRef<Texture> { NodeId = albedoTextureId };
		properties.Textures.Normal = new AssetRef<Texture> { NodeId = normalTextureId };
		properties.Textures.Orm = new AssetRef<Texture> { NodeId = ormTextureId };
		properties.Textures.Emissive = new AssetRef<Texture> { NodeId = emissiveTextureId };
	}

	private static Image<Rgba32>? ResizeIfNeeded(Image<Rgba32>? image, int width, int height)
	{
		if (image is null)
		{
			return null;
		}

		if (image.Width == width && image.Height == height)
		{
			return image.Clone();
		}

		return image.Clone(context => context.Resize(width, height));
	}

	private static byte ReadChannel(Rgba32 pixel, MaterialTextureChannel channel)
	{
		return channel switch
		{
			MaterialTextureChannel.R => pixel.R,
			MaterialTextureChannel.G => pixel.G,
			MaterialTextureChannel.B => pixel.B,
			MaterialTextureChannel.A => pixel.A,
			_ => pixel.R
		};
	}

	private static Image<Rgba32> LoadSourceImage(string sourcePath)
	{
		if (string.Equals(Path.GetExtension(sourcePath), ".psd", StringComparison.OrdinalIgnoreCase))
		{
			var imageData = File.ReadAllBytes(sourcePath);
			var image = ImageResult.FromMemory(imageData, ColorComponents.RedGreenBlueAlpha);
			return Image.LoadPixelData<Rgba32>(image.Data, image.Width, image.Height);
		}

		return Image.Load<Rgba32>(sourcePath);
	}

	private static byte PackSignedNormalChannel(float value)
	{
		var mappedValue = (Math.Clamp(value, -1.0f, 1.0f) * 0.5f) + 0.5f;
		return (byte)Math.Clamp(MathF.Round(mappedValue * 255.0f), 0.0f, 255.0f);
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch
		{
			// Best-effort cleanup; original import failure is more actionable.
		}
	}
}
