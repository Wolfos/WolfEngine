#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class ShadowMapPass
{
	public const int ShadowMapResolution = 2048;
	private const float MaxShadowDistance = 50.0f;
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

		Span<Vector3> corners = stackalloc Vector3[8];
		var cornerIndex = 0;
		for (var z = 0; z <= 1; z++)
		{
			for (var y = -1; y <= 1; y += 2)
			{
				for (var x = -1; x <= 1; x += 2)
				{
					var clip = new Vector4(x, y, z, 1.0f);
					var world = Vector4.Transform(clip, sceneData.InverseViewProjection);
					corners[cornerIndex++] = world.W != 0.0f
						? world.XYZ() / world.W
						: Vector3.Zero;
				}
			}
		}

		for (var i = 0; i < 4; i++)
		{
			var nearCorner = corners[i];
			var farCorner = corners[i + 4];
			var ray = farCorner - nearCorner;
			var length = ray.Length();
			if (length <= MaxShadowDistance || length <= float.Epsilon)
			{
				continue;
			}

			corners[i + 4] = nearCorner + (ray / length) * MaxShadowDistance;
		}

		var frustumCenter = Vector3.Zero;
		for (var i = 0; i < corners.Length; i++)
		{
			frustumCenter += corners[i];
		}
		frustumCenter /= corners.Length;

		var radius = 1.0f;
		for (var i = 0; i < corners.Length; i++)
		{
			var distance = Vector3.Distance(corners[i], frustumCenter);
			if (distance > radius)
			{
				radius = distance;
			}
		}

		var up = Math.Abs(Vector3.Dot(lightDirection, Vector3.UnitY)) > 0.99f
			? Vector3.UnitZ
			: Vector3.UnitY;
		var eye = frustumCenter - (lightDirection * (radius + 64.0f));
		var lightView = Matrix4x4.CreateLookAtLeftHanded(eye, frustumCenter, up);

		var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
		var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
		for (var i = 0; i < corners.Length; i++)
		{
			var cornerLs = Vector3.Transform(corners[i], lightView);
			min = Vector3.Min(min, cornerLs);
			max = Vector3.Max(max, cornerLs);
		}

		// Expand XY for off-frustum casters that still project into the visible receiver area.
		var halfWidth = ((max.X - min.X) * 0.5f) + ReceiverPadding + CasterPaddingXY;
		var halfHeight = ((max.Y - min.Y) * 0.5f) + ReceiverPadding + CasterPaddingXY;
		var centerLs = (min + max) * 0.5f;

		// Stabilize shadow projection in light space to reduce angle-dependent shimmering/popping.
		var extentWidth = MathF.Max(halfWidth * 2.0f, 1e-3f);
		var extentHeight = MathF.Max(halfHeight * 2.0f, 1e-3f);
		var texelSizeX = extentWidth / ShadowMapResolution;
		var texelSizeY = extentHeight / ShadowMapResolution;
		if (texelSizeX > 0.0f)
		{
			centerLs.X = MathF.Floor(centerLs.X / texelSizeX) * texelSizeX;
		}
		if (texelSizeY > 0.0f)
		{
			centerLs.Y = MathF.Floor(centerLs.Y / texelSizeY) * texelSizeY;
		}

		var minX = centerLs.X - halfWidth;
		var maxX = centerLs.X + halfWidth;
		var minY = centerLs.Y - halfHeight;
		var maxY = centerLs.Y + halfHeight;

		var nearPlane = MathF.Max(0.1f, min.Z - CasterPaddingNear);
		var farPlane = MathF.Max(nearPlane + 1.0f, max.Z + CasterPaddingFar);
		var lightProjection = Matrix4x4.CreateOrthographicOffCenterLeftHanded(
			minX,
			maxX,
			minY,
			maxY,
			nearPlane,
			farPlane);

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
