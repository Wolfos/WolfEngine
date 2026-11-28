using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UnityImport;

/// <summary>How a Unity mask map becomes an ORM map: each channel remapped from 0..1 into its range.</summary>
public sealed record UnityMaskConversion(
	string Path,
	Vector2 MetallicRange,
	Vector2 OcclusionRange,
	Vector2 SmoothnessRange);

/// <summary>What a prefab is in the scene, which decides how its foliage materials shade and move.</summary>
public enum UnityPrefabRole
{
	/// <summary>A tree, bush or prop: leaves sway with the tree and shade as one crown.</summary>
	Tree = 0,

	/// <summary>Terrain detail such as grass, ferns and flowers: plants bend from their base and shade like the ground.</summary>
	GroundCover = 1
}

/// <summary>A Unity material as a WolfEngine material, before any texture is written.</summary>
/// <param name="BaseColor">Linear RGBA.</param>
/// <param name="Foliage">The foliage shading settings, for a <see cref="MaterialAssetType.Foliage"/> material; null otherwise.</param>
/// <param name="Wind">How the material moves in the scene's wind.</param>
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
	FoliageMaterialSettings? Foliage,
	MaterialWind Wind,
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
	public static UnityMaterialPlan Describe(UnityMaterialExport material, UnityPrefabRole role = UnityPrefabRole.Tree)
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

		var shader = material.Shader ?? string.Empty;
		var isFoliage = shader.EndsWith("/Foliage/Foliage", StringComparison.Ordinal) ||
		                shader.EndsWith("/Foliage/Cross", StringComparison.Ordinal);
		var isBark = shader.EndsWith("/Foliage/Bark", StringComparison.Ordinal);
		FoliageMaterialSettings? foliage = null;
		if (isFoliage)
		{
			type = MaterialAssetType.Foliage;
			foliage = DescribeFoliage(material, role, color);
		}
		else if (material.Colors.ContainsKey("_DryColor") && material.Colors.ContainsKey("_HealthyColor"))
		{
			approximations.Add("tinted with the healthy colour only; Unity varies it towards the dry colour per instance");
		}

		var wind = DescribeWind(material, role, isFoliage, isBark);

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
			foliage,
			wind,
			approximations);
	}

	/// <summary>
	/// NatureManufacture's foliage and cross shaders as the foliage shading model. Their thickness (0 thin, 1 thick)
	/// lowers translucency. The base colour is already the healthy colour, so the tints are white and the dry colour
	/// relative to it, varied fully per instance as Unity does.
	/// </summary>
	private static FoliageMaterialSettings DescribeFoliage(UnityMaterialExport material, UnityPrefabRole role, Vector4 healthy)
	{
		var thickness = Math.Clamp(FindFloat(material, "_Thickness") ?? 0.5f, 0.0f, 1.0f);
		var settings = new FoliageMaterialSettings
		{
			Translucency = 0.6f * (1.0f - (0.5f * thickness)),
			NormalMode = role == UnityPrefabRole.GroundCover ? FoliageNormalMode.Up : FoliageNormalMode.Spherical,
			NormalBlend = role == UnityPrefabRole.GroundCover ? 0.7f : 0.5f
		};

		if (FindColor(material, "_DryColor") is { } dry && material.Colors.ContainsKey("_HealthyColor"))
		{
			settings.TintVariation = 1.0f;
			settings.HealthyTint = ColorRGBA.White;
			settings.DryTint = new ColorRGBA(
				Ratio(SrgbToLinear(dry.X), SrgbToLinear(healthy.X)),
				Ratio(SrgbToLinear(dry.Y), SrgbToLinear(healthy.Y)),
				Ratio(SrgbToLinear(dry.Z), SrgbToLinear(healthy.Z)),
				1.0f);
		}

		return settings;
	}

	private static float Ratio(float value, float reference)
	{
		return reference > 1e-4f ? Math.Clamp(value / reference, 0.0f, 1.0f) : 1.0f;
	}

	/// <summary>
	/// Leaves and cross cards sway with their tree and flutter; bark sways without fluttering so trunks carry the
	/// crown; ground cover bends from its base. The pack's <c>_no_wind</c> variants stay still.
	/// </summary>
	private static MaterialWind DescribeWind(UnityMaterialExport material, UnityPrefabRole role, bool isFoliage, bool isBark)
	{
		if ((isFoliage || isBark) == false ||
		    material.Name.Contains("no_wind", StringComparison.OrdinalIgnoreCase))
		{
			return new MaterialWind();
		}

		if (isBark)
		{
			return new MaterialWind
			{
				Mode = WindMode.Tree,
				FlutterScale = 0.0f
			};
		}

		return new MaterialWind
		{
			Mode = role == UnityPrefabRole.GroundCover ? WindMode.Grass : WindMode.Tree
		};
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
