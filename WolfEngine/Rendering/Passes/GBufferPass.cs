#nullable enable

using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Platform;
using SharpMetal.Metal;
using WolfEngine.Rendering.Backend.Metal;

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

		// Build camera constants once
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
		var cameraBytes = MemoryMarshal.AsBytes(cameraConstants);
		MetalBuffer? cameraBuffer = null;
		if (config.CameraBuffer is MetalBuffer metalCameraBuffer)
		{
			var cameraArray = cameraConstants.ToArray();
			BufferHelper.CopyToBuffer(cameraArray, metalCameraBuffer.Buffer);
			cameraBuffer = metalCameraBuffer;
		}

		Span<Vector4> frustumPlanes = stackalloc Vector4[6];
		ExtractFrustumPlanes(sceneData.ViewProjection, frustumPlanes);

		// Bind camera constants once
		commandList.SetGraphicsConstants(2, cameraBytes);

		// Bind GPU draw tables once
		if (config.InstanceBuffer is not null)
		{
			commandList.BindConstantBuffer(10, config.InstanceBuffer);
		}

		if (config.MaterialBuffer is not null)
		{
			commandList.BindConstantBuffer(11, config.MaterialBuffer);
		}

		if (commandList is MetalCommandList metalCommandList &&
		    config.GfxDevice is MetalDevice metalDevice)
		{
			var icb = metalDevice.GetOrCreateIndirectCommandBuffer((uint)sceneData.DrawPackets.Count);
			uint commandCount = 0;
			icb.Reset((uint)sceneData.DrawPackets.Count);
			var instanceStride = (ulong)Marshal.SizeOf<GpuInstanceData>();
			var usedResources = new HashSet<IntPtr>();
			void UseIfNeeded(MTLBuffer buffer)
			{
				if (buffer.NativePtr == IntPtr.Zero)
				{
					return;
				}

				if (usedResources.Add(buffer.NativePtr))
				{
					metalCommandList.UseResource(buffer, MTLResourceUsage.Read);
				}
			}

			if (config.InstanceBuffer is MetalBuffer sharedInstanceBuffer)
			{
				UseIfNeeded(sharedInstanceBuffer.Buffer);
			}

			if (config.MaterialBuffer is MetalBuffer sharedMaterialBuffer)
			{
				UseIfNeeded(sharedMaterialBuffer.Buffer);
			}

			if (cameraBuffer is not null)
			{
				UseIfNeeded(cameraBuffer.Buffer);
			}

			metalCommandList.UseBindlessArgumentBuffers();

			for (var i = 0; i < sceneData.DrawPackets.Count; i++)
			{
				var drawPacket = sceneData.DrawPackets[i];
				var mesh = drawPacket.Mesh;
				var material = drawPacket.Material;

				if (mesh.VertexBuffer == null || mesh.IndexBuffer == null || material.Resources == null)
				{
					continue;
				}

				var bounds = mesh.BoundingSphere;
				var boundsCenter = Vector3.Transform(bounds.Center, drawPacket.Transform);
				var maxScale = GetMaxScale(drawPacket.Transform);
				var boundsRadius = bounds.Radius * maxScale;
				if (IsSphereVisible(boundsCenter, boundsRadius, frustumPlanes) == false)
				{
					continue;
				}

				if (material.Resources.Pipeline is not MetalPipeline metalPipeline)
				{
					continue;
				}

				var vertexBuffer = mesh.VertexBuffer as MetalBuffer;
				var indexBuffer = mesh.IndexBuffer as MetalBuffer;
				if (vertexBuffer is null || indexBuffer is null)
				{
					continue;
				}

				UseIfNeeded(vertexBuffer.Buffer);
				UseIfNeeded(indexBuffer.Buffer);

				var command = icb.GetRenderCommand(commandCount);
				command.SetRenderPipelineState(metalPipeline.RenderPipelineState);
				metalCommandList.BindBindlessArgumentBuffers(command);
				command.SetVertexBuffer(vertexBuffer.Buffer, 0, mesh.StrideInBytes, 0);
				if (config.InstanceBuffer is MetalBuffer instanceBuffer)
				{
					var instanceOffset = (ulong)drawPacket.InstanceId * instanceStride;
					command.SetVertexBuffer(instanceBuffer.Buffer, instanceOffset, 10);
					command.SetFragmentBuffer(instanceBuffer.Buffer, instanceOffset, 10);
				}
				if (config.MaterialBuffer is MetalBuffer materialBuffer)
				{
					command.SetVertexBuffer(materialBuffer.Buffer, 0, 11);
					command.SetFragmentBuffer(materialBuffer.Buffer, 0, 11);
				}
				if (cameraBuffer is not null)
				{
					command.SetVertexBuffer(cameraBuffer.Buffer, 0, 2);
					command.SetFragmentBuffer(cameraBuffer.Buffer, 0, 2);
				}
				command.DrawIndexedPrimitives(
					MTLPrimitiveType.Triangle,
					mesh.IndexCount,
					MTLIndexType.UInt32,
					indexBuffer.Buffer,
					0,
					1,
					0,
					0);

				commandCount++;
			}

			commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);
			metalCommandList.ExecuteIndirect(icb, commandCount);
			commandList.EndPass();
			return;
		}

		// Fallback for non-Metal backends
		for (var i = 0; i < sceneData.DrawPackets.Count; i++)
		{
			var drawPacket = sceneData.DrawPackets[i];
			var mesh = drawPacket.Mesh;
			var material = drawPacket.Material;

			if (mesh.VertexBuffer == null || mesh.IndexBuffer == null || material.Resources == null)
			{
				continue;
			}

			commandList.BindPipeline(material.Resources.Pipeline);
			commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);

			var vertexViews = new[]
			{
				new VertexBufferView(mesh.VertexBuffer, mesh.StrideInBytes, 0)
			};
			commandList.SetVertexBuffers(vertexViews);

			var indexView = new IndexBufferView(mesh.IndexBuffer, IndexFormat.UInt32, 0);
			commandList.SetIndexBuffer(indexView);

			commandList.Draw(new DrawArguments(mesh.IndexCount, 1, 0, 0, (uint)drawPacket.InstanceId));
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

	private static void ExtractFrustumPlanes(Matrix4x4 viewProjection, Span<Vector4> planes)
	{
		if (planes.Length < 6)
		{
			throw new ArgumentException("Plane span must contain at least 6 elements.", nameof(planes));
		}

		var col1 = new Vector4(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
		var col2 = new Vector4(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
		var col3 = new Vector4(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
		var col4 = new Vector4(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

		planes[0] = NormalizePlane(col4 + col1); // Left
		planes[1] = NormalizePlane(col4 - col1); // Right
		planes[2] = NormalizePlane(col4 + col2); // Bottom
		planes[3] = NormalizePlane(col4 - col2); // Top
		planes[4] = NormalizePlane(col3);        // Near (LH, 0..1 depth)
		planes[5] = NormalizePlane(col4 - col3); // Far
	}

	private static Vector4 NormalizePlane(Vector4 plane)
	{
		var normal = new Vector3(plane.X, plane.Y, plane.Z);
		var length = normal.Length();
		if (length <= 0.0f)
		{
			return plane;
		}

		var invLength = 1.0f / length;
		return plane * invLength;
	}

	private static bool IsSphereVisible(Vector3 center, float radius, ReadOnlySpan<Vector4> planes)
	{
		for (var i = 0; i < planes.Length; i++)
		{
			var plane = planes[i];
			var distance = plane.X * center.X + plane.Y * center.Y + plane.Z * center.Z + plane.W;
			if (distance < -radius)
			{
				return false;
			}
		}

		return true;
	}

	private static float GetMaxScale(Matrix4x4 matrix)
	{
		var scaleX = new Vector3(matrix.M11, matrix.M12, matrix.M13).Length();
		var scaleY = new Vector3(matrix.M21, matrix.M22, matrix.M23).Length();
		var scaleZ = new Vector3(matrix.M31, matrix.M32, matrix.M33).Length();
		return MathF.Max(scaleX, MathF.Max(scaleY, scaleZ));
	}
}
