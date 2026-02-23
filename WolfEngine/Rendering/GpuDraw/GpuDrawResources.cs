#nullable enable

using System;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

public sealed class GpuDrawResources : IDisposable
{
	public const int IndirectCommandBufferSlotCount = 4;
	public const int MaxFramesInFlight = 4;
	public const int MaxDrawCount = 20000;
	public const int MaxInstanceCount = 20000;
	public const int MaxMaterialCount = 8192;
	public const int MaxMeshCount = 20000;
	public const int HardeningCounterCount = 16;

	public IGfxBuffer? InstanceBuffer { get; private set; }
	public IGfxBuffer? MaterialBuffer { get; private set; }
	public IGfxBuffer? MeshBuffer { get; private set; }
	public IGfxBuffer? DrawCommandBuffer { get; private set; }
	public IGfxBuffer? DrawArgsBuffer { get; private set; }
	public IGfxBuffer? VisibleDrawIdsPerBucketBuffer { get; private set; }
	public IGfxBuffer? DrawGenerationBuffer { get; private set; }
	public IGfxBuffer? InstanceGenerationBuffer { get; private set; }
	public IGfxBuffer? MaterialGenerationBuffer { get; private set; }
	public IGfxBuffer? MeshGenerationBuffer { get; private set; }
	public IGfxBuffer? DiagnosticsCounterBuffer { get; private set; }
	private readonly IGfxBuffer?[] _updateBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _cameraBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _drawCountPerBucketBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private readonly IGfxBuffer?[] _drawExecutionRangePerBucketBuffers = new IGfxBuffer?[MaxFramesInFlight];
	private int _activeFrameSlot;
	private readonly IGfxIndirectCommandBuffer?[] _gbufferIndirectCommandSlots =
		new IGfxIndirectCommandBuffer?[IndirectCommandBufferSlotCount * GBufferDrawBuckets.BucketCount];
	private readonly IGfxPipeline?[] _gbufferPipelines = new IGfxPipeline?[GBufferDrawBuckets.BucketCount];
	private int _activeIndirectCommandSlot;
	public uint ActiveDrawCommandUpperBound { get; set; } = 1;

	public int ActiveIndirectCommandSlot
	{
		get => _activeIndirectCommandSlot;
		set
		{
			if (value < 0 || value >= IndirectCommandBufferSlotCount)
			{
				throw new ArgumentOutOfRangeException(nameof(value), value, "Indirect command buffer slot is out of range.");
			}

			_activeIndirectCommandSlot = value;
		}
	}

	public int ActiveFrameSlot
	{
		get => _activeFrameSlot;
		set
		{
			if (value < 0 || value >= MaxFramesInFlight)
			{
				throw new ArgumentOutOfRangeException(nameof(value), value, "Frame slot is out of range.");
			}

			_activeFrameSlot = value;
		}
	}

	public IGfxBuffer? UpdateBuffer => _updateBuffers[_activeFrameSlot];

	public IGfxBuffer? CameraBuffer => _cameraBuffers[_activeFrameSlot];

	public IGfxBuffer? DrawCountPerBucketBuffer => _drawCountPerBucketBuffers[_activeFrameSlot];

	public IGfxBuffer? DrawExecutionRangePerBucketBuffer => _drawExecutionRangePerBucketBuffers[_activeFrameSlot];

	public void EnsureCreated(IGfxDevice device)
	{
		if (device is null)
		{
			throw new ArgumentNullException(nameof(device));
		}

		InstanceBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxInstanceCount * Marshal.SizeOf<GpuInstanceData>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		MaterialBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxMaterialCount * Marshal.SizeOf<GpuMaterialData>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		MeshBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxMeshCount * Marshal.SizeOf<GpuMeshData>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		DrawCommandBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxDrawCount * Marshal.SizeOf<GpuDrawCommand>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		DrawArgsBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxDrawCount * Marshal.SizeOf<GpuDrawArgs>()),
			BufferUsage.Indirect,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		VisibleDrawIdsPerBucketBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxDrawCount * GBufferDrawBuckets.BucketCount * sizeof(uint)),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		DrawGenerationBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)((MaxDrawCount + 1) * sizeof(uint)),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		InstanceGenerationBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)((MaxInstanceCount + 1) * sizeof(uint)),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		MaterialGenerationBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)((MaxMaterialCount + 1) * sizeof(uint)),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		MeshGenerationBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)((MaxMeshCount + 1) * sizeof(uint)),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		DiagnosticsCounterBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(HardeningCounterCount * sizeof(uint)),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		for (var i = 0; i < MaxFramesInFlight; i++)
		{
			_updateBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(MaxDrawCount * Marshal.SizeOf<GpuDrawUpdateData>()),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_cameraBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(sizeof(float) * 24),
				BufferUsage.Constant,
				BufferFlags.AllowShaderResource));

			_drawCountPerBucketBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(GBufferDrawBuckets.BucketCount * sizeof(uint)),
				BufferUsage.Indirect,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

			_drawExecutionRangePerBucketBuffers[i] ??= device.CreateBuffer(new BufferDescriptor(
				(ulong)(GBufferDrawBuckets.BucketCount * 2 * sizeof(uint)),
				BufferUsage.Indirect,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));
		}

		for (var slotIndex = 0; slotIndex < IndirectCommandBufferSlotCount; slotIndex++)
		{
			for (var bucketIndex = 0; bucketIndex < GBufferDrawBuckets.BucketCount; bucketIndex++)
			{
				var index = FlattenSlotBucketIndex(slotIndex, bucketIndex);
				_gbufferIndirectCommandSlots[index] ??= device.CreateIndirectCommandBuffer(new IndirectCommandBufferDescriptor(
					PassKind.Graphics,
					(uint)MaxDrawCount,
					supportsIndexedExecution: true));
			}
		}
	}

	public IGfxIndirectCommandBuffer? GetIndirectCommandBufferSlot(int slotIndex, int bucketIndex)
	{
		if (slotIndex < 0 || slotIndex >= IndirectCommandBufferSlotCount)
		{
			throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "Indirect command buffer slot is out of range.");
		}

		if (bucketIndex < 0 || bucketIndex >= GBufferDrawBuckets.BucketCount)
		{
			throw new ArgumentOutOfRangeException(nameof(bucketIndex), bucketIndex, "GBuffer bucket index is out of range.");
		}

		return _gbufferIndirectCommandSlots[FlattenSlotBucketIndex(slotIndex, bucketIndex)];
	}

	public IGfxPipeline? GetGBufferPipeline(int bucketIndex)
	{
		if (bucketIndex < 0 || bucketIndex >= _gbufferPipelines.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(bucketIndex), bucketIndex, "GBuffer bucket index is out of range.");
		}

		return _gbufferPipelines[bucketIndex];
	}

	public void SetGBufferPipeline(int bucketIndex, IGfxPipeline pipeline)
	{
		if (bucketIndex < 0 || bucketIndex >= _gbufferPipelines.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(bucketIndex), bucketIndex, "GBuffer bucket index is out of range.");
		}

		_gbufferPipelines[bucketIndex] = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
	}

	public void Dispose()
	{
		(InstanceBuffer as IDisposable)?.Dispose();
		(MaterialBuffer as IDisposable)?.Dispose();
		(MeshBuffer as IDisposable)?.Dispose();
		(DrawCommandBuffer as IDisposable)?.Dispose();
		(DrawArgsBuffer as IDisposable)?.Dispose();
		(VisibleDrawIdsPerBucketBuffer as IDisposable)?.Dispose();
		(DrawGenerationBuffer as IDisposable)?.Dispose();
		(InstanceGenerationBuffer as IDisposable)?.Dispose();
		(MaterialGenerationBuffer as IDisposable)?.Dispose();
		(MeshGenerationBuffer as IDisposable)?.Dispose();
		(DiagnosticsCounterBuffer as IDisposable)?.Dispose();
		for (var i = 0; i < MaxFramesInFlight; i++)
		{
			(_updateBuffers[i] as IDisposable)?.Dispose();
			(_cameraBuffers[i] as IDisposable)?.Dispose();
			(_drawCountPerBucketBuffers[i] as IDisposable)?.Dispose();
			(_drawExecutionRangePerBucketBuffers[i] as IDisposable)?.Dispose();
			_updateBuffers[i] = null;
			_cameraBuffers[i] = null;
			_drawCountPerBucketBuffers[i] = null;
			_drawExecutionRangePerBucketBuffers[i] = null;
		}
		for (var i = 0; i < _gbufferIndirectCommandSlots.Length; i++)
		{
			(_gbufferIndirectCommandSlots[i] as IDisposable)?.Dispose();
			_gbufferIndirectCommandSlots[i] = null;
		}

		Array.Clear(_gbufferPipelines, 0, _gbufferPipelines.Length);
	}

	private static int FlattenSlotBucketIndex(int slotIndex, int bucketIndex) =>
		(slotIndex * GBufferDrawBuckets.BucketCount) + bucketIndex;

}
