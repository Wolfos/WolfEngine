using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

public sealed class TerrainBrushPreviewDecalController
{
	private const int PreviewTextureSize = 128;
	private const float RimWidthNormalized = 0.035f;
	private static readonly ColorRGBA PreviewTint = new(0.22f, 0.48f, 1.0f, 1.0f);

	private readonly ITextureFactory _textureFactory;
	private readonly Dictionary<int, PreviewTextureSet> _textureCache = new();

	public TerrainBrushPreviewDecalController(ITextureFactory textureFactory)
	{
		_textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
	}

	public void ApplyPreview(ref TerrainComponent terrain, Vector3 localHitPosition, float radiusMeters, float falloff)
	{
		radiusMeters = MathF.Max(radiusMeters, 0.1f);
		falloff = MathF.Max(falloff, 0.1f);
		var textures = GetOrCreateTextures(falloff);
		var thickness = ResolveProjectorThickness(terrain, radiusMeters);

		terrain.AuthoringBrushPreviewDecal = new DecalProjector
		{
			Enabled = true,
			Size = new Vector3(radiusMeters * 2.0f, radiusMeters * 2.0f, thickness),
			Tint = PreviewTint,
			AlbedoTexture = textures.Albedo,
			EmissiveTexture = textures.Emissive,
			AlbedoOpacity = 1.0f,
			EmissiveOpacity = 1.0f,
			EmissiveIntensity = 1.6f,
			ChannelMask = DecalChannelMask.Albedo | DecalChannelMask.Emissive
		};
		terrain.AuthoringBrushPreviewLocalTransform = CreateProjectorLocalTransform(localHitPosition);
	}

	public void ClearPreview(ref TerrainComponent terrain)
	{
		terrain.AuthoringBrushPreviewDecal = null;
		terrain.AuthoringBrushPreviewLocalTransform = Matrix4x4.Identity;
	}

	internal static float ComputeFillMask(float distanceNormalized, float falloff)
	{
		if (distanceNormalized >= 1.0f)
		{
			return 0.0f;
		}

		return MathF.Pow(1.0f - MathF.Max(distanceNormalized, 0.0f), MathF.Max(falloff, 0.1f));
	}

	internal static float ComputeRimMask(float distanceNormalized)
	{
		if (distanceNormalized >= 1.0f)
		{
			return 0.0f;
		}

		return Math.Clamp((distanceNormalized - (1.0f - RimWidthNormalized)) / RimWidthNormalized, 0.0f, 1.0f);
	}

	internal static Matrix4x4 CreateProjectorLocalTransform(Vector3 localHitPosition)
	{
		return new Matrix4x4(
			1.0f, 0.0f, 0.0f, 0.0f,
			0.0f, 0.0f, -1.0f, 0.0f,
			0.0f, 1.0f, 0.0f, 0.0f,
			localHitPosition.X, localHitPosition.Y, localHitPosition.Z, 1.0f);
	}

	internal static float ResolveProjectorThickness(in TerrainComponent terrain, float radiusMeters)
	{
		return MathF.Max(MathF.Max(terrain.GetResolvedHeightScale() * 2.0f, radiusMeters * 0.5f), 4.0f);
	}

	private PreviewTextureSet GetOrCreateTextures(float falloff)
	{
		var key = (int)MathF.Round(MathF.Max(falloff, 0.1f) * 100.0f);
		if (_textureCache.TryGetValue(key, out var textures))
		{
			return textures;
		}

		var quantizedFalloff = key / 100.0f;
		var albedo = _textureFactory.GetTexture(new Texture(
			$"terrain_brush_preview_albedo_{key}",
			PreviewTextureSize,
			PreviewTextureSize,
			true,
			TextureFormat.Rgba8Unorm,
			[new TextureMipData(PreviewTextureSize, PreviewTextureSize, BuildPreviewTextureData(quantizedFalloff, emissive: false))]));
		var emissive = _textureFactory.GetTexture(new Texture(
			$"terrain_brush_preview_emissive_{key}",
			PreviewTextureSize,
			PreviewTextureSize,
			true,
			TextureFormat.Rgba8Unorm,
			[new TextureMipData(PreviewTextureSize, PreviewTextureSize, BuildPreviewTextureData(quantizedFalloff, emissive: true))]));
		textures = new PreviewTextureSet(albedo, emissive);
		_textureCache[key] = textures;
		return textures;
	}

	private static byte[] BuildPreviewTextureData(float falloff, bool emissive)
	{
		var data = new byte[PreviewTextureSize * PreviewTextureSize * 4];
		for (var y = 0; y < PreviewTextureSize; y++)
		{
			for (var x = 0; x < PreviewTextureSize; x++)
			{
				var u = ((x + 0.5f) / PreviewTextureSize) * 2.0f - 1.0f;
				var v = ((y + 0.5f) / PreviewTextureSize) * 2.0f - 1.0f;
				var distanceNormalized = MathF.Min(MathF.Sqrt((u * u) + (v * v)), 1.0f);
				var fill = ComputeFillMask(distanceNormalized, falloff);
				var rim = ComputeRimMask(distanceNormalized);

				var alpha = emissive
					? Math.Clamp(MathF.Max(fill * 0.35f, rim), 0.0f, 1.0f)
					: fill;
				var intensity = emissive
					? Math.Clamp(MathF.Max(fill * 0.6f, rim), 0.0f, 1.0f)
					: 1.0f;

				var offset = ((y * PreviewTextureSize) + x) * 4;
				var encoded = (byte)Math.Clamp((int)MathF.Round(intensity * 255.0f), 0, 255);
				data[offset] = encoded;
				data[offset + 1] = encoded;
				data[offset + 2] = encoded;
				data[offset + 3] = (byte)Math.Clamp((int)MathF.Round(alpha * 255.0f), 0, 255);
			}
		}

		return data;
	}

	private readonly record struct PreviewTextureSet(Texture Albedo, Texture Emissive);
}
