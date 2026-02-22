#nullable enable

using System;
using WolfEngine;

namespace WolfEngine.Rendering.Passes;

public readonly struct GBufferDrawBucketDefinition
{
	public GBufferDrawBucketDefinition(
		string debugName,
		string shaderVariant,
		string preprocessorDefine,
		params AlphaMode[] supportedAlphaModes)
	{
		DebugName = debugName;
		ShaderVariant = shaderVariant;
		PreprocessorDefine = preprocessorDefine;
		SupportedAlphaModes = supportedAlphaModes ?? Array.Empty<AlphaMode>();
	}

	public string DebugName { get; }
	public string ShaderVariant { get; }
	public string PreprocessorDefine { get; }
	public AlphaMode[] SupportedAlphaModes { get; }
}

public static class GBufferDrawBuckets
{
	private static readonly GBufferDrawBucketDefinition[] _definitions =
	{
		new GBufferDrawBucketDefinition(
			"GBuffer.ExecuteOpaque",
			"Opaque",
			string.Empty,
			AlphaMode.Opaque,
			AlphaMode.AlphaBlend),
		new GBufferDrawBucketDefinition(
			"GBuffer.ExecuteAlphaTest",
			"AlphaTest",
			"WOLF_ALPHA_CLIP",
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
