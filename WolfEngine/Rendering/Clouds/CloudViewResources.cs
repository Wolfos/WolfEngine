using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Clouds;

/// <summary>
/// A view's cloud resources for one frame. Default, with <see cref="Enabled"/> false, when the view draws no clouds:
/// they are off, or the sky is not Procedural.
/// </summary>
internal readonly struct CloudViewResources
{
	/// <summary>Resolution of the cloud shadow map, in texels per side.</summary>
	public const int ShadowMapSize = 512;

	/// <summary>World size the cloud shadow map covers around the camera, in metres per side.</summary>
	public const float ShadowMapExtentMetres = 16000.0f;

	/// <summary>How much of the history a still camera keeps each frame.</summary>
	public const float HistoryWeight = 0.92f;

	public bool Enabled { get; init; }
	public CloudFrame Frame { get; init; }

	public Int2 TraceSize { get; init; }
	public int TraceDivisor { get; init; }

	public RenderGraphResourceHandle Weather { get; init; }
	public RenderGraphResourceHandle Shape { get; init; }
	public RenderGraphResourceHandle Detail { get; init; }

	/// <summary>This frame's march at trace resolution, before temporal accumulation.</summary>
	public RenderGraphResourceHandle CurrentColor { get; init; }
	public RenderGraphResourceHandle CurrentDepth { get; init; }

	public RenderGraphResourceHandle HistoryColorRead { get; init; }
	public RenderGraphResourceHandle HistoryDepthRead { get; init; }

	/// <summary>The accumulated clouds, which deferred lighting upsamples and composites.</summary>
	public RenderGraphResourceHandle HistoryColorWrite { get; init; }
	public RenderGraphResourceHandle HistoryDepthWrite { get; init; }
	public bool HistoryValid { get; init; }

	/// <summary>Sun transmittance through the layer; invalid when the clouds cast no shadows.</summary>
	public RenderGraphResourceHandle ShadowMap { get; init; }

	/// <summary>Centre of the shadow map relative to the camera, in world XZ metres.</summary>
	public Vector2 ShadowMapCentre { get; init; }

	/// <summary>The clouds seen in every direction, when this frame redraws the sky environment.</summary>
	public RenderGraphResourceHandle Environment { get; init; }

	public bool CastsShadows => ShadowMap.IsValid;
	public bool RecordsEnvironment => Environment.IsValid;

	/// <summary>The trace resolution for a render size: half or a quarter of it, rounded up.</summary>
	public static Int2 GetTraceSize(Int2 renderSize, int divisor)
	{
		var safeDivisor = Math.Max(divisor, 1);

		return new Int2(
			Math.Max(1, (renderSize.X + safeDivisor - 1) / safeDivisor),
			Math.Max(1, (renderSize.Y + safeDivisor - 1) / safeDivisor));
	}

	/// <summary>
	/// The shadow map's centre relative to the camera: the camera's position snapped to whole texels, so the map
	/// does not crawl as the camera moves.
	/// </summary>
	public static Vector2 GetShadowMapCentre(Vector3 cameraOrigin)
	{
		var texel = (double)ShadowMapExtentMetres / ShadowMapSize;
		var snappedX = Math.Round(cameraOrigin.X / texel) * texel;
		var snappedZ = Math.Round(cameraOrigin.Z / texel) * texel;

		return new Vector2((float)(snappedX - cameraOrigin.X), (float)(snappedZ - cameraOrigin.Z));
	}

	public static TextureDescriptor DescribeTraceTexture(Int2 size, TextureFormat format)
	{
		return new TextureDescriptor(
			size.X,
			size.Y,
			format,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f));
	}
}
