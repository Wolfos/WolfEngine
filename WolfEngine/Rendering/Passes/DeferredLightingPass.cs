#nullable enable

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic deferred lighting compute pass that shades the G-buffer into the output texture.
/// </summary>
public static class DeferredLightingPass
{
	public static void Record(RenderGraphContext context, DeferredLightingPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(config);

		var commandList = context.CommandList;

		// D3D12-specific: Set descriptor heaps before binding any descriptors
		if (commandList is Backend.D3D12.D3D12CommandList d3d12Cmd && config.D3D12DescriptorHeap.HasValue)
		{
			var heaps = new[] { config.D3D12DescriptorHeap.Value };
			d3d12Cmd.SetDescriptorHeaps(heaps);
		}

		// Bind pipeline
		commandList.BindPipeline(config.Pipeline);

		// D3D12-specific: Bind descriptor tables for SRVs and UAVs
		if (commandList is Backend.D3D12.D3D12CommandList d3d12CmdList && config.D3D12GpuDescriptorHandle.HasValue)
		{
			// Root parameter 0: SRV descriptor table (3 GBuffer textures)
			d3d12CmdList.SetComputeRootDescriptorTable(0, config.D3D12GpuDescriptorHandle.Value);
			
			// Root parameter 1: UAV descriptor table (1 output texture)
			// UAV is at offset 3 in the heap (after the 3 SRVs)
			var uavHandle = config.D3D12GpuDescriptorHandle.Value;
			uavHandle.Ptr += 3 * config.D3D12DescriptorSize;
			d3d12CmdList.SetComputeRootDescriptorTable(1, uavHandle);
		}

		// Bind descriptor table if provided (for future bindless abstraction)
		if (config.DescriptorTable != null)
		{
			commandList.SetBindlessTable(config.DescriptorTable);
		}

		// Set camera constants (Root parameter 2)
		Span<float> cameraConstants = stackalloc float[20];
		WriteMatrix(cameraConstants, sceneData.ViewProjection);
		cameraConstants[16] = sceneData.CameraPosition.X;
		cameraConstants[17] = sceneData.CameraPosition.Y;
		cameraConstants[18] = sceneData.CameraPosition.Z;
		cameraConstants[19] = 1.0f;
		var cameraBytes = MemoryMarshal.AsBytes(cameraConstants);
		commandList.SetComputeConstants(2, cameraBytes);

		// Dispatch the compute shader
		var dispatchX = (uint)((config.DispatchSize.X + 7) / 8);
		var dispatchY = (uint)((config.DispatchSize.Y + 7) / 8);
		commandList.Dispatch(dispatchX, dispatchY, 1);
	}

	private static void WriteMatrix(Span<float> destination, Matrix4x4 matrix)
	{
		if (destination.Length < 16)
		{
			throw new ArgumentException("Destination span must contain at least 16 elements.", nameof(destination));
		}

		destination[0] = matrix.M11;
		destination[1] = matrix.M12;
		destination[2] = matrix.M13;
		destination[3] = matrix.M14;
		destination[4] = matrix.M21;
		destination[5] = matrix.M22;
		destination[6] = matrix.M23;
		destination[7] = matrix.M24;
		destination[8] = matrix.M31;
		destination[9] = matrix.M32;
		destination[10] = matrix.M33;
		destination[11] = matrix.M34;
		destination[12] = matrix.M41;
		destination[13] = matrix.M42;
		destination[14] = matrix.M43;
		destination[15] = matrix.M44;
	}
}

