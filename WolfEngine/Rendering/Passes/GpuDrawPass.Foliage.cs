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
/// Placed foliage: the GPU copies of each terrain's instances, the per-frame type and renderer tables, the batch slots
/// reserved for them, and the cull that expands visible instances into the instance table's foliage region. See
/// docs/foliage.md.
/// </summary>
public sealed partial class GpuDrawPass
{
	/// <summary>
	/// Most instance slots the foliage region is grown to. Visible foliage past it is dropped and counted in the foliage
	/// counters.
	/// </summary>
	private const int MaxFoliageExpansionSlots = 262144;

	/// <summary>Views a frame culls foliage for: the camera, and the shadow cascades.</summary>
	private const int FoliageExpansionViewGroups = 2;

	private readonly Dictionary<TerrainFoliageRuntime, FoliageRuntimeGpuState> _foliageRuntimeStates = new();
	private readonly Dictionary<FoliageSetKey, FoliageSetGpuState> _foliageSetStates = new();
	private readonly Dictionary<FoliageReservationKey, FoliageReservation> _foliageReservations = new();
	private readonly HashSet<FoliageReservationKey> _desiredFoliageReservations = new();
	private readonly List<FoliageReservationKey> _foliageReservationScratch = new();
	private readonly List<PreparedFoliageSet> _preparedFoliageSets = new();
	private readonly List<GpuFoliageType> _foliageTypeScratch = new();
	private readonly List<GpuFoliageRenderer> _foliageRendererScratch = new();
	private readonly IGfxBuffer?[] _foliageCounterBuffers = new IGfxBuffer?[GpuDrawResources.MaxFramesInFlight];
	private int _foliageReservationClearGeneration = -1;
	private int _foliageFrame;
	private ReadOnlyMemory<byte> _foliageCullShaderBytecode;
	private ComputeThreadGroupSize? _foliageCullThreadGroupSize;
	private ShaderPropertyWriter? _foliageCullParamsWriter;
	private FoliageCullBindings? _foliageCullBindings;
	private GraphicsBackendKind? _foliageCullBackendKind;
	private IGfxPipeline? _foliageCullPipeline;

	private readonly record struct FoliageSetKey(int OwnerViewIndex, int WorldId, Entity Entity);

	private readonly record struct FoliageReservationKey(
		FoliageSetKey Set,
		TerrainFoliageRuntime Runtime,
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

	private readonly record struct PreparedFoliageSet(
		int OwnerViewIndex,
		GpuFoliageSet Set,
		FoliageRuntimeGpuState Runtime,
		FoliageSetGpuState State);

	/// <summary>
	/// Instance slots the foliage cull could expand into this frame if every placed instance were visible to both the
	/// camera and the shadow cascades, capped at <see cref="MaxFoliageExpansionSlots"/>.
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

				var setKey = new FoliageSetKey(source.Owner.Index, set.WorldId, set.Entity);
				var runtimeState = EnsureFoliageRuntimeState(set.Runtime, device);
				var setState = EnsureFoliageSetState(setKey, set.Runtime, device);
				BuildFoliageTables(setKey, set, source.Database);
				UploadFoliageTables(setState, device, frameSlot);
				_preparedFoliageSets.Add(new PreparedFoliageSet(source.Owner.Index, set, runtimeState, setState));
			}
		}

		ReleaseUndesiredFoliageReservations();
		RetireUnusedFoliageStates(device);
	}

	private void BuildFoliageTables(FoliageSetKey setKey, in GpuFoliageSet set, GpuDrawDatabase database)
	{
		_foliageTypeScratch.Clear();
		_foliageRendererScratch.Clear();

		var runtime = set.Runtime;
		for (var typeIndex = 0; typeIndex < runtime.Types.Length; typeIndex++)
		{
			var gpuType = new GpuFoliageType
			{
				RendererStart = (uint)_foliageRendererScratch.Count
			};

			var instanceCount = (uint)runtime.TypeInstanceCounts[typeIndex];
			if (runtime.Types[typeIndex] is { } type && instanceCount > 0)
			{
				for (var rendererIndex = 0; rendererIndex < type.Renderers.Count; rendererIndex++)
				{
					if (TryBuildFoliageRenderer(
						    setKey,
						    set,
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
		in GpuFoliageSet set,
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
			renderer.LocalTransform * set.LocalToWorld);
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
				set.Runtime,
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

	private FoliageSetGpuState EnsureFoliageSetState(FoliageSetKey key, TerrainFoliageRuntime runtime, IGfxDevice device)
	{
		if (_foliageSetStates.TryGetValue(key, out var state) && state.LevelCapacity >= runtime.InstanceCount)
		{
			state.LastUsedFrame = _foliageFrame;
			return state;
		}

		if (state is not null)
		{
			RetireFoliageSetState(state, device);
		}

		var levelCapacity = Math.Max(runtime.InstanceCount, 1);
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
		var pipeline = EnsureFoliageCullPipeline(device);
		var writer = _foliageCullParamsWriter!;
		var bindings = _foliageCullBindings!;
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

				var set = prepared.Set;
				var chunkCount = set.Runtime.Chunks.Length;
				if (chunkCount == 0)
				{
					continue;
				}

				writer.Clear();
				WriteCullPlanes(writer, viewProjections, cullingPlanes, cullingPlaneCounts);
				writer.SetMatrix4x4("localToWorld", set.LocalToWorld);
				writer.SetVector4(
					"setParams",
					new Vector4(FoliageTypeRuntime.GetMaxScale(set.LocalToWorld), useShadowBuffers ? 1.0f : 0.0f, 0.0f, 0.0f));
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

				commandList.BindPipeline(pipeline);
				commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
				commandList.SetComputeBuffer(bindings.InstanceTable, instanceBuffer);
				commandList.SetComputeBuffer(bindings.DrawArgs, drawArgsBuffer);
				commandList.SetComputeBuffer(bindings.VisibleInstances, visibleInstanceBuffer);
				commandList.SetComputeBuffer(bindings.Levels, prepared.State.Levels);
				commandList.SetComputeBuffer(bindings.Counters, counterBuffer);
				commandList.SetComputeReadOnlyBuffer(bindings.Batches, batchBuffer);
				commandList.SetComputeReadOnlyBuffer(bindings.Instances, prepared.Runtime.Instances);
				commandList.SetComputeReadOnlyBuffer(bindings.Chunks, prepared.Runtime.Chunks);
				commandList.SetComputeReadOnlyBuffer(bindings.Types, typeBuffer);
				commandList.SetComputeReadOnlyBuffer(bindings.Renderers, rendererBuffer);

				const int groupsPerRow = 65535;
				var groupCountX = (uint)Math.Min(chunkCount, groupsPerRow);
				var groupCountY = (uint)((chunkCount + groupsPerRow - 1) / groupsPerRow);
				commandList.Dispatch(groupCountX, groupCountY, 1);
			}
		}
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

	private IGfxPipeline EnsureFoliageCullPipeline(IGfxDevice device)
	{
		if (_foliageCullPipeline is not null && _foliageCullBackendKind == device.BackendKind)
		{
			return _foliageCullPipeline;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.FoliageCull,
			"CSCullFoliage",
			device.BackendKind);
		_foliageCullShaderBytecode = compiled.Bytecode;
		_foliageCullThreadGroupSize = compiled.ThreadGroupSize;
		_foliageCullParamsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("FoliageCullParams"));

		var layout = compiled.ReflectionLayout;
		_foliageCullBindings = new FoliageCullBindings(
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

		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSCullFoliage", default, default, default);
		_foliageCullPipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(
				compute: _foliageCullShaderBytecode,
				computeThreadGroupSize: _foliageCullThreadGroupSize));
		_foliageCullBackendKind = device.BackendKind;

		return _foliageCullPipeline;
	}

	/// <summary>Placed foliage drawn by the last update, for diagnostics.</summary>
	public FoliageDrawStats GetFoliageStats()
	{
		var sets = 0;
		var instances = 0;
		var renderers = 0;
		foreach (var prepared in _preparedFoliageSets)
		{
			sets++;
			instances += prepared.Set.Runtime.InstanceCount;
			renderers += prepared.Set.Runtime.RendererInstanceCount;
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
			_gpuDrawResources.FoliageExpansionCapacity);
	}
}

/// <summary>Placed foliage in the last frame's draw tables. See docs/foliage.md.</summary>
/// <param name="ReservedBatchSlots">Visible list slots reserved across every foliage batch, per view.</param>
/// <param name="ExpansionCapacity">Instance slots the cull can expand visible foliage into per frame.</param>
public readonly record struct FoliageDrawStats(
	int SetCount,
	int InstanceCount,
	int RendererInstanceCount,
	int ReservationCount,
	long ReservedBatchSlots,
	int ExpansionCapacity);
