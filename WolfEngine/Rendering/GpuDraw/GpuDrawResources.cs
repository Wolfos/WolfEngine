#nullable enable

using System;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

public sealed class GpuDrawResources : IDisposable
{
	public const int IndirectCommandBufferSlotCount = 4;
	public const int MaxDrawCount = 20000;
	public const int MaxInstanceCount = 20000;
	public const int MaxMaterialCount = 8192;
	public const int MaxMeshCount = 20000;

	public IGfxBuffer? InstanceBuffer { get; private set; }
	public IGfxBuffer? MaterialBuffer { get; private set; }
	public IGfxBuffer? MeshBuffer { get; private set; }
	public IGfxBuffer? DrawCommandBuffer { get; private set; }
	public IGfxBuffer? DrawArgsBuffer { get; private set; }
	public IGfxBuffer? DrawCountPerBucketBuffer { get; private set; }
	public IGfxBuffer? VisibleDrawIdsPerBucketBuffer { get; private set; }
	public IGfxBuffer? DrawExecutionRangePerBucketBuffer { get; private set; }
	public IGfxBuffer? UpdateBuffer { get; private set; }
	public IGfxBuffer? CameraBuffer { get; private set; }
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

		DrawCountPerBucketBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(GBufferDrawBuckets.BucketCount * sizeof(uint)),
			BufferUsage.Indirect,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		VisibleDrawIdsPerBucketBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxDrawCount * GBufferDrawBuckets.BucketCount * sizeof(uint)),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		DrawExecutionRangePerBucketBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(GBufferDrawBuckets.BucketCount * 2 * sizeof(uint)),
			BufferUsage.Indirect,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		UpdateBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxDrawCount * Marshal.SizeOf<GpuDrawUpdateData>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		CameraBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(sizeof(float) * 24),
			BufferUsage.Constant,
			BufferFlags.AllowShaderResource));

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
		(DrawCountPerBucketBuffer as IDisposable)?.Dispose();
		(VisibleDrawIdsPerBucketBuffer as IDisposable)?.Dispose();
		(DrawExecutionRangePerBucketBuffer as IDisposable)?.Dispose();
		(UpdateBuffer as IDisposable)?.Dispose();
		(CameraBuffer as IDisposable)?.Dispose();
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
