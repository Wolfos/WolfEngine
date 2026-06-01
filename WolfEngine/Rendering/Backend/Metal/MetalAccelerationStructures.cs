using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct MetalAccelerationStructureInstanceDescriptorData
{
	public float Column0X;
	public float Column0Y;
	public float Column0Z;
	public float Column1X;
	public float Column1Y;
	public float Column1Z;
	public float Column2X;
	public float Column2Y;
	public float Column2Z;
	public float Column3X;
	public float Column3Y;
	public float Column3Z;
	public uint Options;
	public uint Mask;
	public uint IntersectionFunctionTableOffset;
	public uint AccelerationStructureIndex;
}

internal sealed class MetalBottomLevelAccelerationStructure : IGfxBottomLevelAccelerationStructure, IDisposable
{
	public MTLPrimitiveAccelerationStructureDescriptor MetalDescriptor;

	public MetalBottomLevelAccelerationStructure(
		string name,
		in BottomLevelAccelerationStructureDescriptor descriptor,
		MTLPrimitiveAccelerationStructureDescriptor metalDescriptor,
		MTLAccelerationStructure accelerationStructure,
		MTLBuffer scratchBuffer,
		MTLFence buildFence)
	{
		Name = name;
		Descriptor = descriptor;
		MetalDescriptor = metalDescriptor;
		AccelerationStructure = accelerationStructure;
		ScratchBuffer = scratchBuffer;
		BuildFence = buildFence;
	}

	public string Name { get; }

	public BottomLevelAccelerationStructureDescriptor Descriptor { get; }

	public MTLAccelerationStructure AccelerationStructure { get; }

	public MTLBuffer ScratchBuffer { get; }

	public MTLFence BuildFence { get; }

	public void Dispose()
	{
		if (BuildFence.NativePtr != IntPtr.Zero)
		{
			BuildFence.Dispose();
		}

		if (ScratchBuffer.NativePtr != IntPtr.Zero)
		{
			ScratchBuffer.Dispose();
		}

		if (AccelerationStructure.NativePtr != IntPtr.Zero)
		{
			AccelerationStructure.Dispose();
		}

		if (MetalDescriptor.NativePtr != IntPtr.Zero)
		{
			MetalDescriptor.Dispose();
		}
	}
}

internal sealed class MetalTopLevelAccelerationStructure : IGfxTopLevelAccelerationStructure, IDisposable
{
	public MTLInstanceAccelerationStructureDescriptor MetalDescriptor;
	public NSArray InstancedAccelerationStructures;
	public readonly List<MTLAccelerationStructure> ReferencedBottomLevelAccelerationStructures = new();

	public MetalTopLevelAccelerationStructure(
		string name,
		in TopLevelAccelerationStructureDescriptor descriptor,
		MTLInstanceAccelerationStructureDescriptor metalDescriptor,
		MTLAccelerationStructure accelerationStructure,
		MTLBuffer instanceDescriptorBuffer,
		MTLBuffer scratchBuffer,
		MTLFence buildFence)
	{
		Name = name;
		Descriptor = descriptor;
		MetalDescriptor = metalDescriptor;
		AccelerationStructure = accelerationStructure;
		InstanceDescriptorBuffer = instanceDescriptorBuffer;
		ScratchBuffer = scratchBuffer;
		BuildFence = buildFence;
	}

	public string Name { get; }

	public TopLevelAccelerationStructureDescriptor Descriptor { get; }

	public MTLAccelerationStructure AccelerationStructure { get; }

	public MTLBuffer InstanceDescriptorBuffer { get; }

	public MTLBuffer ScratchBuffer { get; }

	public MTLFence BuildFence { get; }

	public void Dispose()
	{
		if (BuildFence.NativePtr != IntPtr.Zero)
		{
			BuildFence.Dispose();
		}

		if (ScratchBuffer.NativePtr != IntPtr.Zero)
		{
			ScratchBuffer.Dispose();
		}

		if (InstanceDescriptorBuffer.NativePtr != IntPtr.Zero)
		{
			InstanceDescriptorBuffer.Dispose();
		}

		if (InstancedAccelerationStructures.NativePtr != IntPtr.Zero)
		{
			InstancedAccelerationStructures.Dispose();
		}

		if (AccelerationStructure.NativePtr != IntPtr.Zero)
		{
			AccelerationStructure.Dispose();
		}

		if (MetalDescriptor.NativePtr != IntPtr.Zero)
		{
			MetalDescriptor.Dispose();
		}
	}
}
