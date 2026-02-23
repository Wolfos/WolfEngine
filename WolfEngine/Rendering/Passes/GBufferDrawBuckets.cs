#nullable enable

using System;
using WolfEngine;

namespace WolfEngine.Rendering.Passes;

[Flags]
public enum DrawPassParticipation
{
	None = 0,
	GBuffer = 1 << 0,
	ForwardTransparent = 1 << 1
}

public readonly struct GBufferDrawBucketDefinition
{
	public GBufferDrawBucketDefinition(
		string debugName,
		string shaderVariant,
		string preprocessorDefine,
		DrawPassParticipation participation,
		params AlphaMode[] supportedAlphaModes)
	{
		DebugName = debugName;
		ShaderVariant = shaderVariant;
		PreprocessorDefine = preprocessorDefine;
		Participation = participation;
		SupportedAlphaModes = supportedAlphaModes ?? Array.Empty<AlphaMode>();
	}

	public string DebugName { get; }
	public string ShaderVariant { get; }
	public string PreprocessorDefine { get; }
	public DrawPassParticipation Participation { get; }
	public AlphaMode[] SupportedAlphaModes { get; }

	public bool SupportsPass(DrawPassParticipation pass) => (Participation & pass) != 0;
}

public static class GBufferDrawBuckets
{
	private static readonly GBufferDrawBucketDefinition[] _definitions =
	{
		new GBufferDrawBucketDefinition(
			"GBuffer.ExecuteOpaque",
			"Opaque",
			string.Empty,
			DrawPassParticipation.GBuffer,
			AlphaMode.Opaque),
		new GBufferDrawBucketDefinition(
			"ForwardTransparent.ExecuteAlphaBlend",
			"AlphaBlend",
			string.Empty,
			DrawPassParticipation.ForwardTransparent,
			AlphaMode.AlphaBlend),
		new GBufferDrawBucketDefinition(
			"GBuffer.ExecuteAlphaTest",
			"AlphaTest",
			"WOLF_ALPHA_CLIP",
			DrawPassParticipation.GBuffer,
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
