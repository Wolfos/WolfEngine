#nullable enable

using System;
using System.Collections.Generic;
using WolfEngine.Rendering;

namespace WolfEngine.Rendering.Passes;

public readonly record struct GpuDrawExecutionKey(GpuDrawKind DrawKind, GpuDrawBucketId BucketId);

public readonly struct GpuDrawExecutionLaneDefinition
{
	public GpuDrawExecutionLaneDefinition(
		GpuDrawKind drawKind,
		GpuDrawBucketId bucketId,
		int executionIndex,
		string debugName,
		string shaderVariant,
		string preprocessorDefine,
		DrawPassParticipation participation)
	{
		DrawKind = drawKind;
		BucketId = bucketId;
		ExecutionIndex = executionIndex;
		DebugName = debugName;
		ShaderVariant = shaderVariant;
		PreprocessorDefine = preprocessorDefine;
		Participation = participation;
	}

	public GpuDrawKind DrawKind { get; }
	public GpuDrawBucketId BucketId { get; }
	public int ExecutionIndex { get; }
	public string DebugName { get; }
	public string ShaderVariant { get; }
	public string PreprocessorDefine { get; }
	public DrawPassParticipation Participation { get; }
	public GpuDrawExecutionKey Key => new(DrawKind, BucketId);

	public bool SupportsPass(DrawPassParticipation pass) => (Participation & pass) != 0;
}

public sealed class GpuDrawExecutionLaneRegistry
{
	private readonly GpuDrawExecutionLaneDefinition[] _definitions;
	private readonly Dictionary<GpuDrawExecutionKey, GpuDrawExecutionLaneDefinition> _definitionsByKey = new();
	private readonly Dictionary<DrawPassParticipation, GpuDrawExecutionLaneDefinition[]> _definitionsByPass = new();

	public GpuDrawExecutionLaneRegistry(params GpuDrawExecutionLaneDefinition[] definitions)
	{
		_definitions = definitions ?? Array.Empty<GpuDrawExecutionLaneDefinition>();
		if (_definitions.Length == 0)
		{
			throw new InvalidOperationException("At least one shared draw execution lane must be configured.");
		}

		var executionIndexCoverage = new bool[_definitions.Length];
		for (var i = 0; i < _definitions.Length; i++)
		{
			var definition = _definitions[i];
			if (_definitionsByKey.TryAdd(definition.Key, definition) == false)
			{
				throw new InvalidOperationException(
					$"Duplicate execution lane for draw kind '{definition.DrawKind}' and bucket '{definition.BucketId}'.");
			}

			if (definition.ExecutionIndex < 0 || definition.ExecutionIndex >= _definitions.Length)
			{
				throw new InvalidOperationException(
					$"Execution lane '{definition.DebugName}' uses invalid execution index {definition.ExecutionIndex}.");
			}

			if (executionIndexCoverage[definition.ExecutionIndex])
			{
				throw new InvalidOperationException(
					$"Shared draw execution index {definition.ExecutionIndex} is configured more than once.");
			}

			executionIndexCoverage[definition.ExecutionIndex] = true;
		}

		for (var i = 0; i < executionIndexCoverage.Length; i++)
		{
			if (executionIndexCoverage[i] == false)
			{
				throw new InvalidOperationException($"Shared draw execution index {i} is not configured.");
			}
		}

		_definitionsByPass[DrawPassParticipation.GBuffer] = FilterDefinitions(DrawPassParticipation.GBuffer);
		_definitionsByPass[DrawPassParticipation.ForwardTransparent] = FilterDefinitions(DrawPassParticipation.ForwardTransparent);
		_definitionsByPass[DrawPassParticipation.ShadowCaster] = FilterDefinitions(DrawPassParticipation.ShadowCaster);
	}

	public ReadOnlySpan<GpuDrawExecutionLaneDefinition> Definitions => _definitions;
	public int ExecutionLaneCount => _definitions.Length;

	public GpuDrawExecutionLaneDefinition GetDefinition(GpuDrawExecutionKey key)
	{
		if (_definitionsByKey.TryGetValue(key, out var definition))
		{
			return definition;
		}

		throw new KeyNotFoundException(
			$"Unknown shared draw execution lane for draw kind '{key.DrawKind}' and bucket '{key.BucketId}'.");
	}

	public bool TryGetDefinition(GpuDrawExecutionKey key, out GpuDrawExecutionLaneDefinition definition) =>
		_definitionsByKey.TryGetValue(key, out definition);

	public ReadOnlySpan<GpuDrawExecutionLaneDefinition> GetDefinitionsForPass(DrawPassParticipation pass)
	{
		if (_definitionsByPass.TryGetValue(pass, out var definitions))
		{
			return definitions;
		}

		return FilterDefinitions(pass);
	}

	private GpuDrawExecutionLaneDefinition[] FilterDefinitions(DrawPassParticipation pass)
	{
		var filtered = new List<GpuDrawExecutionLaneDefinition>(_definitions.Length);
		for (var i = 0; i < _definitions.Length; i++)
		{
			var definition = _definitions[i];
			if (definition.SupportsPass(pass))
			{
				filtered.Add(definition);
			}
		}

		filtered.Sort(static (left, right) => left.ExecutionIndex.CompareTo(right.ExecutionIndex));
		return filtered.ToArray();
	}
}

public static class GpuDrawExecutionLanes
{
	private static readonly GpuDrawExecutionLaneRegistry _registry = new(
		new GpuDrawExecutionLaneDefinition(
			GpuDrawKind.Mesh,
			GpuDrawBucketId.Opaque,
			executionIndex: 0,
			"GBuffer.ExecuteMeshOpaque",
			"MeshOpaque",
			string.Empty,
			DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster),
		new GpuDrawExecutionLaneDefinition(
			GpuDrawKind.Mesh,
			GpuDrawBucketId.AlphaBlend,
			executionIndex: 1,
			"ForwardTransparent.ExecuteMeshAlphaBlend",
			"MeshAlphaBlend",
			string.Empty,
			DrawPassParticipation.ForwardTransparent),
		new GpuDrawExecutionLaneDefinition(
			GpuDrawKind.Mesh,
			GpuDrawBucketId.AlphaTest,
			executionIndex: 2,
			"GBuffer.ExecuteMeshAlphaTest",
			"MeshAlphaTest",
			"WOLF_ALPHA_CLIP",
			DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster),
		new GpuDrawExecutionLaneDefinition(
			GpuDrawKind.DebugPrimitive,
			GpuDrawBucketId.Opaque,
			executionIndex: 3,
			"GBuffer.ExecuteDebugPrimitiveOpaque",
			"DebugPrimitiveOpaque",
			string.Empty,
			DrawPassParticipation.GBuffer),
		new GpuDrawExecutionLaneDefinition(
			GpuDrawKind.DebugPrimitive,
			GpuDrawBucketId.AlphaBlend,
			executionIndex: 4,
			"ForwardTransparent.ExecuteDebugPrimitiveAlphaBlend",
			"DebugPrimitiveAlphaBlend",
			string.Empty,
			DrawPassParticipation.ForwardTransparent),
		new GpuDrawExecutionLaneDefinition(
			GpuDrawKind.Terrain,
			GpuDrawBucketId.Opaque,
			executionIndex: 5,
			"GBuffer.ExecuteTerrainOpaque",
			"TerrainOpaque",
			string.Empty,
			DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster));

	public static GpuDrawExecutionLaneRegistry Registry => _registry;
	public static ReadOnlySpan<GpuDrawExecutionLaneDefinition> Definitions => _registry.Definitions;
	public static int ExecutionLaneCount => _registry.ExecutionLaneCount;

	public static GpuDrawExecutionLaneDefinition GetDefinition(GpuDrawKind drawKind, GpuDrawBucketId bucketId) =>
		_registry.GetDefinition(new GpuDrawExecutionKey(drawKind, bucketId));

	public static bool TryGetDefinition(GpuDrawKind drawKind, GpuDrawBucketId bucketId,
		out GpuDrawExecutionLaneDefinition definition) =>
		_registry.TryGetDefinition(new GpuDrawExecutionKey(drawKind, bucketId), out definition);

	public static ReadOnlySpan<GpuDrawExecutionLaneDefinition> GetDefinitionsForPass(DrawPassParticipation pass) =>
		_registry.GetDefinitionsForPass(pass);
}
