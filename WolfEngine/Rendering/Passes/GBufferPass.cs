#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Mathematics;
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

		Span<Vector4> frustumPlanes = stackalloc Vector4[6];
		ExtractFrustumPlanes(sceneData.ViewProjection, frustumPlanes);

		// Draw all meshes
		for (var i = 0; i < sceneData.DrawPackets.Count; i++)
		{
			var drawPacket = sceneData.DrawPackets[i];
			var mesh = drawPacket.Mesh;
			var material = drawPacket.Material;

			// Skip if mesh resources aren't loaded yet
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

			// Bind pipeline
			commandList.BindPipeline(material.Resources.Pipeline);

			// Bind constant buffer (material color)
			if (material.Resources.ConstantBuffer != null)
			{
				commandList.BindConstantBuffer(0, material.Resources.ConstantBuffer, 0);
			}

			// Bind albedo texture descriptor set
			Span<uint> materialHandles = stackalloc uint[8];
			materialHandles[0] = material.Resources.AlbedoTexture.Value;
			materialHandles[1] = material.Resources.MetallicRoughnessTexture.Value;
			materialHandles[2] = material.Resources.NormalTexture.Value;
			materialHandles[3] = material.Resources.OcclusionTexture.Value;
			materialHandles[4] = material.Resources.EmissiveTexture.Value;
			materialHandles[5] = material.Resources.Sampler.Value;
			materialHandles[6] = 0;
			materialHandles[7] = 0;
			commandList.SetGraphicsConstants(3, MemoryMarshal.AsBytes(materialHandles));

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
