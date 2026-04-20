#nullable enable

using System;
using System.Collections.Generic;
using WolfEngine;

namespace WolfEngine.Rendering.Passes;

[Flags]
public enum DrawPassParticipation
{
	None = 0,
	GBuffer = 1 << 0,
	ForwardTransparent = 1 << 1,
	ShadowCaster = 1 << 2
}

public enum GpuDrawBucketId
{
	Opaque = 0,
	AlphaBlend = 1,
	AlphaTest = 2
}

public readonly struct GBufferDrawBucketDefinition
{
	public GBufferDrawBucketDefinition(
		GpuDrawBucketId bucketId,
		int executionIndex,
		string debugName,
		string shaderVariant,
		string preprocessorDefine,
		DrawPassParticipation participation,
		params AlphaMode[] supportedAlphaModes)
	{
		BucketId = bucketId;
		ExecutionIndex = executionIndex;
		DebugName = debugName;
		ShaderVariant = shaderVariant;
		PreprocessorDefine = preprocessorDefine;
		Participation = participation;
		SupportedAlphaModes = supportedAlphaModes ?? Array.Empty<AlphaMode>();
	}

	public GpuDrawBucketId BucketId { get; }
	public int ExecutionIndex { get; }
	public string DebugName { get; }
	public string ShaderVariant { get; }
	public string PreprocessorDefine { get; }
	public DrawPassParticipation Participation { get; }
	public AlphaMode[] SupportedAlphaModes { get; }

	public bool SupportsPass(DrawPassParticipation pass) => (Participation & pass) != 0;
}

public sealed class GBufferDrawBucketRegistry
{
	private readonly GBufferDrawBucketDefinition[] _definitions;
	private readonly GBufferDrawBucketDefinition[] _stableOrderDefinitions;
	private readonly GBufferDrawBucketDefinition[] _gbufferDefinitions;
	private readonly GBufferDrawBucketDefinition[] _transparentDefinitions;
	private readonly GBufferDrawBucketDefinition[] _shadowDefinitions;
	private readonly Dictionary<GpuDrawBucketId, GBufferDrawBucketDefinition> _definitionsById = new();
	private readonly Dictionary<AlphaMode, GpuDrawBucketId> _bucketIdsByAlphaMode = new();

	public GBufferDrawBucketRegistry(params GBufferDrawBucketDefinition[] definitions)
	{
		_definitions = definitions ?? Array.Empty<GBufferDrawBucketDefinition>();
		if (_definitions.Length == 0)
		{
			throw new InvalidOperationException("At least one GBuffer draw bucket must be configured.");
		}

		var executionIndexCoverage = new bool[_definitions.Length];
		for (var i = 0; i < _definitions.Length; i++)
		{
			var definition = _definitions[i];
			if (_definitionsById.TryAdd(definition.BucketId, definition) == false)
			{
				throw new InvalidOperationException($"Duplicate draw bucket id '{definition.BucketId}' is not allowed.");
			}

			if (definition.ExecutionIndex < 0 || definition.ExecutionIndex >= _definitions.Length)
			{
				throw new InvalidOperationException(
					$"Draw bucket '{definition.BucketId}' uses invalid execution index {definition.ExecutionIndex}.");
			}

			if (executionIndexCoverage[definition.ExecutionIndex])
			{
				throw new InvalidOperationException(
					$"Draw bucket execution index {definition.ExecutionIndex} is configured more than once.");
			}

			executionIndexCoverage[definition.ExecutionIndex] = true;

			for (var modeIndex = 0; modeIndex < definition.SupportedAlphaModes.Length; modeIndex++)
			{
				var alphaMode = definition.SupportedAlphaModes[modeIndex];
				if (_bucketIdsByAlphaMode.TryAdd(alphaMode, definition.BucketId) == false)
				{
					throw new InvalidOperationException($"Alpha mode '{alphaMode}' is mapped to more than one draw bucket.");
				}
			}
		}

		for (var i = 0; i < executionIndexCoverage.Length; i++)
		{
			if (executionIndexCoverage[i] == false)
			{
				throw new InvalidOperationException($"Draw bucket execution index {i} is not configured.");
			}
		}

		_stableOrderDefinitions = OrderDefinitions(_definitions, static (left, right) => left.BucketId.CompareTo(right.BucketId));
		_gbufferDefinitions = FilterDefinitions(DrawPassParticipation.GBuffer);
		_transparentDefinitions = FilterDefinitions(DrawPassParticipation.ForwardTransparent);
		_shadowDefinitions = FilterDefinitions(DrawPassParticipation.ShadowCaster);
	}

	public ReadOnlySpan<GBufferDrawBucketDefinition> Definitions => _definitions;
	public ReadOnlySpan<GBufferDrawBucketDefinition> StableOrderDefinitions => _stableOrderDefinitions;
	public int BucketCount => _definitions.Length;

	public GBufferDrawBucketDefinition GetDefinition(GpuDrawBucketId bucketId)
	{
		if (_definitionsById.TryGetValue(bucketId, out var definition))
		{
			return definition;
		}

		throw new KeyNotFoundException($"Unknown draw bucket id '{bucketId}'.");
	}

	public bool TryGetDefinition(GpuDrawBucketId bucketId, out GBufferDrawBucketDefinition definition) =>
		_definitionsById.TryGetValue(bucketId, out definition);

	public int GetExecutionIndex(GpuDrawBucketId bucketId) => GetDefinition(bucketId).ExecutionIndex;

	public GpuDrawBucketId ResolveBucketId(AlphaMode alphaMode, GpuDrawBucketId fallbackBucketId = GpuDrawBucketId.Opaque)
	{
		if (_bucketIdsByAlphaMode.TryGetValue(alphaMode, out var bucketId))
		{
			return bucketId;
		}

		return fallbackBucketId;
	}

	public ReadOnlySpan<GBufferDrawBucketDefinition> GetDefinitionsForPass(DrawPassParticipation pass) => pass switch
	{
		DrawPassParticipation.GBuffer => _gbufferDefinitions,
		DrawPassParticipation.ForwardTransparent => _transparentDefinitions,
		DrawPassParticipation.ShadowCaster => _shadowDefinitions,
		_ => FilterDefinitions(pass)
	};

	private GBufferDrawBucketDefinition[] FilterDefinitions(DrawPassParticipation pass)
	{
		var filtered = new List<GBufferDrawBucketDefinition>(_definitions.Length);
		for (var i = 0; i < _definitions.Length; i++)
		{
			var definition = _definitions[i];
			if (definition.SupportsPass(pass))
			{
				filtered.Add(definition);
			}
		}

		return OrderDefinitions(filtered.ToArray(), static (left, right) => left.ExecutionIndex.CompareTo(right.ExecutionIndex));
	}

	private static GBufferDrawBucketDefinition[] OrderDefinitions(
		GBufferDrawBucketDefinition[] definitions,
		Comparison<GBufferDrawBucketDefinition> comparison)
	{
		var ordered = (GBufferDrawBucketDefinition[])definitions.Clone();
		Array.Sort(ordered, comparison);
		return ordered;
	}
}

public static class GBufferDrawBuckets
{
	private static readonly GBufferDrawBucketRegistry _registry = new(
		new GBufferDrawBucketDefinition(
			GpuDrawBucketId.Opaque,
			executionIndex: 0,
			"GBuffer.ExecuteOpaque",
			"Opaque",
			string.Empty,
			DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster,
			AlphaMode.Opaque),
		new GBufferDrawBucketDefinition(
			GpuDrawBucketId.AlphaBlend,
			executionIndex: 1,
			"ForwardTransparent.ExecuteAlphaBlend",
			"AlphaBlend",
			string.Empty,
			DrawPassParticipation.ForwardTransparent,
			AlphaMode.AlphaBlend),
		new GBufferDrawBucketDefinition(
			GpuDrawBucketId.AlphaTest,
			executionIndex: 2,
			"GBuffer.ExecuteAlphaTest",
			"AlphaTest",
			"WOLF_ALPHA_CLIP",
			DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster,
			AlphaMode.AlphaTest));

	public static GBufferDrawBucketRegistry Registry => _registry;
	public static ReadOnlySpan<GBufferDrawBucketDefinition> Definitions => _registry.Definitions;
	public static ReadOnlySpan<GBufferDrawBucketDefinition> StableOrderDefinitions => _registry.StableOrderDefinitions;
	public static int BucketCount => _registry.BucketCount;

	public static GBufferDrawBucketDefinition GetDefinition(GpuDrawBucketId bucketId) => _registry.GetDefinition(bucketId);
	public static bool TryGetDefinition(GpuDrawBucketId bucketId, out GBufferDrawBucketDefinition definition) =>
		_registry.TryGetDefinition(bucketId, out definition);
	public static int GetExecutionIndex(GpuDrawBucketId bucketId) => _registry.GetExecutionIndex(bucketId);
	public static GpuDrawBucketId ResolveBucketId(AlphaMode alphaMode, GpuDrawBucketId fallbackBucketId = GpuDrawBucketId.Opaque) =>
		_registry.ResolveBucketId(alphaMode, fallbackBucketId);
	public static ReadOnlySpan<GBufferDrawBucketDefinition> GetDefinitionsForPass(DrawPassParticipation pass) =>
		_registry.GetDefinitionsForPass(pass);
}
