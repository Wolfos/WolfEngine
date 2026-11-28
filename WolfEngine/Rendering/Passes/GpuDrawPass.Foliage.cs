using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WolfEngine.ECS;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// A foliage type as the foliage cull reads it. Mirrors <c>GpuFoliageType</c> in foliage_cull.compute.slang. Only the
/// level count and thresholds of <see cref="Lod"/> are used.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuFoliageType
{
	public Vector4 BoundsCenterRadius;
	public Vector4 Distances;
	public uint RendererStart;
	public uint RendererCount;
	private uint _pad0;
	private uint _pad1;
	public GpuDrawBatchLodData Lod;
}

/// <summary>
/// One renderer of a foliage type as the foliage cull reads it. Mirrors <c>GpuFoliageRenderer</c> in
/// foliage_cull.compute.slang.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuFoliageRenderer
{
	public Matrix4x4 LocalTransform;
	public uint MaterialHandle;
	public uint MeshHandle;
	public uint DrawFlags;

	/// <summary>First group level in bits 0-7, last in bits 8-15, the mesh's level count in bits 16-23.</summary>
	public uint LevelRange;

	/// <summary>The batch drawing each of the mesh's levels; zero for a level with no batch.</summary>
	public GpuDrawBatchLodData.LevelBatchIndices LevelBatches;

	public static uint PackLevelRange(int firstLevel, int lastLevel, int meshLevelCount)
	{
		return (uint)firstLevel | ((uint)lastLevel << 8) | ((uint)meshLevelCount << 16);
	}
}

/// <summary>
/// Foliage: the GPU copies of each terrain's placed instances, the buffers detail foliage is generated into each frame,
/// the per-frame type and renderer tables, the batch slots reserved for them, and the cull that expands visible
/// instances into the instance table's foliage region. See docs/foliage.md.
/// </summary>
public sealed partial class GpuDrawPass
{
	/// <summary>
	/// Most instance slots the foliage region is grown to. Visible foliage past it is dropped and counted in the foliage
	/// counters.
	/// </summary>
	private const int MaxFoliageExpansionSlots = 524288;

	/// <summary>Views a frame culls foliage for: the camera, and the shadow cascades.</summary>
	private const int FoliageExpansionViewGroups = 2;

	private readonly Dictionary<TerrainFoliageRuntime, FoliageRuntimeGpuState> _foliageRuntimeStates = new();
	private readonly Dictionary<FoliageSetKey, FoliageSetGpuState> _foliageSetStates = new();
	private readonly Dictionary<FoliageSetKey, DetailFoliageGpuState> _detailFoliageStates = new();
	private readonly Dictionary<FoliageReservationKey, FoliageReservation> _foliageReservations = new();
	private readonly HashSet<FoliageReservationKey> _desiredFoliageReservations = new();
	private readonly List<FoliageReservationKey> _foliageReservationScratch = new();
	private readonly List<PreparedFoliageSet> _preparedFoliageSets = new();
	private readonly List<GpuFoliageType> _foliageTypeScratch = new();
	private readonly List<GpuFoliageRenderer> _foliageRendererScratch = new();
	private readonly IGfxBuffer?[] _foliageCounterBuffers = new IGfxBuffer?[GpuDrawResources.MaxFramesInFlight];
	private int _foliageReservationClearGeneration = -1;
	private int _foliageFrame;

	/// <param name="Detail">True for a terrain's detail foliage, false for its placed foliage.</param>
	private readonly record struct FoliageSetKey(int OwnerViewIndex, int WorldId, Entity Entity, bool Detail);

	private readonly record struct FoliageReservationKey(
		FoliageSetKey Set,
		IFoliageCullSource Runtime,
		int TypeIndex,
		int RendererIndex,
		int MeshLevel,
		GpuDrawExecutionKey ExecutionKey,
		Mesh Mesh)
	{
		public bool Equals(FoliageReservationKey other) =>
			Set == other.Set &&
			ReferenceEquals(Runtime, other.Runtime) &&
			TypeIndex == other.TypeIndex &&
			RendererIndex == other.RendererIndex &&
			MeshLevel == other.MeshLevel &&
			ExecutionKey == other.ExecutionKey &&
			ReferenceEquals(Mesh, other.Mesh);

		public override int GetHashCode() =>
			HashCode.Combine(
				Set,
				RuntimeHelpers.GetHashCode(Runtime),
				TypeIndex,
				RendererIndex,
				MeshLevel,
				ExecutionKey,
				RuntimeHelpers.GetHashCode(Mesh));
	}

	private readonly record struct FoliageReservation(uint BatchIndex, uint SlotCount);

	private sealed record FoliageCullBindings(
		uint InstanceTable,
		uint DrawArgs,
		uint VisibleInstances,
		uint Levels,
		uint Counters,
		uint Batches,
		uint Instances,
		uint Chunks,
		uint Types,
		uint Renderers);

	private sealed record DetailGenerateBindings(
		uint Instances,
		uint Chunks,
		uint TerrainMaterials,
		uint TerrainLayers,
		uint Entries);

	/// <summary>A terrain's instances and chunks on the GPU, shared by every view that draws it.</summary>
	private sealed class FoliageRuntimeGpuState
	{
		public required IGfxBuffer Instances;
		public required IGfxBuffer Chunks;
		public int LastUsedFrame;
	}

	/// <summary>One view's copy of a terrain's foliage: level state for hysteresis, and the tables per frame slot.</summary>
	private sealed class FoliageSetGpuState
	{
		public required IGfxBuffer Levels;
		public required int LevelCapacity;
		public readonly IGfxBuffer?[] Types = new IGfxBuffer?[GpuDrawResources.MaxFramesInFlight];
		public readonly IGfxBuffer?[] Renderers = new IGfxBuffer?[GpuDrawResources.MaxFramesInFlight];
		public readonly int[] TypeCapacities = new int[GpuDrawResources.MaxFramesInFlight];
		public readonly int[] RendererCapacities = new int[GpuDrawResources.MaxFramesInFlight];
		public int LastUsedFrame;
	}

	/// <summary>
	/// A terrain's detail foliage on the GPU for one view: the instances and chunks it is generated into, sized for its
	/// runtime's candidates, and the entry table generation reads.
	/// </summary>
	private sealed class DetailFoliageGpuState
	{
		public required TerrainDetailRuntime Runtime;
		public required IGfxBuffer Instances;
		public required IGfxBuffer Chunks;
		public required IGfxBuffer Entries;
		public int LastUsedFrame;
	}

	/// <summary>What detail generation needs this frame, besides the buffers it writes.</summary>
	/// <param name="TerrainMaterialHandle">Zero while the terrain's material is not in the draw tables yet.</param>
	private sealed record PreparedDetailGeneration(
		GpuDetailFoliageSet Set,
		DetailFoliageGpuState Gpu,
		uint TerrainMaterialHandle);

	/// <param name="Instances">Packed instances the cull reads, four words each.</param>
	/// <param name="Detail">How to generate the instances first; null for placed foliage.</param>
	private sealed record PreparedFoliageSet(
		int OwnerViewIndex,
		IFoliageCullSource Source,
		Matrix4x4 LocalToWorld,
		IGfxBuffer Instances,
		IGfxBuffer Chunks,
		FoliageSetGpuState State,
		PreparedDetailGeneration? Detail);

	/// <summary>
	/// Instance slots the foliage cull could expand into this frame if every placed instance and detail candidate were
	/// visible to both the camera and the shadow cascades, capped at <see cref="MaxFoliageExpansionSlots"/>.
	/// </summary>
	private static int GetFoliageExpansionSlots(ReadOnlySpan<GpuDrawSource> sources)
	{
		long slots = 0;
		for (var i = 0; i < sources.Length; i++)
		{
			foreach (var set in sources[i].Database.FoliageSets)
			{
				slots += (long)set.Runtime.RendererInstanceCount * FoliageExpansionViewGroups;
			}

			foreach (var set in sources[i].Database.DetailFoliageSets)
			{
				slots += (long)set.Runtime.RendererInstanceCount * FoliageExpansionViewGroups;
			}
		}

		return (int)Math.Min(slots, MaxFoliageExpansionSlots);
	}

	/// <summary>
	/// Builds this frame's foliage tables from every source's foliage sets, and reserves batch slots for each renderer's
	/// mesh levels. Runs after the frame's updates, so the materials the anchors keep resident have their draw state.
	/// </summary>
	private void PrepareFoliage(ReadOnlySpan<GpuDrawSource> sources, IGfxDevice device, int frameSlot)
	{
		_foliageFrame++;
		_preparedFoliageSets.Clear();
		_desiredFoliageReservations.Clear();

		// Emptying every batch dropped the reservations with them.
		if (_foliageReservationClearGeneration != _batches.ClearGeneration)
		{
			_foliageReservations.Clear();
			_foliageReservationClearGeneration = _batches.ClearGeneration;
		}

		ResetFoliageCounters(device, frameSlot);

		for (var sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
		{
			var source = sources[sourceIndex];
			foreach (var set in source.Database.FoliageSets)
			{
				if (set.Runtime.InstanceCount == 0)
				{
					continue;
				}

				var setKey = new FoliageSetKey(source.Owner.Index, set.WorldId, set.Entity, Detail: false);
				var runtimeState = EnsureFoliageRuntimeState(set.Runtime, device);
				var setState = EnsureFoliageSetState(setKey, set.Runtime.InstanceCount, device);
				BuildFoliageTables(setKey, set.Runtime, set.LocalToWorld, source.Database);
				UploadFoliageTables(setState, device, frameSlot);
				_preparedFoliageSets.Add(new PreparedFoliageSet(
					source.Owner.Index,
					set.Runtime,
					set.LocalToWorld,
					runtimeState.Instances,
					runtimeState.Chunks,
					setState,
					null));
			}

			foreach (var set in source.Database.DetailFoliageSets)
			{
				var setKey = new FoliageSetKey(source.Owner.Index, set.WorldId, set.Entity, Detail: true);
				var detailState = EnsureDetailFoliageState(setKey, set.Runtime, device);
				var setState = EnsureFoliageSetState(setKey, set.Runtime.CandidateCount, device);
				BuildFoliageTables(setKey, set.Runtime, set.LocalToWorld, source.Database);
				UploadFoliageTables(setState, device, frameSlot);
				var terrainMaterialHandle = source.Database.TryGetMaterialHandle(set.TerrainMaterial, out var handle)
					? handle.Value
					: 0u;
				_preparedFoliageSets.Add(new PreparedFoliageSet(
					source.Owner.Index,
					set.Runtime,
					set.LocalToWorld,
					detailState.Instances,
					detailState.Chunks,
					setState,
					new PreparedDetailGeneration(set, detailState, terrainMaterialHandle)));
			}
		}

		ReleaseUndesiredFoliageReservations();
		RetireUnusedFoliageStates(device);
	}

	private void BuildFoliageTables(
		FoliageSetKey setKey,
		IFoliageCullSource source,
		in Matrix4x4 localToWorld,
		GpuDrawDatabase database)
	{
		_foliageTypeScratch.Clear();
		_foliageRendererScratch.Clear();

		for (var typeIndex = 0; typeIndex < source.Types.Count; typeIndex++)
		{
			var gpuType = new GpuFoliageType
			{
				RendererStart = (uint)_foliageRendererScratch.Count
			};

			var instanceCount = (uint)source.GetTypeSlotCount(typeIndex);
			if (source.Types[typeIndex] is { } type && instanceCount > 0)
			{
				for (var rendererIndex = 0; rendererIndex < type.Renderers.Count; rendererIndex++)
				{
					if (TryBuildFoliageRenderer(
						    setKey,
						    source,
						    localToWorld,
						    database,
						    typeIndex,
						    rendererIndex,
						    type.Renderers[rendererIndex],
						    instanceCount,
						    out var gpuRenderer))
					{
						_foliageRendererScratch.Add(gpuRenderer);
					}
				}

				gpuType.BoundsCenterRadius = new Vector4(type.Bounds.Center, type.Bounds.Radius);
				gpuType.Distances = new Vector4(type.CullDistance, type.ShadowDistance, 0.0f, 0.0f);
				gpuType.Lod.LevelCount = (uint)type.LevelCount;
				for (var i = 0; i < type.ScreenSizes.Length; i++)
				{
					gpuType.Lod.ScreenSizes[i] = type.ScreenSizes[i];
				}
			}

			gpuType.RendererCount = (uint)_foliageRendererScratch.Count - gpuType.RendererStart;
			_foliageTypeScratch.Add(gpuType);
		}
	}

	/// <summary>
	/// Resolves a renderer's material lane and reserves a slot per instance in the batch of each mesh level it can draw.
	/// </summary>
	/// <returns>False while its mesh or material is not in the draw tables yet, or no lane draws its material.</returns>
	private bool TryBuildFoliageRenderer(
		FoliageSetKey setKey,
		IFoliageCullSource source,
		in Matrix4x4 localToWorld,
		GpuDrawDatabase database,
		int typeIndex,
		int rendererIndex,
		in FoliageRendererRuntime renderer,
		uint instanceCount,
		out GpuFoliageRenderer gpuRenderer)
	{
		gpuRenderer = default;
		if (database.TryGetMaterialHandle(renderer.Material, out var materialHandle) == false ||
		    database.TryGetMeshHandle(renderer.Mesh, out var meshHandle) == false ||
		    _materialDrawStates.TryGetValue(materialHandle.Value, out var drawState) == false ||
		    drawState.DrawFlags == 0u)
		{
			return false;
		}

		// Alpha-blended batches are one instance each, to keep their order stable, which reservations cannot give.
		if (drawState.ExecutionKey.BucketId == GpuDrawBucketId.AlphaBlend)
		{
			return false;
		}

		var executionKey = GpuDrawClassification.ResolveExecutionKey(
			drawState.ExecutionKey.DrawKind,
			drawState.ExecutionKey.BucketId,
			drawState.ExecutionKey.Sidedness,
			renderer.Mesh,
			renderer.LocalTransform * localToWorld);
		if (GpuDrawExecutionLanes.TryGetDefinition(executionKey, out var lane) == false)
		{
			return false;
		}

		var rangeLength = renderer.LastLevel - renderer.FirstLevel + 1;
		var meshLevelCount = Math.Min(
			Math.Min(rangeLength, 1 + renderer.Mesh.LodLevels.Count),
			GpuDrawBatchLodData.MaxLevelCount);
		for (var meshLevel = 0; meshLevel < meshLevelCount; meshLevel++)
		{
			var levelMesh = meshLevel == 0 ? renderer.Mesh : renderer.Mesh.LodLevels[meshLevel - 1];
			_batches.EnsureResident(levelMesh);
			var key = new FoliageReservationKey(
				setKey,
				source,
				typeIndex,
				rendererIndex,
				meshLevel,
				executionKey,
				levelMesh);
			_desiredFoliageReservations.Add(key);
			if (_foliageReservations.TryGetValue(key, out var reservation) == false)
			{
				var batchIndex = _batches.Reserve(executionKey, levelMesh, instanceCount);
				reservation = new FoliageReservation(batchIndex, batchIndex == 0 ? 0u : instanceCount);
				_foliageReservations.Add(key, reservation);
			}

			gpuRenderer.LevelBatches[meshLevel] = reservation.BatchIndex;
		}

		gpuRenderer.LocalTransform = renderer.LocalTransform;
		gpuRenderer.MaterialHandle = materialHandle.Value;
		gpuRenderer.MeshHandle = meshHandle.Value;
		gpuRenderer.DrawFlags = CreateDrawFlags(lane.ExecutionIndex);
		gpuRenderer.LevelRange = GpuFoliageRenderer.PackLevelRange(renderer.FirstLevel, renderer.LastLevel, meshLevelCount);

		return true;
	}

	private void ReleaseUndesiredFoliageReservations()
	{
		_foliageReservationScratch.Clear();
		foreach (var key in _foliageReservations.Keys)
		{
			if (_desiredFoliageReservations.Contains(key) == false)
			{
				_foliageReservationScratch.Add(key);
			}
		}

		foreach (var key in _foliageReservationScratch)
		{
			var reservation = _foliageReservations[key];
			if (reservation.SlotCount > 0)
			{
				_batches.Release(reservation.BatchIndex, reservation.SlotCount, _foliageReservationClearGeneration);
			}

			_foliageReservations.Remove(key);
		}
	}

	private FoliageRuntimeGpuState EnsureFoliageRuntimeState(TerrainFoliageRuntime runtime, IGfxDevice device)
	{
		if (_foliageRuntimeStates.TryGetValue(runtime, out var state))
		{
			state.LastUsedFrame = _foliageFrame;
			return state;
		}

		var instances = device.CreateBuffer(new BufferDescriptor(
			(ulong)runtime.PackedInstances.Length * sizeof(uint),
			BufferUsage.Structured,
			BufferFlags.AllowShaderResource,
			name: "FoliageInstances"));
		WriteBuffer<uint>(instances, runtime.PackedInstances, "FoliageInstances");

		var chunks = device.CreateBuffer(new BufferDescriptor(
			(ulong)(Math.Max(runtime.Chunks.Length, 1) * Unsafe.SizeOf<GpuFoliageChunk>()),
			BufferUsage.Structured,
			BufferFlags.AllowShaderResource,
			name: "FoliageChunks"));
		WriteBuffer<GpuFoliageChunk>(chunks, runtime.Chunks, "FoliageChunks");

		state = new FoliageRuntimeGpuState
		{
			Instances = instances,
			Chunks = chunks,
			LastUsedFrame = _foliageFrame
		};
		_foliageRuntimeStates.Add(runtime, state);

		return state;
	}

	private FoliageSetGpuState EnsureFoliageSetState(FoliageSetKey key, int slotCount, IGfxDevice device)
	{
		if (_foliageSetStates.TryGetValue(key, out var state) && state.LevelCapacity >= slotCount)
		{
			state.LastUsedFrame = _foliageFrame;
			return state;
		}

		if (state is not null)
		{
			RetireFoliageSetState(state, device);
		}

		var levelCapacity = Math.Max(slotCount, 1);
		var levels = device.CreateBuffer(new BufferDescriptor(
			(ulong)levelCapacity * sizeof(uint),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			name: "FoliageLevels"));
		WriteBuffer<uint>(levels, new uint[levelCapacity], "FoliageLevels");

		state = new FoliageSetGpuState
		{
			Levels = levels,
			LevelCapacity = levelCapacity,
			LastUsedFrame = _foliageFrame
		};
		_foliageSetStates[key] = state;

		return state;
	}

	/// <summary>
	/// The buffers a terrain's detail foliage is generated into for one view, created again when its runtime changes.
	/// Chunks start empty, so a set the cull reads before its first generation draws nothing.
	/// </summary>
	private DetailFoliageGpuState EnsureDetailFoliageState(FoliageSetKey key, TerrainDetailRuntime runtime, IGfxDevice device)
	{
		if (_detailFoliageStates.TryGetValue(key, out var state))
		{
			if (ReferenceEquals(state.Runtime, runtime))
			{
				state.LastUsedFrame = _foliageFrame;
				return state;
			}

			RetireDetailFoliageState(state, device);
		}

		var instances = device.CreateBuffer(new BufferDescriptor(
			(ulong)Math.Max(runtime.CandidateCount, 1) * FoliageInstancePacking.SizeInBytes,
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			name: "DetailFoliageInstances"));

		var chunkCount = Math.Max(runtime.ChunkCount, 1);
		var chunks = device.CreateBuffer(new BufferDescriptor(
			(ulong)(chunkCount * Unsafe.SizeOf<GpuFoliageChunk>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			name: "DetailFoliageChunks"));
		WriteBuffer<GpuFoliageChunk>(chunks, new GpuFoliageChunk[chunkCount], "DetailFoliageChunks");

		var entries = device.CreateBuffer(new BufferDescriptor(
			(ulong)(Math.Max(runtime.Entries.Length, 1) * Unsafe.SizeOf<GpuDetailEntry>()),
			BufferUsage.Structured,
			BufferFlags.AllowShaderResource,
			name: "DetailFoliageEntries"));
		WriteBuffer<GpuDetailEntry>(entries, runtime.Entries, "DetailFoliageEntries");

		state = new DetailFoliageGpuState
		{
			Runtime = runtime,
			Instances = instances,
			Chunks = chunks,
			Entries = entries,
			LastUsedFrame = _foliageFrame
		};
		_detailFoliageStates[key] = state;

		return state;
	}

	private static void RetireDetailFoliageState(DetailFoliageGpuState state, IGfxDevice device)
	{
		RetireBuffer(device, state.Instances, "DetailFoliageInstances");
		RetireBuffer(device, state.Chunks, "DetailFoliageChunks");
		RetireBuffer(device, state.Entries, "DetailFoliageEntries");
	}

	/// <summary>
	/// Writes the tables into this frame slot's buffers. Each slot has its own, because earlier frames may still be
	/// reading the others.
	/// </summary>
	private void UploadFoliageTables(FoliageSetGpuState state, IGfxDevice device, int frameSlot)
	{
		var types = CollectionsMarshal.AsSpan(_foliageTypeScratch);
		var renderers = CollectionsMarshal.AsSpan(_foliageRendererScratch);

		state.Types[frameSlot] = EnsureFoliageTableCapacity<GpuFoliageType>(
			device,
			state.Types[frameSlot],
			ref state.TypeCapacities[frameSlot],
			types.Length,
			"FoliageTypes");
		state.Renderers[frameSlot] = EnsureFoliageTableCapacity<GpuFoliageRenderer>(
			device,
			state.Renderers[frameSlot],
			ref state.RendererCapacities[frameSlot],
			renderers.Length,
			"FoliageRenderers");

		WriteBuffer<GpuFoliageType>(state.Types[frameSlot]!, types, "FoliageTypes");
		if (renderers.IsEmpty == false)
		{
			WriteBuffer<GpuFoliageRenderer>(state.Renderers[frameSlot]!, renderers, "FoliageRenderers");
		}
	}

	private static IGfxBuffer EnsureFoliageTableCapacity<T>(
		IGfxDevice device,
		IGfxBuffer? buffer,
		ref int capacity,
		int count,
		string name)
		where T : unmanaged
	{
		if (buffer is not null && capacity >= count)
		{
			return buffer;
		}

		if (buffer is IDisposable disposable)
		{
			device.Retire(disposable, name);
		}

		capacity = Math.Max(count, 8);

		return device.CreateBuffer(new BufferDescriptor(
			(ulong)(capacity * Unsafe.SizeOf<T>()),
			BufferUsage.Structured,
			BufferFlags.AllowShaderResource,
			name: name));
	}

	private void ResetFoliageCounters(IGfxDevice device, int frameSlot)
	{
		_foliageCounterBuffers[frameSlot] ??= device.CreateBuffer(new BufferDescriptor(
			GpuDrawResources.FoliageCounterCount * sizeof(uint),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			name: $"FoliageCounters[{frameSlot}]"));

		Span<uint> zeros = stackalloc uint[GpuDrawResources.FoliageCounterCount];
		zeros.Clear();
		WriteBuffer<uint>(_foliageCounterBuffers[frameSlot]!, zeros, "FoliageCounters");
	}

	private void RetireUnusedFoliageStates(IGfxDevice device)
	{
		foreach (var (runtime, state) in _foliageRuntimeStates.ToArray())
		{
			if (state.LastUsedFrame == _foliageFrame)
			{
				continue;
			}

			RetireBuffer(device, state.Instances, "FoliageInstances");
			RetireBuffer(device, state.Chunks, "FoliageChunks");
			_foliageRuntimeStates.Remove(runtime);
		}

		foreach (var (key, state) in _foliageSetStates.ToArray())
		{
			if (state.LastUsedFrame == _foliageFrame)
			{
				continue;
			}

			RetireFoliageSetState(state, device);
			_foliageSetStates.Remove(key);
		}

		foreach (var (key, state) in _detailFoliageStates.ToArray())
		{
			if (state.LastUsedFrame == _foliageFrame)
			{
				continue;
			}

			RetireDetailFoliageState(state, device);
			_detailFoliageStates.Remove(key);
		}
	}

	private static void RetireFoliageSetState(FoliageSetGpuState state, IGfxDevice device)
	{
		RetireBuffer(device, state.Levels, "FoliageLevels");
		for (var slot = 0; slot < GpuDrawResources.MaxFramesInFlight; slot++)
		{
			RetireBuffer(device, state.Types[slot], "FoliageTypes");
			RetireBuffer(device, state.Renderers[slot], "FoliageRenderers");
		}
	}

	private static void RetireBuffer(IGfxDevice device, IGfxBuffer? buffer, string name)
	{
		if (buffer is IDisposable disposable)
		{
			device.Retire(disposable, name);
		}
	}

	/// <summary>
	/// Culls every foliage set this view owns for the given views, after the instance cull has appended to the same
	/// draw args.
	/// </summary>
	private void RecordFoliageCull(
		RenderGraphContext context,
		IGfxDevice device,
		ReadOnlySpan<Matrix4x4> viewProjections,
		ReadOnlySpan<Vector4> cullingPlanes,
		ReadOnlySpan<int> cullingPlaneCounts,
		Vector3 cameraOrigin,
		bool useShadowBuffers,
		DrawPassParticipation participation,
		in MeshLodCullSettings meshLod,
		IGfxBuffer drawArgsBuffer,
		IGfxBuffer visibleInstanceBuffer)
	{
		if (_preparedFoliageSets.Count == 0 ||
		    _gpuDrawResources.FoliageExpansionCapacity == 0 ||
		    _gpuDrawResources.InstanceBuffer is not { } instanceBuffer ||
		    _gpuDrawResources.BatchBuffer is not { } batchBuffer ||
		    _foliageCounterBuffers[_gpuDrawResources.ActiveFrameSlot] is not { } counterBuffer)
		{
			return;
		}

		var viewIndex = context.View.Index;
		var pipeline = _foliageCull.GetPipeline(device);
		var writer = _foliageCull.Writer("FoliageCullParams");
		var bindings = GetFoliageCullBindings(_foliageCull.ReflectionLayout);
		var commandList = context.CommandList;
		var frameSlot = _gpuDrawResources.ActiveFrameSlot;

		using (Profiling.FrameProfiler.Instance.Measure("GpuDraw.CullFoliage"))
		{
			foreach (var prepared in _preparedFoliageSets)
			{
				if (prepared.OwnerViewIndex != viewIndex ||
				    prepared.State.Types[frameSlot] is not { } typeBuffer ||
				    prepared.State.Renderers[frameSlot] is not { } rendererBuffer)
				{
					continue;
				}

				var chunkCount = prepared.Source.ChunkCount;
				if (chunkCount == 0)
				{
					continue;
				}

				writer.Clear();
				WriteCullPlanes(writer, viewProjections, cullingPlanes, cullingPlaneCounts);
				writer.SetMatrix4x4("localToWorld", prepared.LocalToWorld);
				writer.SetVector4(
					"setParams",
					new Vector4(FoliageTypeRuntime.GetMaxScale(prepared.LocalToWorld), useShadowBuffers ? 1.0f : 0.0f, 0.0f, 0.0f));
				writer.SetVector4("cameraPosition", new Vector4(cameraOrigin, 0.0f));
				writer.SetVector4(
					"lodProjection",
					new Vector4(meshLod.ProjectionScale, meshLod.PerspectiveW, meshLod.ConstantW, meshLod.Bias));
				writer.SetFloat("lodHysteresis", meshLod.Hysteresis);
				writer.SetUInt("lodEnabled", meshLod.Enabled ? 1u : 0u);
				writer.SetUInt("lodWriteState", meshLod.WriteState ? 1u : 0u);
				writer.SetUInt("viewCount", (uint)viewProjections.Length);
				writer.SetUInt("chunkCount", (uint)chunkCount);
				writer.SetUInt("participatingLaneMask", BuildParticipatingLaneMask(participation));
				writer.SetUInt("outputDrawArgsStride", (uint)_gpuDrawResources.InstanceCapacity);
				writer.SetUInt("visibleInstanceStride", (uint)_gpuDrawResources.VisibleInstanceCapacity);
				writer.SetUInt("maxBatchCount", _gpuDrawResources.ActiveDrawCommandUpperBound);
				writer.SetUInt("expansionBase", (uint)_gpuDrawResources.FoliageExpansionBase);
				writer.SetUInt("expansionCapacity", (uint)_gpuDrawResources.FoliageExpansionCapacity);
				writer.SetUInt("ownerDrawFlags", (uint)viewIndex << GpuDrawFlags.OwnerViewShift);
				writer.SetUInt("typeCount", (uint)prepared.Source.Types.Count);

				commandList.BindPipeline(pipeline);
				commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
				commandList.SetComputeBuffer(bindings.InstanceTable, instanceBuffer);
				commandList.SetComputeBuffer(bindings.DrawArgs, drawArgsBuffer);
				commandList.SetComputeBuffer(bindings.VisibleInstances, visibleInstanceBuffer);
				commandList.SetComputeBuffer(bindings.Levels, prepared.State.Levels);
				commandList.SetComputeBuffer(bindings.Counters, counterBuffer);
				commandList.SetComputeReadOnlyBuffer(bindings.Batches, batchBuffer);
				commandList.SetComputeReadOnlyBuffer(bindings.Instances, prepared.Instances);
				commandList.SetComputeReadOnlyBuffer(bindings.Chunks, prepared.Chunks);
				commandList.SetComputeReadOnlyBuffer(bindings.Types, typeBuffer);
				commandList.SetComputeReadOnlyBuffer(bindings.Renderers, rendererBuffer);

				const int groupsPerRow = 65535;
				var groupCountX = (uint)Math.Min(chunkCount, groupsPerRow);
				var groupCountY = (uint)((chunkCount + groupsPerRow - 1) / groupsPerRow);
				commandList.Dispatch(groupCountX, groupCountY, 1);
			}
		}
	}

	/// <summary>
	/// Generates every prepared set's detail foliage around its view's camera, one dispatch per detail type. Runs once a
	/// frame after the table updates, because it reads the terrain tables, and before any view culls, because every
	/// view's cull reads what it writes.
	/// </summary>
	private void RecordDetailGeneration(RenderGraphContext context, IGfxDevice device)
	{
		if (_gpuDrawResources.TerrainMaterialBuffer is not { } terrainMaterials ||
		    _gpuDrawResources.TerrainLayerBuffer is not { } terrainLayers)
		{
			return;
		}

		var commandList = context.CommandList;
		using (Profiling.FrameProfiler.Instance.Measure("GpuDraw.GenerateDetailFoliage"))
		{
			foreach (var prepared in _preparedFoliageSets)
			{
				if (prepared.Detail is not { TerrainMaterialHandle: not 0u } detail)
				{
					continue;
				}

				var pipeline = _detailGenerate.GetPipeline(device);
				var writer = _detailGenerate.Writer("DetailGenerateParams");
				var bindings = GetDetailGenerateBindings(_detailGenerate.ReflectionLayout);
				var set = detail.Set;
				var runtime = set.Runtime;
				var setScale = FoliageTypeRuntime.GetMaxScale(set.LocalToWorld);
				commandList.BindPipeline(pipeline);
				commandList.SetComputeBuffer(bindings.Instances, detail.Gpu.Instances);
				commandList.SetComputeBuffer(bindings.Chunks, detail.Gpu.Chunks);
				commandList.SetComputeReadOnlyBuffer(bindings.TerrainMaterials, terrainMaterials);
				commandList.SetComputeReadOnlyBuffer(bindings.TerrainLayers, terrainLayers);
				commandList.SetComputeReadOnlyBuffer(bindings.Entries, detail.Gpu.Entries);
				foreach (var detailType in runtime.DetailTypes)
				{
					var (originX, originZ) = TerrainDetailRuntime.GetWindowOrigin(detailType, set.LocalCamera);
					var bounds = detailType.Type.Bounds;
					writer.Clear();
					writer.SetVector4("localCamera", new Vector4(set.LocalCamera, 0.0f));
					writer.SetVector4("terrainSize", new Vector4(set.TerrainSize, set.HeightScale, setScale));
					writer.SetVector4("typeBounds", new Vector4(bounds.Center, bounds.Radius));
					writer.SetVector4(
						"grid",
						new Vector4(detailType.Spacing, detailType.TileSize, detailType.Distance, detailType.FadeDistance));
					writer.SetInt("windowOriginX", originX);
					writer.SetInt("windowOriginZ", originZ);
					writer.SetUInt("gridDimension", (uint)detailType.GridDimension);
					writer.SetUInt("chunkStart", (uint)detailType.ChunkStart);
					writer.SetFloat("maxDensity", detailType.MaxDensity);
					writer.SetFloat("densityScale", 1.0f);
					writer.SetFloat("shadowDistance", detailType.Type.ShadowDistance);
					writer.SetUInt("typeIndex", (uint)detailType.TypeIndex);
					writer.SetUInt("typeCount", (uint)runtime.Types.Length);
					writer.SetUInt("layerCount", (uint)runtime.LayerCount);
					writer.SetUInt("seed", detailType.Seed);
					writer.SetUInt("terrainMaterialHandle", detail.TerrainMaterialHandle);
					commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());

					const int groupsPerRow = 65535;
					var tileCount = detailType.ChunkCount;
					var groupCountX = (uint)Math.Min(tileCount, groupsPerRow);
					var groupCountY = (uint)((tileCount + groupsPerRow - 1) / groupsPerRow);
					commandList.Dispatch(groupCountX, groupCountY, 1);
				}

				commandList.SynchronizeUnorderedAccess(detail.Gpu.Instances);
				commandList.SynchronizeUnorderedAccess(detail.Gpu.Chunks);
			}
		}
	}

	private static DetailGenerateBindings GetDetailGenerateBindings(ShaderReflectionLayout layout)
	{
		return new DetailGenerateBindings(
			layout.GetResource("g_DetailInstances").RegisterIndex,
			layout.GetResource("g_DetailChunks").RegisterIndex,
			layout.GetResource("g_TerrainMaterialTable").RegisterIndex,
			layout.GetResource("g_TerrainLayerTable").RegisterIndex,
			layout.GetResource("g_DetailEntries").RegisterIndex);
	}

	/// <summary>Writes each view's culling planes the way both cull shaders lay them out.</summary>
	private static void WriteCullPlanes(
		ShaderPropertyWriter writer,
		ReadOnlySpan<Matrix4x4> viewProjections,
		ReadOnlySpan<Vector4> cullingPlanes,
		ReadOnlySpan<int> cullingPlaneCounts)
	{
		Span<Vector4> planes = stackalloc Vector4[6];
		for (var viewIndex = 0; viewIndex < viewProjections.Length; viewIndex++)
		{
			scoped ReadOnlySpan<Vector4> viewPlanes;
			if (cullingPlanes.IsEmpty)
			{
				FrustumCulling.ExtractPlanes(viewProjections[viewIndex], planes);
				viewPlanes = planes;
			}
			else
			{
				viewPlanes = cullingPlanes.Slice(viewIndex * FrustumCulling.MaxPlaneCount, cullingPlaneCounts[viewIndex]);
			}

			writer.SetUInt($"planeCounts[{viewIndex}]", (uint)viewPlanes.Length);
			for (var planeIndex = 0; planeIndex < viewPlanes.Length; planeIndex++)
			{
				var flattenedPlaneIndex = (viewIndex * FrustumCulling.MaxPlaneCount) + planeIndex;
				writer.SetVector4($"planes[{flattenedPlaneIndex}]", viewPlanes[planeIndex]);
			}
		}
	}

	private static FoliageCullBindings GetFoliageCullBindings(ShaderReflectionLayout layout)
	{
		return new FoliageCullBindings(
			layout.GetResource("g_InstanceTable").RegisterIndex,
			layout.GetResource("g_DrawArgs").RegisterIndex,
			layout.GetResource("g_VisibleInstances").RegisterIndex,
			layout.GetResource("g_FoliageLevels").RegisterIndex,
			layout.GetResource("g_FoliageCounters").RegisterIndex,
			layout.GetResource("g_Batches").RegisterIndex,
			layout.GetResource("g_FoliageInstances").RegisterIndex,
			layout.GetResource("g_FoliageChunks").RegisterIndex,
			layout.GetResource("g_FoliageTypes").RegisterIndex,
			layout.GetResource("g_FoliageRenderers").RegisterIndex);
	}

	/// <summary>Placed foliage drawn by the last update, for diagnostics.</summary>
	public FoliageDrawStats GetFoliageStats()
	{
		var sets = 0;
		var instances = 0;
		var renderers = 0;
		var detailSets = 0;
		var detailTypes = 0;
		var detailCandidates = 0;
		var detailDistanceScale = 1.0f;
		foreach (var prepared in _preparedFoliageSets)
		{
			if (prepared.Detail is { } detail)
			{
				detailSets++;
				detailTypes += detail.Set.Runtime.DetailTypes.Length;
				detailCandidates += detail.Set.Runtime.CandidateCount;
				detailDistanceScale = MathF.Min(detailDistanceScale, detail.Set.Runtime.DistanceScale);
				continue;
			}

			sets++;
			instances += prepared.Source.SlotCount;
			renderers += prepared.Source.RendererInstanceCount;
		}

		var reservedSlots = 0L;
		foreach (var reservation in _foliageReservations.Values)
		{
			reservedSlots += reservation.SlotCount;
		}

		return new FoliageDrawStats(
			sets,
			instances,
			renderers,
			_foliageReservations.Count,
			reservedSlots,
			_gpuDrawResources.FoliageExpansionCapacity,
			detailSets,
			detailTypes,
			detailCandidates,
			detailDistanceScale);
	}
}

/// <summary>Foliage in the last frame's draw tables. See docs/foliage.md.</summary>
/// <param name="SetCount">Terrains with placed foliage, per view that draws them.</param>
/// <param name="ReservedBatchSlots">Visible list slots reserved across every foliage batch, per view.</param>
/// <param name="ExpansionCapacity">Instance slots the cull can expand visible foliage into per frame.</param>
/// <param name="DetailSetCount">Terrains growing detail foliage, per view that draws them.</param>
/// <param name="DetailTypeCount">Detail types generated, across those sets.</param>
/// <param name="DetailCandidateCount">Candidate plants generated per frame, kept or not.</param>
/// <param name="DetailDistanceScale">
/// The smallest factor a set's detail distances were shrunk by to stay within its candidate limit; 1 when none was.
/// </param>
public readonly record struct FoliageDrawStats(
	int SetCount,
	int InstanceCount,
	int RendererInstanceCount,
	int ReservationCount,
	long ReservedBatchSlots,
	int ExpansionCapacity,
	int DetailSetCount,
	int DetailTypeCount,
	int DetailCandidateCount,
	float DetailDistanceScale);
