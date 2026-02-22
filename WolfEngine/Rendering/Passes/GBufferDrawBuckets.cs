#nullable enable

using System;
using WolfEngine;

namespace WolfEngine.Rendering.Passes;

public readonly struct GBufferDrawBucketDefinition
{
	public GBufferDrawBucketDefinition(
		string debugName,
		string pixelEntryPoint,
		params AlphaMode[] supportedAlphaModes)
	{
		DebugName = debugName;
		PixelEntryPoint = pixelEntryPoint;
		SupportedAlphaModes = supportedAlphaModes ?? Array.Empty<AlphaMode>();
	}

	public string DebugName { get; }
	public string PixelEntryPoint { get; }
	public AlphaMode[] SupportedAlphaModes { get; }
}

public static class GBufferDrawBuckets
{
	private static readonly GBufferDrawBucketDefinition[] _definitions =
	{
		new GBufferDrawBucketDefinition(
			"GBuffer.ExecuteOpaque",
			"fragmentShader",
			AlphaMode.Opaque,
			AlphaMode.AlphaBlend),
		new GBufferDrawBucketDefinition(
			"GBuffer.ExecuteAlphaTest",
			"fragmentShaderAlphaTest",
			AlphaMode.AlphaTest)
	};

	static GBufferDrawBuckets()
	{
		if (_definitions.Length == 0)
		{
			throw new InvalidOperationException("At least one GBuffer draw bucket must be configured.");
		}
	}

	public static ReadOnlySpan<GBufferDrawBucketDefinition> Definitions => _definitions;
	public static int BucketCount => _definitions.Length;
}
