#nullable enable

using System;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public sealed class GpuDrawResources : IDisposable
{
	public const int MaxDrawCount = 10000;
	public const int MaxInstanceCount = 10000;
	public const int MaxMaterialCount = 4096;
	public const int MaxMeshCount = 4096;

	public IGfxBuffer? InstanceBuffer { get; private set; }
	public IGfxBuffer? MaterialBuffer { get; private set; }
	public IGfxBuffer? MeshBuffer { get; private set; }
	public IGfxBuffer? DrawCommandBuffer { get; private set; }
	public IGfxBuffer? DrawArgsBuffer { get; private set; }
	public IGfxBuffer? DrawCountBuffer { get; private set; }
	public IGfxBuffer? VisibleDrawIdsBuffer { get; private set; }
	public IGfxBuffer? DrawExecutionRangeBuffer { get; private set; }
	public IGfxBuffer? UpdateBuffer { get; private set; }
	public IGfxBuffer? CameraBuffer { get; private set; }
	public IGfxIndirectCommandBuffer? GBufferIndirectCommands { get; private set; }
	public IGfxPipeline? GBufferPipeline { get; set; }

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

		DrawCountBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)sizeof(uint),
			BufferUsage.Indirect,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		VisibleDrawIdsBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(MaxDrawCount * sizeof(uint)),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));

		DrawExecutionRangeBuffer ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)(2 * sizeof(uint)),
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

		GBufferIndirectCommands ??= device.CreateIndirectCommandBuffer(new IndirectCommandBufferDescriptor(
			PassKind.Graphics,
			(uint)MaxDrawCount));
	}

	public void Dispose()
	{
		(InstanceBuffer as IDisposable)?.Dispose();
		(MaterialBuffer as IDisposable)?.Dispose();
		(MeshBuffer as IDisposable)?.Dispose();
		(DrawCommandBuffer as IDisposable)?.Dispose();
		(DrawArgsBuffer as IDisposable)?.Dispose();
		(DrawCountBuffer as IDisposable)?.Dispose();
		(VisibleDrawIdsBuffer as IDisposable)?.Dispose();
		(DrawExecutionRangeBuffer as IDisposable)?.Dispose();
		(UpdateBuffer as IDisposable)?.Dispose();
		(CameraBuffer as IDisposable)?.Dispose();
		(GBufferIndirectCommands as IDisposable)?.Dispose();
	}

}
