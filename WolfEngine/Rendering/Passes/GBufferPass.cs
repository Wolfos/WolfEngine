#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;
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
			new ColorTargetBinding(config.EmissiveTarget)
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
		commandList.ClearDepthStencil(config.DepthClearValue);
		commandList.SetScissorRect(new RectInt(0, 0, config.FramebufferWidth, config.FramebufferHeight));

		Span<float> cameraConstants = stackalloc float[24];
		WriteMatrix(cameraConstants, sceneData.ViewProjection);
		cameraConstants[16] = sceneData.CameraOrigin.X;
		cameraConstants[17] = sceneData.CameraOrigin.Y;
		cameraConstants[18] = sceneData.CameraOrigin.Z;
		cameraConstants[19] = 1.0f;
		cameraConstants[20] = 0.0f;
		cameraConstants[21] = 0.0f;
		cameraConstants[22] = 0.0f;
		cameraConstants[23] = 0.0f;
		commandList.SetGraphicsConstants(2, MemoryMarshal.AsBytes(cameraConstants));

		if (config.InstanceBuffer is not null)
		{
			commandList.BindConstantBuffer(10, config.InstanceBuffer);
		}

		if (config.MaterialBuffer is not null)
		{
			commandList.BindConstantBuffer(11, config.MaterialBuffer);
		}

		if (config.DrawArgsBuffer is null)
		{
			throw new InvalidOperationException("GBuffer pass requires DrawArgsBuffer for GPU-driven rendering.");
		}

		var drawArgsBuffer = config.DrawArgsBuffer;
		var commandCount = 0u;
		var totalPackets = sceneData.DrawPackets.Count;
		var skippedNoMesh = 0;
		var skippedNoMaterial = 0;
		var skippedNoPipeline = 0;
		var skippedOutOfRange = 0;

		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);

		var pipelineBuckets = new Dictionary<IGfxPipeline, List<int>>();
		for (var i = 0; i < sceneData.DrawPackets.Count; i++)
		{
			var drawPacket = sceneData.DrawPackets[i];
			var mesh = drawPacket.Mesh;
			var material = drawPacket.Material;

			if (drawPacket.DrawId <= 0 ||
			    drawPacket.DrawId >= global::WolfEngine.Rendering.GpuDrawResources.MaxDrawCount ||
			    drawPacket.InstanceId <= 0 ||
			    drawPacket.InstanceId >= global::WolfEngine.Rendering.GpuDrawResources.MaxInstanceCount)
			{
				skippedOutOfRange++;
				continue;
			}

			if (mesh.VertexBuffer is null || mesh.IndexBuffer is null)
			{
				skippedNoMesh++;
				continue;
			}

			if (material.Resources is null)
			{
				skippedNoMaterial++;
				continue;
			}

			if (material.Resources.Pipeline is null)
			{
				skippedNoPipeline++;
				continue;
			}

			if (pipelineBuckets.TryGetValue(material.Resources.Pipeline, out var bucket) == false)
			{
				bucket = new List<int>();
				pipelineBuckets.Add(material.Resources.Pipeline, bucket);
			}

			bucket.Add(i);
		}

		foreach (var (pipeline, bucket) in pipelineBuckets)
		{
			commandList.BindPipeline(pipeline);

			for (var j = 0; j < bucket.Count; j++)
			{
				var drawPacket = sceneData.DrawPackets[bucket[j]];
				var mesh = drawPacket.Mesh;
				if (mesh.VertexBuffer is null || mesh.IndexBuffer is null)
				{
					skippedNoMesh++;
					continue;
				}

				commandList.SetVertexBuffers(new[] { new VertexBufferView(mesh.VertexBuffer, mesh.StrideInBytes, 0) });
				var drawArgsOffset = (ulong)drawPacket.DrawId * (ulong)Marshal.SizeOf<GpuDrawArgs>();
				commandList.DrawIndexedIndirect(
					new IndexBufferView(mesh.IndexBuffer, IndexFormat.UInt32, 0),
					drawArgsBuffer,
					drawArgsOffset);
				commandCount++;
			}
		}

		if ((totalPackets > 0) && (commandCount != totalPackets))
		{
			Console.WriteLine($"GBuffer: packets={totalPackets}, draws={commandCount}, " +
			                  $"skippedNoMesh={skippedNoMesh}, skippedNoMaterial={skippedNoMaterial}, " +
			                  $"skippedNoPipeline={skippedNoPipeline}, skippedOutOfRange={skippedOutOfRange}, gpuArgs=1");
		}

		commandList.EndPass();
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
