#nullable enable

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct D3D12RayTracingInstanceData
{
	public float M11;
	public float M12;
	public float M13;
	public float M14;
	public float M21;
	public float M22;
	public float M23;
	public float M24;
	public float M31;
	public float M32;
	public float M33;
	public float M34;
	public uint InstanceIdAndMask;
	public uint InstanceContributionAndFlags;
	public ulong AccelerationStructure;

	public static D3D12RayTracingInstanceData Create(
		in RayTracingInstanceDescription instance,
		ulong accelerationStructureAddress)
	{
		var transform = instance.Transform;
		return new D3D12RayTracingInstanceData
		{
			// D3D12 stores the affine transform as a row-major 3x4 matrix; Matrix4x4
			// uses row-vector transforms, so transpose its 3x4 affine portion here.
			M11 = transform.M11, M12 = transform.M21, M13 = transform.M31, M14 = transform.M41,
			M21 = transform.M12, M22 = transform.M22, M23 = transform.M32, M24 = transform.M42,
			M31 = transform.M13, M32 = transform.M23, M33 = transform.M33, M34 = transform.M43,
			InstanceIdAndMask = (instance.InstanceIndex & 0x00FF_FFFFu) |
				((instance.Active ? instance.Mask : 0u) & 0xFFu) << 24,
			InstanceContributionAndFlags = 0,
			AccelerationStructure = accelerationStructureAddress
		};
	}
}

internal static class D3D12RayTracingGeometryValidation
{
	public static void Validate(
		in BottomLevelAccelerationStructureDescriptor descriptor,
		D3D12Buffer vertexBuffer,
		D3D12Buffer indexBuffer)
	{
		var vertexBytes = checked((ulong)descriptor.VertexCount * descriptor.VertexStrideBytes);
		var indexBytes = checked((ulong)descriptor.IndexCount * sizeof(uint));
		if (descriptor.VertexBufferOffsetBytes > vertexBuffer.SizeInBytes ||
			vertexBytes > vertexBuffer.SizeInBytes - descriptor.VertexBufferOffsetBytes)
		{
			throw new InvalidOperationException(
				$"DXR BLAS vertex range is outside its buffer: offset={descriptor.VertexBufferOffsetBytes}, " +
				$"bytes={vertexBytes}, bufferSize={vertexBuffer.SizeInBytes}.");
		}
		if (descriptor.IndexBufferOffsetBytes > indexBuffer.SizeInBytes ||
			indexBytes > indexBuffer.SizeInBytes - descriptor.IndexBufferOffsetBytes)
		{
			throw new InvalidOperationException(
				$"DXR BLAS index range is outside its buffer: offset={descriptor.IndexBufferOffsetBytes}, " +
				$"bytes={indexBytes}, bufferSize={indexBuffer.SizeInBytes}.");
		}
	}
}

internal sealed unsafe class D3D12BottomLevelAccelerationStructure : IGfxBottomLevelAccelerationStructure, IDisposable
{
	public D3D12BottomLevelAccelerationStructure(
		in BottomLevelAccelerationStructureDescriptor descriptor,
		ComPtr<ID3D12Resource> result,
		ComPtr<ID3D12Resource> scratch)
	{
		Descriptor = descriptor;
		Result = result;
		Scratch = scratch;
	}

	public BottomLevelAccelerationStructureDescriptor Descriptor { get; }
	public string? Name => null;
	public ComPtr<ID3D12Resource> Result { get; private set; }
	public ComPtr<ID3D12Resource> Scratch { get; private set; }

	public void Dispose()
	{
		Scratch.Dispose();
		Result.Dispose();
	}
}

internal sealed unsafe class D3D12TopLevelAccelerationStructure : IGfxTopLevelAccelerationStructure, IDisposable
{
	public D3D12TopLevelAccelerationStructure(
		in TopLevelAccelerationStructureDescriptor descriptor,
		ComPtr<ID3D12Resource> result,
		ComPtr<ID3D12Resource> scratch,
		ComPtr<ID3D12Resource> instanceDescriptions)
	{
		Descriptor = descriptor;
		Result = result;
		Scratch = scratch;
		InstanceDescriptions = instanceDescriptions;
	}

	public TopLevelAccelerationStructureDescriptor Descriptor { get; }
	public string? Name => null;
	public ComPtr<ID3D12Resource> Result { get; private set; }
	public ComPtr<ID3D12Resource> Scratch { get; private set; }
	public ComPtr<ID3D12Resource> InstanceDescriptions { get; private set; }

	public void Dispose()
	{
		InstanceDescriptions.Dispose();
		Scratch.Dispose();
		Result.Dispose();
	}
}
