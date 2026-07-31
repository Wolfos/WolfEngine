#nullable enable

using System;
using System.Numerics;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public static class GBufferPass
{
	public static PassTargets CreatePassTargets(GBufferPassConfig config)
	{
		var colorBindings = new[]
		{
			new ColorTargetBinding(config.AlbedoTarget),
			new ColorTargetBinding(config.NormalTarget),
			new ColorTargetBinding(config.MaterialTarget),
			new ColorTargetBinding(config.EmissiveTarget),
			new ColorTargetBinding(config.VelocityTarget)
		};

		var depthBinding = new DepthTargetBinding(config.DepthTarget);

		return new PassTargets(colorBindings, depthBinding);
	}

	public static Viewport CreateViewport(GBufferPassConfig config)
	{
		return new Viewport(0.0f, 0.0f, config.FramebufferWidth, config.FramebufferHeight);
	}

	public static void Record(RenderGraphContext context, GBufferPassConfig config, SceneDrawData sceneData)
	{
		var commandList = context.CommandList;
		var targets = CreatePassTargets(config);
		var viewport = CreateViewport(config);
		commandList.BeginPass(targets, viewport);
		commandList.ClearColorAttachment(0, config.AlbedoClearColor);
		commandList.ClearColorAttachment(1, config.NormalClearColor);
		commandList.ClearColorAttachment(2, config.MaterialClearColor);
		commandList.ClearColorAttachment(3, config.EmissiveClearColor);
		commandList.ClearColorAttachment(4, config.VelocityClearColor);
		commandList.ClearDepthStencil(config.DepthClearValue);
		commandList.SetScissorRect(new RectInt(0, 0, config.FramebufferWidth, config.FramebufferHeight));

		var cameraWriter = new ShaderPropertyWriter(config.CameraLayout);
		cameraWriter.Clear();
		cameraWriter.SetMatrix4x4("viewProjection", sceneData.ViewProjection);
		cameraWriter.SetVector3("cameraPosition", sceneData.CameraOrigin);
		cameraWriter.SetVector3("previousCameraPosition", sceneData.PreviousCameraOrigin);
		cameraWriter.SetFloat("currentJitterPixelsX", sceneData.JitterPixels.X);
		cameraWriter.SetFloat("currentJitterPixelsY", sceneData.JitterPixels.Y);
		cameraWriter.SetMatrix4x4("unjitteredViewProjection", sceneData.UnjitteredViewProjection);
		cameraWriter.SetMatrix4x4("previousViewProjection", sceneData.PreviousViewProjection);
		cameraWriter.SetVector2("currentJitterNdc", sceneData.JitterNdc);
		cameraWriter.SetUInt("frameSizeX", (uint)Math.Max(config.FramebufferWidth, 1));
		cameraWriter.SetUInt("frameSizeY", (uint)Math.Max(config.FramebufferHeight, 1));
		cameraWriter.SetMatrix4x4("viewMatrix", sceneData.ViewMatrix);
		cameraWriter.SetFloat("nearPlane", sceneData.NearPlane);
		cameraWriter.SetFloat("farPlane", sceneData.FarPlane);
		UploadCameraConstants(config, commandList, cameraWriter);

		if (config.InstanceBuffer is null ||
		    config.MaterialBuffer is null ||
		    config.DrawArgsBuffer is null ||
		    config.CameraBuffer is null)
		{
			commandList.EndPass();
			return;
		}

		var buckets = config.Buckets.Span;
		if (buckets.Length == 0)
		{
			commandList.EndPass();
			return;
		}
		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);
		SharedDrawIndirectExecution.BindPackedGeometry(
			commandList,
			config.PackedVertexBuffer,
			config.PackedIndexBuffer,
			config.PackedVertexStride);

		for (var i = 0; i < buckets.Length; i++)
		{
			var bucket = buckets[i];
			using (FrameProfiler.Instance.Measure(bucket.DebugName))
			{
				commandList.BindPipeline(bucket.Pipeline);
				BindPassBindings(commandList, bucket.PassBindings);
				commandList.BindConstantBuffer(bucket.BufferBindings.InstanceRegisterIndex, config.InstanceBuffer);
				commandList.BindConstantBuffer(bucket.BufferBindings.MaterialRegisterIndex, config.MaterialBuffer);
				commandList.BindConstantBuffer(bucket.BufferBindings.DrawArgsRegisterIndex, config.DrawArgsBuffer);
				if (config.MaterialGenerationBuffer is not null)
				{
					commandList.BindConstantBuffer(bucket.BufferBindings.MaterialGenerationRegisterIndex, config.MaterialGenerationBuffer);
				}
				if (config.CompactedCommandCountBuffer is { } countBuffer)
				{
					SharedDrawIndirectExecution.ExecuteCompactedPages(
						commandList,
						bucket.IndirectCommandPages.Span,
						countBuffer,
						config.IndirectCommandSlot,
						bucket.ExecutionIndex,
						config.FallbackMaxCommandCount);
				}
				else
				{
					SharedDrawIndirectExecution.ExecutePages(
						commandList,
						bucket.IndirectCommandPages.Span,
						config.FallbackMaxCommandCount);
				}
			}
		}

		commandList.EndPass();
	}

	private static void BindPassBindings(IGfxCommandList commandList, GraphicsPassBindingSet passBindings)
	{
		foreach (var binding in passBindings.Bindings)
		{
			commandList.BindConstantBuffer(binding.RegisterIndex, binding.Resource);
		}
	}

	private static void UploadCameraConstants(
		in GBufferPassConfig config,
		IGfxCommandList commandList,
		ShaderPropertyWriter cameraWriter)
	{
		if (config.CameraBuffer is IWritableGpuBuffer writableCameraBuffer)
		{
			writableCameraBuffer.Write<byte>(cameraWriter.AsBytes());
			return;
		}

		commandList.SetGraphicsConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());
	}
}
