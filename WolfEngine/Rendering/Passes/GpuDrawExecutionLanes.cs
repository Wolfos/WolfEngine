using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Which faces of a lane's geometry survive rasterisation. Each pass owns the cull mode it uses for
/// single-sided draws — the shadow pass biases differently from the GBuffer pass — so lanes carry the
/// material's intent rather than a cull mode, and <see cref="GpuDrawExecutionLaneDefinition.ResolveCullMode"/>
/// maps it onto the pass's own choice.
/// </summary>
public enum GpuDrawSidedness
{
	SingleSided = 0,
	DoubleSided = 1
}

public readonly record struct GpuDrawExecutionKey(
	GpuDrawKind DrawKind,
	GpuDrawBucketId BucketId,
	GpuDrawSidedness Sidedness,
	bool IsSkinned = false,
	bool ReverseWinding = false);

public readonly struct GpuDrawExecutionLaneDefinition
{
	public GpuDrawExecutionLaneDefinition(
		GpuDrawKind drawKind,
		GpuDrawBucketId bucketId,
		GpuDrawSidedness sidedness,
		int executionIndex,
		string debugName,
		string shaderVariant,
		string preprocessorDefine,
		DrawPassParticipation participation,
		bool isSkinned = false,
		bool reverseWinding = false)
	{
		DrawKind = drawKind;
		BucketId = bucketId;
		Sidedness = sidedness;
		ExecutionIndex = executionIndex;
		DebugName = debugName;
		ShaderVariant = shaderVariant;
		PreprocessorDefine = preprocessorDefine;
		Participation = participation;
		IsSkinned = isSkinned;
		ReverseWinding = reverseWinding;
	}

	public GpuDrawKind DrawKind { get; }
	public GpuDrawBucketId BucketId { get; }
	public GpuDrawSidedness Sidedness { get; }
	public int ExecutionIndex { get; }
	public string DebugName { get; }
	public string ShaderVariant { get; }
	public string PreprocessorDefine { get; }
	public DrawPassParticipation Participation { get; }
	public bool IsSkinned { get; }
	public bool ReverseWinding { get; }
	public GpuDrawExecutionKey Key => new(DrawKind, BucketId, Sidedness, IsSkinned, ReverseWinding);

	public bool SupportsPass(DrawPassParticipation pass) => (Participation & pass) != 0;

	public CullMode ResolveCullMode(CullMode singleSidedCullMode) =>
		Sidedness == GpuDrawSidedness.DoubleSided ? CullMode.None : singleSidedCullMode;
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
					$"Duplicate execution lane for draw kind '{definition.DrawKind}', bucket '{definition.BucketId}' " +
					$"and sidedness '{definition.Sidedness}'.");
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
			$"Unknown shared draw execution lane for draw kind '{key.DrawKind}', bucket '{key.BucketId}' " +
			$"and sidedness '{key.Sidedness}'.");
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
	private static readonly GpuDrawExecutionLaneRegistry _registry = CreateRegistry();

	private static GpuDrawExecutionLaneRegistry CreateRegistry()
	{
		GpuDrawExecutionLaneDefinition[] normal =
		[
			new GpuDrawExecutionLaneDefinition(
				GpuDrawKind.Mesh,
				GpuDrawBucketId.Opaque,
				GpuDrawSidedness.SingleSided,
				executionIndex: 0,
				"GBuffer.ExecuteMeshOpaque",
				"MeshOpaque",
				string.Empty,
				DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster),
			new GpuDrawExecutionLaneDefinition(
				GpuDrawKind.Mesh,
				GpuDrawBucketId.AlphaBlend,
				GpuDrawSidedness.SingleSided,
				executionIndex: 1,
				"ForwardTransparent.ExecuteMeshAlphaBlend",
				"MeshAlphaBlend",
				string.Empty,
				DrawPassParticipation.ForwardTransparent),
			new GpuDrawExecutionLaneDefinition(
				GpuDrawKind.Mesh,
				GpuDrawBucketId.AlphaTest,
				GpuDrawSidedness.SingleSided,
				executionIndex: 2,
				"GBuffer.ExecuteMeshAlphaTest",
				"MeshAlphaTest",
				"WOLF_ALPHA_CLIP",
				DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster),
			new GpuDrawExecutionLaneDefinition(
				GpuDrawKind.DebugPrimitive,
				GpuDrawBucketId.Opaque,
				GpuDrawSidedness.SingleSided,
				executionIndex: 3,
				"GBuffer.ExecuteDebugPrimitiveOpaque",
				"DebugPrimitiveOpaque",
				string.Empty,
				DrawPassParticipation.GBuffer),
			new GpuDrawExecutionLaneDefinition(
				GpuDrawKind.DebugPrimitive,
				GpuDrawBucketId.AlphaBlend,
				GpuDrawSidedness.SingleSided,
				executionIndex: 4,
				"ForwardTransparent.ExecuteDebugPrimitiveAlphaBlend",
				"DebugPrimitiveAlphaBlend",
				string.Empty,
				DrawPassParticipation.ForwardTransparent),
			new GpuDrawExecutionLaneDefinition(
				GpuDrawKind.Terrain,
				GpuDrawBucketId.Opaque,
				GpuDrawSidedness.SingleSided,
				executionIndex: 5,
				"GBuffer.ExecuteTerrainOpaque",
				"TerrainOpaque",
				string.Empty,
				DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster),
			new GpuDrawExecutionLaneDefinition(
				GpuDrawKind.Mesh,
				GpuDrawBucketId.Opaque,
				GpuDrawSidedness.DoubleSided,
				executionIndex: 6,
				"GBuffer.ExecuteMeshOpaqueDoubleSided",
				"MeshOpaqueDoubleSided",
				string.Empty,
				DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster),
			new GpuDrawExecutionLaneDefinition(
				GpuDrawKind.Mesh,
				GpuDrawBucketId.AlphaBlend,
				GpuDrawSidedness.DoubleSided,
				executionIndex: 7,
				"ForwardTransparent.ExecuteMeshAlphaBlendDoubleSided",
				"MeshAlphaBlendDoubleSided",
				string.Empty,
				DrawPassParticipation.ForwardTransparent),
			new GpuDrawExecutionLaneDefinition(
				GpuDrawKind.Mesh,
				GpuDrawBucketId.AlphaTest,
				GpuDrawSidedness.DoubleSided,
				executionIndex: 8,
				"GBuffer.ExecuteMeshAlphaTestDoubleSided",
				"MeshAlphaTestDoubleSided",
				"WOLF_ALPHA_CLIP",
				DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster),
			new GpuDrawExecutionLaneDefinition(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided,
	            9, "GBuffer.ExecuteSkinnedMeshOpaqueSingleSided", "SkinnedMeshOpaqueSingleSided", "",
	            DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster, isSkinned: true),
			new GpuDrawExecutionLaneDefinition(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.SingleSided,
	            10, "GBuffer.ExecuteSkinnedMeshAlphaTestSingleSided", "SkinnedMeshAlphaTestSingleSided", "WOLF_ALPHA_CLIP",
	            DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster, isSkinned: true),
			new GpuDrawExecutionLaneDefinition(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.DoubleSided,
	            11, "GBuffer.ExecuteSkinnedMeshOpaqueDoubleSided", "SkinnedMeshOpaqueDoubleSided", "",
	            DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster, isSkinned: true),
			new GpuDrawExecutionLaneDefinition(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.DoubleSided,
	            12, "GBuffer.ExecuteSkinnedMeshAlphaTestDoubleSided", "SkinnedMeshAlphaTestDoubleSided", "WOLF_ALPHA_CLIP",
	            DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster, isSkinned: true)
		];
		// Preserve existing indices and append a mirrored counterpart for every lane.
		var mirrored = normal.Select(lane => new GpuDrawExecutionLaneDefinition(
			lane.DrawKind, lane.BucketId, lane.Sidedness, lane.ExecutionIndex + normal.Length,
			lane.DebugName + "Mirrored", lane.ShaderVariant + "Mirrored", lane.PreprocessorDefine,
			lane.Participation, lane.IsSkinned, reverseWinding: true));
		return new GpuDrawExecutionLaneRegistry(normal.Concat(mirrored).ToArray());
	}

	public static GpuDrawExecutionLaneRegistry Registry => _registry;
	public static ReadOnlySpan<GpuDrawExecutionLaneDefinition> Definitions => _registry.Definitions;
	public static int ExecutionLaneCount => _registry.ExecutionLaneCount;

	public static GpuDrawExecutionLaneDefinition GetDefinition(
		GpuDrawKind drawKind,
		GpuDrawBucketId bucketId,
		GpuDrawSidedness sidedness) =>
		_registry.GetDefinition(new GpuDrawExecutionKey(drawKind, bucketId, sidedness));

	public static bool TryGetDefinition(GpuDrawExecutionKey key, out GpuDrawExecutionLaneDefinition definition) =>
		_registry.TryGetDefinition(key, out definition);

	public static bool TryGetDefinition(GpuDrawKind drawKind, GpuDrawBucketId bucketId, GpuDrawSidedness sidedness,
		out GpuDrawExecutionLaneDefinition definition) =>
		_registry.TryGetDefinition(new GpuDrawExecutionKey(drawKind, bucketId, sidedness), out definition);

	public static ReadOnlySpan<GpuDrawExecutionLaneDefinition> GetDefinitionsForPass(DrawPassParticipation pass) =>
		_registry.GetDefinitionsForPass(pass);
}
