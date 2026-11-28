using System.Numerics;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UnityImport;

/// <summary>How a Unity mask map becomes an ORM map: each channel remapped from 0..1 into its range.</summary>
public sealed record UnityMaskConversion(
	string Path,
	Vector2 MetallicRange,
	Vector2 OcclusionRange,
	Vector2 SmoothnessRange);

/// <summary>A Unity material as a WolfEngine material, before any texture is written.</summary>
/// <param name="BaseColor">Linear RGBA.</param>
/// <param name="Approximations">What the WolfEngine material leaves out or simplifies, for the import report.</param>
public sealed record UnityMaterialPlan(
	string Name,
	MaterialAssetType Type,
	float AlphaCutoff,
	bool DoubleSided,
	Vector4 BaseColor,
	float NormalScale,
	float MetallicFactor,
	float RoughnessFactor,
	string? AlbedoPath,
	string? NormalPath,
	UnityMaskConversion? Mask,
	IReadOnlyList<string> Approximations);

/// <summary>
/// Reads a Unity material's exported properties as a WolfEngine material. Every shader the pack uses (HDRP Lit, Layered
/// Lit and NatureManufacture's foliage, bark and cover shaders) follows HDRP Lit's property names, optionally with a layer
/// prefix (<c>_TrunkBaseColorMap</c>, <c>_BaseNormalMap</c>) or a layer suffix (<c>_BaseColorMap0</c>). The first layer is
/// found from the textures present and converted; other layers are reported as approximations.
/// </summary>
/// <remarks>
/// HDRP mask maps hold metallic, occlusion, a detail mask and smoothness in R, G, B and A, each remapped into the
/// material's range. With no mask map, the material's metallic and smoothness values are used as factors.
/// </remarks>
public static class UnityMaterialConversion
{
	public static UnityMaterialPlan Describe(UnityMaterialExport material)
	{
		ArgumentNullException.ThrowIfNull(material);

		var approximations = new List<string>();
		var layer = FindLayer(material);
		var albedo = FindTexture(material, layer.Name("BaseColorMap"), "_BaseColorMap", "_MainTex", "_BaseMap");
		var normal = FindTexture(material, layer.Name("NormalMap"), "_NormalMap", "_BumpMap");
		var mask = FindTexture(material, layer.Name("MaskMap"), "_MaskMap");

		var color = FindColor(material, layer.Name("BaseColor"), "_HealthyColor", "_BaseColor", "_Color") ?? Vector4.One;
		var linearColor = new Vector4(SrgbToLinear(color.X), SrgbToLinear(color.Y), SrgbToLinear(color.Z), color.W);

		var alphaTest = material.Keywords.Contains("_ALPHATEST_ON") || FindFloat(material, "_AlphaCutoffEnable") > 0.5f;
		var transparent = FindFloat(material, "_SurfaceType") > 0.5f;
		var type = transparent
			? MaterialAssetType.AlphaBlend
			: alphaTest ? MaterialAssetType.AlphaTest : MaterialAssetType.Opaque;
		var doubleSided = material.Keywords.Contains("_DOUBLESIDED_ON") ||
		                  FindFloat(material, "_DoubleSidedEnable") > 0.5f;

		UnityMaskConversion? maskConversion = null;
		var metallicFactor = 1.0f;
		var roughnessFactor = 1.0f;
		if (mask is not null)
		{
			maskConversion = new UnityMaskConversion(
				mask.Path,
				FindRange(material, layer, "Metallic"),
				FindRange(material, layer, "AO"),
				FindRange(material, layer, "Smoothness"));
		}
		else
		{
			metallicFactor = FindFloat(material, layer.Name("Metallic"), "_Metallic") ?? 0.0f;
			roughnessFactor = 1.0f - (FindFloat(material, layer.Name("Smoothness"), "_Smoothness", "_Glossiness") ?? 0.5f);
		}

		foreach (var texture in new[] { albedo, normal, mask })
		{
			if (texture is not null && (texture.Scale.Any(value => MathF.Abs(value - 1.0f) > 0.0001f) || texture.Offset.Any(value => MathF.Abs(value) > 0.0001f)))
			{
				approximations.Add($"texture '{System.IO.Path.GetFileName(texture.Path)}' is tiled or offset in Unity; WolfEngine materials do not tile");
			}
		}

		if (layer.OtherLayers.Count > 0)
		{
			approximations.Add($"only the {layer.Description} layer is converted; {string.Join(", ", layer.OtherLayers)} left out");
		}

		if (material.Colors.ContainsKey("_DryColor") && material.Colors.ContainsKey("_HealthyColor"))
		{
			approximations.Add("tinted with the healthy colour only; Unity varies it towards the dry colour per instance");
		}

		return new UnityMaterialPlan(
			material.Name,
			type,
			FindFloat(material, "_AlphaCutoff", "_Cutoff") ?? 0.5f,
			doubleSided,
			linearColor,
			FindFloat(material, layer.Name("NormalScale"), "_NormalScale", "_BumpScale") ?? 1.0f,
			metallicFactor,
			roughnessFactor,
			albedo?.Path,
			normal?.Path,
			maskConversion,
			approximations);
	}

	public static float SrgbToLinear(float value)
	{
		return value <= 0.04045f
			? value / 12.92f
			: MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
	}

	/// <summary>The layer the material's base colour map belongs to, and which other layers it has.</summary>
	private static LayerNaming FindLayer(UnityMaterialExport material)
	{
		var textures = material.Textures.Keys.ToList();
		if (material.Textures.ContainsKey("_TrunkBaseColorMap"))
		{
			var others = textures.Any(name => name.StartsWith("_Bark", StringComparison.Ordinal)) ? new List<string> { "bark" } : [];
			return new LayerNaming("Trunk", string.Empty, "trunk", others);
		}

		if (material.Textures.ContainsKey("_BaseColorMap0"))
		{
			var others = Enumerable.Range(1, 3)
				.Where(index => material.Textures.ContainsKey($"_BaseColorMap{index}"))
				.Select(index => $"layer {index}")
				.ToList();
			return new LayerNaming(string.Empty, "0", "first", others);
		}

		if (material.Textures.ContainsKey("_BaseNormalMap") || material.Textures.ContainsKey("_BaseMaskMap"))
		{
			var others = textures.Any(name => name.StartsWith("_Cover", StringComparison.Ordinal)) ||
			             material.Colors.ContainsKey("_CoverBaseColor")
				? new List<string> { "the top cover" }
				: [];
			return new LayerNaming("Base", string.Empty, "base", others);
		}

		return new LayerNaming(string.Empty, string.Empty, "only", []);
	}

	private static Vector2 FindRange(UnityMaterialExport material, LayerNaming layer, string channel)
	{
		var min = FindFloat(material, layer.Name($"{channel}RemapMin"), $"_{channel}RemapMin") ?? 0.0f;
		var max = FindFloat(material, layer.Name($"{channel}RemapMax"), $"_{channel}RemapMax") ?? 1.0f;

		return new Vector2(min, max);
	}

	private static UnityMaterialTextureExport? FindTexture(UnityMaterialExport material, params string[] names)
	{
		foreach (var name in names)
		{
			if (material.Textures.TryGetValue(name, out var texture) && string.IsNullOrWhiteSpace(texture.Path) == false)
			{
				return texture;
			}
		}

		return null;
	}

	private static Vector4? FindColor(UnityMaterialExport material, params string[] names)
	{
		foreach (var name in names)
		{
			if (material.Colors.TryGetValue(name, out var color) && color.Length >= 4)
			{
				return new Vector4(color[0], color[1], color[2], color[3]);
			}
		}

		return null;
	}

	private static float? FindFloat(UnityMaterialExport material, params string[] names)
	{
		foreach (var name in names)
		{
			if (material.Floats.TryGetValue(name, out var value))
			{
				return value;
			}
		}

		return null;
	}

	/// <param name="Prefix">Inserted after the underscore: <c>Trunk</c> gives <c>_TrunkNormalMap</c>.</param>
	/// <param name="Suffix">Appended: <c>0</c> gives <c>_NormalMap0</c>.</param>
	private sealed record LayerNaming(string Prefix, string Suffix, string Description, List<string> OtherLayers)
	{
		public string Name(string property) => $"_{Prefix}{property}{Suffix}";
	}
}
