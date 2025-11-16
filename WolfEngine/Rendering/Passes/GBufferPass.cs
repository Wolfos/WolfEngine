#nullable enable

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public static class GBufferPass
{
	public static PassTargets CreatePassTargets(GBufferPassConfig config)
	{
		if (config is null)
		{
			throw new ArgumentNullException(nameof(config));
		}

        var colorBindings = new[]
        {
            new ColorTargetBinding(config.AlbedoTarget),
            new ColorTargetBinding(config.NormalTarget),
            new ColorTargetBinding(config.MaterialTarget)
        };

        var depthBinding = new DepthTargetBinding(config.DepthTarget);

        return new PassTargets(colorBindings, depthBinding);
	}

    public static Viewport CreateViewport(GBufferPassConfig config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        return new Viewport(0.0f, 0.0f, config.FramebufferWidth, config.FramebufferHeight);
    }

	public static void Record(RenderGraphContext context, GBufferPassConfig config, SceneDrawData sceneData)
	{
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);

		var commandList = context.CommandList;
        var targets = CreatePassTargets(config);
		var viewport = CreateViewport(config);
		commandList.BeginPass(targets, viewport);
		commandList.SetScissorRect(new RectInt(0, 0, config.FramebufferWidth, config.FramebufferHeight));
		commandList.ClearColorAttachment(0, config.AlbedoClearColor);
		commandList.ClearColorAttachment(1, config.NormalClearColor);
		commandList.ClearColorAttachment(2, config.MaterialClearColor);
		commandList.ClearDepthStencil(config.DepthClearValue);

		// Build camera constants once
		Span<float> cameraConstants = stackalloc float[20];
		WriteMatrix(cameraConstants, sceneData.ViewProjection);
		cameraConstants[16] = sceneData.CameraPosition.X;
		cameraConstants[17] = sceneData.CameraPosition.Y;
		cameraConstants[18] = sceneData.CameraPosition.Z;
		cameraConstants[19] = 1.0f;
		var cameraBytes = MemoryMarshal.AsBytes(cameraConstants);

		// Draw all meshes
		foreach (var drawPacket in sceneData.DrawPackets)
		{
			var mesh = drawPacket.Mesh;
			var material = drawPacket.Material;

			// Skip if mesh resources aren't loaded yet
			if (mesh.VertexBuffer == null || mesh.IndexBuffer == null || material.Resources == null)
			{
				continue;
			}

			// Bind pipeline
			commandList.BindPipeline(material.Resources.Pipeline);

			// Bind constant buffer (material color)
			if (material.Resources.ConstantBuffer != null)
			{
				commandList.BindConstantBuffer(0, material.Resources.ConstantBuffer, 0);
			}

			// Set model matrix constants
			Span<float> modelConstants = stackalloc float[16];
			WriteMatrix(modelConstants, drawPacket.Transform);
			var modelBytes = MemoryMarshal.AsBytes(modelConstants);
			commandList.SetGraphicsConstants(1, modelBytes);

			// Set camera constants
			commandList.SetGraphicsConstants(2, cameraBytes);

			// Set topology and buffers
			commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);

			var vertexViews = new[] 
			{ 
				new VertexBufferView(mesh.VertexBuffer, mesh.StrideInBytes, 0) 
			};
			commandList.SetVertexBuffers(vertexViews);

			var indexView = new IndexBufferView(mesh.IndexBuffer, IndexFormat.UInt32, 0);
			commandList.SetIndexBuffer(indexView);

			// Draw
			commandList.Draw(new DrawArguments(mesh.IndexCount, 1));
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
