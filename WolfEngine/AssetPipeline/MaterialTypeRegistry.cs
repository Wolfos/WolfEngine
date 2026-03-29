using System;
using System.Collections.Generic;

namespace WolfEngine.AssetPipeline;

public enum MaterialPropertyKind
{
	BaseColor,
	MetallicFactor,
	RoughnessFactor,
	AlphaCutoff,
	AlbedoTexture,
	MetallicRoughnessTexture,
	NormalTexture,
	EmissiveTexture,
	OcclusionTexture
}

public sealed class MaterialPropertyDefinition
{
	public required MaterialPropertyKind Kind { get; init; }
	public required string DisplayName { get; init; }
}

public sealed class MaterialTypeDescriptor
{
	public required MaterialAssetType Type { get; init; }
	public required string DisplayName { get; init; }
	public required string ShaderPath { get; init; }
	public required AlphaMode RuntimeAlphaMode { get; init; }
	public required IReadOnlyList<MaterialPropertyDefinition> Properties { get; init; }
}

public interface IMaterialTypeRegistry
{
	IReadOnlyList<MaterialTypeDescriptor> GetAll();
	MaterialTypeDescriptor GetDescriptor(MaterialAssetType type);
	IReadOnlyList<MaterialPropertyDefinition> GetPropertiesForMaterialType(MaterialAssetType type);
}

public sealed class MaterialTypeRegistry : IMaterialTypeRegistry
{
	private static readonly IReadOnlyList<MaterialPropertyDefinition> SharedProperties =
	[
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.BaseColor, DisplayName = "Base Color" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.MetallicFactor, DisplayName = "Metallic" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.RoughnessFactor, DisplayName = "Roughness" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.AlbedoTexture, DisplayName = "Albedo" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.MetallicRoughnessTexture, DisplayName = "Metallic / Roughness" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.NormalTexture, DisplayName = "Normal" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.EmissiveTexture, DisplayName = "Emissive" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.OcclusionTexture, DisplayName = "Occlusion" }
	];
	
	private static readonly IReadOnlyList<MaterialPropertyDefinition> AlphaTestProperties =
	[
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.BaseColor, DisplayName = "Base Color" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.MetallicFactor, DisplayName = "Metallic" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.RoughnessFactor, DisplayName = "Roughness" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.AlphaCutoff, DisplayName = "Alpha Cutoff" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.AlbedoTexture, DisplayName = "Albedo" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.MetallicRoughnessTexture, DisplayName = "Metallic / Roughness" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.NormalTexture, DisplayName = "Normal" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.EmissiveTexture, DisplayName = "Emissive" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.OcclusionTexture, DisplayName = "Occlusion" }
	];

	private static readonly IReadOnlyList<MaterialPropertyDefinition> AlphaBlendProperties =
	[
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.BaseColor, DisplayName = "Base Color" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.MetallicFactor, DisplayName = "Metallic" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.RoughnessFactor, DisplayName = "Roughness" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.AlbedoTexture, DisplayName = "Albedo" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.MetallicRoughnessTexture, DisplayName = "Metallic / Roughness" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.NormalTexture, DisplayName = "Normal" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.EmissiveTexture, DisplayName = "Emissive" },
		new MaterialPropertyDefinition { Kind = MaterialPropertyKind.OcclusionTexture, DisplayName = "Occlusion" }
	];

	private readonly IReadOnlyList<MaterialTypeDescriptor> _descriptors =
	[
		new MaterialTypeDescriptor
		{
			Type = MaterialAssetType.Opaque,
			DisplayName = "Opaque",
			ShaderPath = "gbuffer.slang",
			RuntimeAlphaMode = AlphaMode.Opaque,
			Properties = SharedProperties
		},
		new MaterialTypeDescriptor
		{
			Type = MaterialAssetType.AlphaTest,
			DisplayName = "AlphaTest",
			ShaderPath = "gbuffer.slang",
			RuntimeAlphaMode = AlphaMode.AlphaTest,
			Properties = AlphaTestProperties
		},
		new MaterialTypeDescriptor
		{
			Type = MaterialAssetType.AlphaBlend,
			DisplayName = "AlphaBlend",
			ShaderPath = "gbuffer.slang",
			RuntimeAlphaMode = AlphaMode.AlphaBlend,
			Properties = AlphaBlendProperties
		}
	];

	public IReadOnlyList<MaterialTypeDescriptor> GetAll() => _descriptors;

	public MaterialTypeDescriptor GetDescriptor(MaterialAssetType type)
	{
		for (var i = 0; i < _descriptors.Count; i++)
		{
			if (_descriptors[i].Type == type)
			{
				return _descriptors[i];
			}
		}

		throw new InvalidOperationException($"Unsupported material asset type '{type}'.");
	}

	public IReadOnlyList<MaterialPropertyDefinition> GetPropertiesForMaterialType(MaterialAssetType type)
	{
		return GetDescriptor(type).Properties;
	}
}
