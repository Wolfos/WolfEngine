#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic deferred lighting compute pass that shades the G-buffer into the output texture.
/// </summary>
public static class DeferredLightingPass
{
	public static DeferredLightingPassConfig BuildDeferredLightingConfig(RenderGraphContext context, RenderGraphFrameResources resources, IRenderer renderer)
	{
		var pipeline = renderer.GetDeferredLightingPipeline();

		var device = renderer.GetGfxDevice();

		var srvTableBuilder = device.CreateDescriptorSetBuilder();
		srvTableBuilder.AddShaderResource(0, context.GetTexture(resources.GBufferAlbedo));
		srvTableBuilder.AddShaderResource(1, context.GetTexture(resources.GBufferNormal));
		srvTableBuilder.AddShaderResource(2, context.GetTexture(resources.GBufferMaterial));
		srvTableBuilder.AddShaderResource(3, context.GetTexture(resources.GBufferDepth));
		var srvTable = srvTableBuilder.Build();

		var uavTableBuilder = device.CreateDescriptorSetBuilder();
		uavTableBuilder.AddUnorderedAccess(0, context.GetTexture(resources.LightingBuffer));
		var uavTable = uavTableBuilder.Build();

		return new DeferredLightingPassConfig
		{
			Pipeline = pipeline,
			SrvTable = srvTable,
			UavTable = uavTable,
			DispatchSize = resources.FramebufferSize
		};
	}

	public static unsafe void Record(RenderGraphContext context, ref DeferredLightingPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;

		// Bind pipeline
		commandList.BindPipeline(config.Pipeline);

		// Bind descriptor sets for SRVs and UAVs
		commandList.BindComputeDescriptorSet(0, config.SrvTable);
		commandList.BindComputeDescriptorSet(1, config.UavTable);

		// Set camera constants (Root parameter 2)
		Span<float> cameraConstants = stackalloc float[20];
		WriteMatrix(cameraConstants, sceneData.InverseProjection);
		cameraConstants[16] = sceneData.CameraOrigin.X;
		cameraConstants[17] = sceneData.CameraOrigin.Y;
		cameraConstants[18] = sceneData.CameraOrigin.Z;
		cameraConstants[19] = 1.0f;
		commandList.SetComputeConstants(2, MemoryMarshal.AsBytes(cameraConstants));
		
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
