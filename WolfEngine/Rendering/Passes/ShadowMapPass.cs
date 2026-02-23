#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class ShadowMapPass
{
	public const int ShadowMapResolution = 2048;
	private const float MaxShadowDistance = 3.0f;
	private const float DefaultDepthBias = 0.0015f;
	private const float DefaultStrength = 1.0f;
	private const float ReceiverPadding = 12.0f;
	private const float CasterPaddingXY = 24.0f;
	private const float CasterPaddingNear = 96.0f;
	private const float CasterPaddingFar = 24.0f;

	private readonly IShaderCompiler _shaderCompiler;
	private readonly Dictionary<int, IGfxPipeline> _pipelinesByBucketIndex = new();
	private ShadowFrameData _currentFrameData = new(
		enabled: false,
		viewProjection: Matrix4x4.Identity,
		shadowedDirectionalLightIndex: -1,
		DefaultDepthBias,
		DefaultStrength,
		ShadowMapResolution);

	public ShadowMapPass(IShaderCompiler shaderCompiler)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
	}

	public void PrepareFrame(SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(sceneData);

		if (TryBuildShadowViewProjection(sceneData, out var viewProjection, out var shadowedLightIndex))
		{
			_currentFrameData = new ShadowFrameData(
				enabled: true,
				viewProjection: viewProjection,
				shadowedDirectionalLightIndex: shadowedLightIndex,
				DefaultDepthBias,
				DefaultStrength,
				ShadowMapResolution);
			return;
		}

		_currentFrameData = new ShadowFrameData(
			enabled: false,
			viewProjection: Matrix4x4.Identity,
			shadowedDirectionalLightIndex: -1,
			DefaultDepthBias,
			DefaultStrength,
			ShadowMapResolution);
	}

	public ShadowFrameData GetCurrentFrameData() => _currentFrameData;

	public ShadowMapPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		GpuDrawResources gpuDrawResources)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);

		var depth = context.GetTexture(resources.ShadowMapDepth);
		var bucketDefinitions = GBufferDrawBuckets.Definitions;
		var buckets = new List<ShadowMapExecutionBucket>(bucketDefinitions.Length);
		var activeIndirectSlot = gpuDrawResources.ActiveIndirectCommandSlot;
		for (var i = 0; i < bucketDefinitions.Length; i++)
		{
			var bucketDefinition = bucketDefinitions[i];
			if (bucketDefinition.SupportsPass(DrawPassParticipation.ShadowCaster) == false)
			{
				continue;
			}

			var indirectCommandBuffer = gpuDrawResources.GetIndirectCommandBufferSlot(activeIndirectSlot, i);
			if (indirectCommandBuffer is null)
			{
				continue;
			}

			var pipeline = EnsurePipeline(device, bucketDefinition, i);
			buckets.Add(new ShadowMapExecutionBucket(
				i,
				bucketDefinition.DebugName,
				pipeline,
				indirectCommandBuffer));
		}

		return new ShadowMapPassConfig
		{
			FramebufferWidth = depth.Descriptor.Width,
			FramebufferHeight = depth.Descriptor.Height,
			DepthTarget = depth,
			InstanceBuffer = gpuDrawResources.InstanceBuffer,
			MaterialBuffer = gpuDrawResources.MaterialBuffer,
			DrawArgsBuffer = gpuDrawResources.DrawArgsBuffer,
			CameraBuffer = gpuDrawResources.ShadowCameraBuffer,
			MaterialGenerationBuffer = gpuDrawResources.MaterialGenerationBuffer,
			VisibleDrawIdsPerBucketBuffer = gpuDrawResources.VisibleDrawIdsPerBucketBuffer,
			DrawExecutionRangePerBucketBuffer = gpuDrawResources.ShadowDrawExecutionRangePerBucketBuffer,
			Buckets = buckets.ToArray(),
			FallbackMaxCommandCount = gpuDrawResources.ActiveDrawCommandUpperBound
		};
	}

	public void Record(RenderGraphContext context, in ShadowMapPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		var targets = new PassTargets(
			Array.Empty<ColorTargetBinding>(),
			new DepthTargetBinding(config.DepthTarget));
		var viewport = new Viewport(0.0f, 0.0f, config.FramebufferWidth, config.FramebufferHeight);
		commandList.BeginPass(targets, viewport);
		commandList.ClearDepthStencil(1.0f);
		commandList.SetScissorRect(new RectInt(0, 0, config.FramebufferWidth, config.FramebufferHeight));

		if (_currentFrameData.Enabled == false ||
		    config.InstanceBuffer is null ||
		    config.MaterialBuffer is null ||
		    config.DrawArgsBuffer is null ||
		    config.CameraBuffer is null)
		{
			commandList.EndPass();
			return;
		}

		UploadCameraConstants(context, config, commandList);

		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);
		commandList.BindConstantBuffer(10, config.InstanceBuffer);
		commandList.BindConstantBuffer(11, config.MaterialBuffer);
		commandList.BindConstantBuffer(12, config.DrawArgsBuffer);
		if (config.MaterialGenerationBuffer is not null)
		{
			commandList.BindConstantBuffer(13, config.MaterialGenerationBuffer);
		}
		commandList.BindConstantBuffer(14, config.CameraBuffer);

		var buckets = config.Buckets.Span;
		if (buckets.Length == 0)
		{
			commandList.EndPass();
			return;
		}

		var fallbackCount = config.FallbackMaxCommandCount == 0
			? (uint)GpuDrawResources.MaxDrawCount
			: config.FallbackMaxCommandCount;
		if (commandList.BackendKind != GraphicsBackendKind.Metal)
		{
			var fallbackBucket = buckets[0];
			commandList.BindPipeline(fallbackBucket.Pipeline);
			commandList.ExecuteIndirectCommandBuffer(fallbackBucket.IndirectCommandBuffer, fallbackCount);
			commandList.EndPass();
			return;
		}

		for (var i = 0; i < buckets.Length; i++)
		{
			var bucket = buckets[i];
			using (FrameProfiler.Instance.Measure($"Shadow.{bucket.DebugName}"))
			{
				commandList.BindPipeline(bucket.Pipeline);
				if (config.VisibleDrawIdsPerBucketBuffer is not null &&
				    config.DrawExecutionRangePerBucketBuffer is not null)
				{
					var indicesOffsetBytes = (ulong)(bucket.BucketIndex * GpuDrawResources.MaxDrawCount * sizeof(uint));
					var rangeOffsetBytes = (ulong)(bucket.BucketIndex * 2 * sizeof(uint));
					commandList.ExecuteIndirectCommandBufferIndexed(
						bucket.IndirectCommandBuffer,
						config.VisibleDrawIdsPerBucketBuffer,
						indicesOffsetBytes,
						config.DrawExecutionRangePerBucketBuffer,
						rangeOffsetBytes);
				}
				else
				{
					commandList.ExecuteIndirectCommandBuffer(bucket.IndirectCommandBuffer, fallbackCount);
				}
			}
		}

		commandList.EndPass();
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device, in GBufferDrawBucketDefinition bucket, int bucketIndex)
	{
		if (_pipelinesByBucketIndex.TryGetValue(bucketIndex, out var pipeline))
		{
			return pipeline;
		}

		var renderState = new RenderStateDescriptor(
			FillMode.Solid,
			CullMode.Back,
			depthTestEnabled: true,
			depthWriteEnabled: true,
			BlendMode.Opaque);

		var key = new PipelineKey(
			PassKind.Graphics,
			vertexEntryPoint: "vertexShader",
			pixelEntryPoint: "fragmentShader",
			computeEntryPoint: null,
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.D32Float),
			renderState: renderState,
			layout: GraphicsLayoutKind.Material,
			shaderVariant: $"Shadow:{bucket.ShaderVariant}");

		var shaders = GraphicsShaderCompiler.Compile(
			_shaderCompiler,
			device.BackendKind,
			"shadow_map.slang",
			"vertexShader",
			"fragmentShader",
			bucket.PreprocessorDefine);
		pipeline = device.GetOrCreatePipeline(key, shaders);
		_pipelinesByBucketIndex[bucketIndex] = pipeline;
		return pipeline;
	}

	private void UploadCameraConstants(RenderGraphContext context, in ShadowMapPassConfig config, IGfxCommandList commandList)
	{
		Span<float> cameraConstants = stackalloc float[24];
		WriteMatrix(cameraConstants, _currentFrameData.ViewProjection);
		var sceneData = context.SceneData!;
		cameraConstants[16] = sceneData.CameraOrigin.X;
		cameraConstants[17] = sceneData.CameraOrigin.Y;
		cameraConstants[18] = sceneData.CameraOrigin.Z;
		cameraConstants[19] = 1.0f;
		cameraConstants[20] = 0.0f;
		cameraConstants[21] = 0.0f;
		cameraConstants[22] = 0.0f;
		cameraConstants[23] = 0.0f;

		if (config.CameraBuffer is IWritableGpuBuffer writableCameraBuffer)
		{
			writableCameraBuffer.Write<float>(cameraConstants);
			return;
		}

		commandList.SetGraphicsConstants(14, MemoryMarshal.AsBytes(cameraConstants));
	}

	private static bool TryBuildShadowViewProjection(SceneDrawData sceneData, out Matrix4x4 shadowViewProjection, out int shadowedLightIndex)
	{
		shadowViewProjection = Matrix4x4.Identity;
		shadowedLightIndex = -1;
		if (sceneData.Lights.Count == 0)
		{
			return false;
		}

		var lightDirection = Vector3.Zero;
		for (var i = 0; i < sceneData.Lights.Count; i++)
		{
			var packet = sceneData.Lights[i];
			if (packet.Light.Type != LightType.Directional)
			{
				continue;
			}

			var forward = Vector3.TransformNormal(Vector3.UnitZ, packet.Transform);
			if (forward == Vector3.Zero)
			{
				forward = new Vector3(0.0f, -1.0f, 0.0f);
			}

			lightDirection = Vector3.Normalize(forward);
			shadowedLightIndex = i;
			break;
		}

		if (shadowedLightIndex < 0)
		{
			return false;
		}

		// Keep the shadow volume anchored to the camera origin in camera-relative space.
		// This avoids rotation-driven recentering shimmer from per-frame frustum fitting.
		var frustumCenter = Vector3.Zero;
		var receiverRadius = MaxShadowDistance;

		var up = Math.Abs(Vector3.Dot(lightDirection, Vector3.UnitY)) > 0.99f
			? Vector3.UnitZ
			: Vector3.UnitY;
		var eye = frustumCenter - (lightDirection * (receiverRadius + 64.0f));
		var lightView = Matrix4x4.CreateLookAtLeftHanded(eye, frustumCenter, up);

		// Use a fixed XY extent from the frustum bounding sphere to keep texel scale constant.
		var halfExtent = receiverRadius + ReceiverPadding + CasterPaddingXY;
		var centerLs = Vector3.Transform(frustumCenter, lightView);

		// Snap the projection center to shadow texels for camera-motion stability.
		// The extra offset anchors snapping to world-space camera movement so translation
		// does not cause continuous sub-texel drift.
		var worldUnitsPerTexel = MathF.Max((halfExtent * 2.0f) / ShadowMapResolution, 1e-6f);
		var lightAxisX = new Vector3(lightView.M11, lightView.M21, lightView.M31);
		var lightAxisY = new Vector3(lightView.M12, lightView.M22, lightView.M32);
		var cameraLsX = Vector3.Dot(sceneData.CameraOrigin, lightAxisX);
		var cameraLsY = Vector3.Dot(sceneData.CameraOrigin, lightAxisY);
		var snappedCameraLsX = MathF.Round(cameraLsX / worldUnitsPerTexel, MidpointRounding.AwayFromZero) * worldUnitsPerTexel;
		var snappedCameraLsY = MathF.Round(cameraLsY / worldUnitsPerTexel, MidpointRounding.AwayFromZero) * worldUnitsPerTexel;
		centerLs.X += snappedCameraLsX - cameraLsX;
		centerLs.Y += snappedCameraLsY - cameraLsY;

		var minX = centerLs.X - halfExtent;
		var maxX = centerLs.X + halfExtent;
		var minY = centerLs.Y - halfExtent;
		var maxY = centerLs.Y + halfExtent;

		// Keep depth span stable to avoid temporal shimmer from per-frame depth re-normalization.
		var nearPlane = MathF.Max(0.1f, centerLs.Z - (receiverRadius + CasterPaddingNear));
		var depthSpan = MathF.Max((receiverRadius * 2.0f) + CasterPaddingNear + CasterPaddingFar, 1.0f);
		var farPlane = nearPlane + depthSpan;
		var lightProjection = Matrix4x4.CreateOrthographicOffCenterLeftHanded(
			minX,
			maxX,
			minY,
			maxY,
			nearPlane,
			farPlane);

		// Keep the shadow matrix in camera-relative space to preserve precision when the
		// world origin is far from zero. Inputs to this matrix are camera-relative positions.
		shadowViewProjection = lightView * lightProjection;
		return true;
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

file static class ShadowMapPassVectorExtensions
{
	public static Vector3 XYZ(this Vector4 vector) => new(vector.X, vector.Y, vector.Z);
}
