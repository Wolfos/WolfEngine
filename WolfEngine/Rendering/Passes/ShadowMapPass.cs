#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class ShadowMapPass
{
	public const int CascadeCount = 3;
	public const int CascadeResolution = 4096;
	public const float MaxShadowDistance = 150.0f;

	private const float DefaultDepthBias = 0.0015f;
	private const float DefaultStrength = 1.0f;
	private const float CasterPaddingNear = 96.0f;
	private const float CasterPaddingFar = 24.0f;
	private const float DefaultCascadeBlendDistance = 2.0f;
	private const int ShadowCameraConstantsFloatCount = (16 * CascadeCount) + 4;

	private static readonly List<float> CascadeSplitDistances =
	[
		6.0f,
		30.0f,
		MaxShadowDistance
	];

	private readonly IShaderCompiler _shaderCompiler;
	private readonly Dictionary<(int CascadeIndex, int BucketIndex), IGfxPipeline> _pipelinesByCascadeBucket = new();
	private ShadowFrameData _currentFrameData = CreateDisabledFrameData();

	public ShadowMapPass(IShaderCompiler shaderCompiler)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
	}

	public void PrepareFrame(SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(sceneData);

		if (TryBuildShadowCascades(sceneData, out var matrices, out var splits, out var shadowedLightIndex))
		{
			_currentFrameData = new ShadowFrameData(
				enabled: true,
				cascadeViewProjection0: matrices[0],
				cascadeViewProjection1: matrices[1],
				cascadeViewProjection2: matrices[2],
				cascadeSplit0: splits[0],
				cascadeSplit1: splits[1],
				cascadeSplit2: splits[2],
				cascadeBlendDistance: DefaultCascadeBlendDistance,
				shadowedDirectionalLightIndex: shadowedLightIndex,
				depthBias: DefaultDepthBias,
				strength: DefaultStrength,
				mapResolution: CascadeResolution);
			return;
		}

		_currentFrameData = CreateDisabledFrameData();
	}

	public ShadowFrameData GetCurrentFrameData() => _currentFrameData;

	public ShadowMapPassConfig BuildConfig(
		RenderGraphContext context,
		IGfxTexture depthTarget,
		IGfxDevice device,
		GpuDrawResources gpuDrawResources,
		int cascadeIndex)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(depthTarget);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);

		if (cascadeIndex < 0 || cascadeIndex >= CascadeCount)
		{
			throw new ArgumentOutOfRangeException(nameof(cascadeIndex), cascadeIndex, "Cascade index is out of range.");
		}

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

			var pipeline = EnsurePipeline(device, bucketDefinition, i, cascadeIndex);
			buckets.Add(new ShadowMapExecutionBucket(
				i,
				bucketDefinition.DebugName,
				pipeline,
				indirectCommandBuffer));
		}

		return new ShadowMapPassConfig
		{
			CascadeIndex = cascadeIndex,
			FramebufferWidth = depthTarget.Descriptor.Width,
			FramebufferHeight = depthTarget.Descriptor.Height,
			DepthTarget = depthTarget,
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
			using (FrameProfiler.Instance.Measure($"Shadow.C{config.CascadeIndex}.{bucket.DebugName}"))
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

	private IGfxPipeline EnsurePipeline(
		IGfxDevice device,
		in GBufferDrawBucketDefinition bucket,
		int bucketIndex,
		int cascadeIndex)
	{
		var pipelineKeyByCascadeBucket = (cascadeIndex, bucketIndex);
		if (_pipelinesByCascadeBucket.TryGetValue(pipelineKeyByCascadeBucket, out var pipeline))
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
			shaderVariant: $"Shadow:{bucket.ShaderVariant}:C{cascadeIndex}");

		var cascadeDefine = $"WOLF_SHADOW_CASCADE_INDEX={cascadeIndex}";
		var shaders = GraphicsShaderCompiler.Compile(
			_shaderCompiler,
			device.BackendKind,
			"shadow_map.slang",
			"vertexShader",
			"fragmentShader",
			bucket.PreprocessorDefine,
			cascadeDefine);
		pipeline = device.GetOrCreatePipeline(key, shaders);
		_pipelinesByCascadeBucket[pipelineKeyByCascadeBucket] = pipeline;
		return pipeline;
	}

	private void UploadCameraConstants(RenderGraphContext context, in ShadowMapPassConfig config, IGfxCommandList commandList)
	{
		Span<float> cameraConstants = stackalloc float[ShadowCameraConstantsFloatCount];
		WriteMatrix(cameraConstants, _currentFrameData.CascadeViewProjection0);
		WriteMatrix(cameraConstants.Slice(16), _currentFrameData.CascadeViewProjection1);
		WriteMatrix(cameraConstants.Slice(32), _currentFrameData.CascadeViewProjection2);
		var sceneData = context.SceneData!;
		cameraConstants[48] = sceneData.CameraOrigin.X;
		cameraConstants[49] = sceneData.CameraOrigin.Y;
		cameraConstants[50] = sceneData.CameraOrigin.Z;
		cameraConstants[51] = 0.0f;

		if (config.CameraBuffer is IWritableGpuBuffer writableCameraBuffer)
		{
			writableCameraBuffer.Write<float>(cameraConstants);
			return;
		}

		commandList.SetGraphicsConstants(14, MemoryMarshal.AsBytes(cameraConstants));
	}

	private static ShadowFrameData CreateDisabledFrameData() => new(
		enabled: false,
		cascadeViewProjection0: Matrix4x4.Identity,
		cascadeViewProjection1: Matrix4x4.Identity,
		cascadeViewProjection2: Matrix4x4.Identity,
		cascadeSplit0: CascadeSplitDistances[0],
		cascadeSplit1: CascadeSplitDistances[1],
		cascadeSplit2: CascadeSplitDistances[2],
		cascadeBlendDistance: DefaultCascadeBlendDistance,
		shadowedDirectionalLightIndex: -1,
		depthBias: DefaultDepthBias,
		strength: DefaultStrength,
		mapResolution: CascadeResolution);

	private static bool TryBuildShadowCascades(
		SceneDrawData sceneData,
		out Matrix4x4[] cascadeMatrices,
		out float[] cascadeSplits,
		out int shadowedLightIndex)
	{
		cascadeMatrices = new Matrix4x4[CascadeCount];
		cascadeSplits = new float[CascadeCount];
		shadowedLightIndex = -1;

		if (TryGetShadowedDirectionalLight(sceneData, out var lightDirection, out shadowedLightIndex) == false)
		{
			return false;
		}

		BuildConfiguredCascadeSplits(cascadeSplits);
		for (var i = 0; i < CascadeCount; i++)
		{
			cascadeMatrices[i] = BuildCascadeViewProjection(sceneData, lightDirection, cascadeSplits[i]);
		}

		return true;
	}

	private static bool TryGetShadowedDirectionalLight(
		SceneDrawData sceneData,
		out Vector3 lightDirection,
		out int shadowedLightIndex)
	{
		lightDirection = Vector3.Zero;
		shadowedLightIndex = -1;
		if (sceneData.Lights.Count == 0)
		{
			return false;
		}

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
			return true;
		}

		return false;
	}

	private static void BuildConfiguredCascadeSplits(Span<float> destination)
	{
		if (destination.Length < CascadeCount)
		{
			throw new ArgumentException("Destination span must contain all cascade splits.", nameof(destination));
		}

		if (CascadeSplitDistances.Count != CascadeCount)
		{
			throw new InvalidOperationException(
				$"Cascade split list must contain exactly {CascadeCount} elements.");
		}

		var previous = 0.01f;
		for (var i = 0; i < CascadeCount; i++)
		{
			var split = CascadeSplitDistances[i];
			destination[i] = Math.Clamp(split, previous, MaxShadowDistance);
			previous = destination[i];
		}
	}

	private static Matrix4x4 BuildCascadeViewProjection(SceneDrawData sceneData, Vector3 lightDirection, float receiverRadius)
	{
		// Keep the shadow volume anchored to the camera origin in camera-relative space.
		var frustumCenter = Vector3.Zero;
		var up = Math.Abs(Vector3.Dot(lightDirection, Vector3.UnitY)) > 0.99f
			? Vector3.UnitZ
			: Vector3.UnitY;
		var eye = frustumCenter - (lightDirection * (receiverRadius + 64.0f));
		var lightView = Matrix4x4.CreateLookAtLeftHanded(eye, frustumCenter, up);

		var halfExtent = receiverRadius;
		var centerLs = Vector3.Transform(frustumCenter, lightView);

		// Snap the projection center to shadow texels for camera-motion stability.
		var worldUnitsPerTexel = MathF.Max((halfExtent * 2.0f) / CascadeResolution, 1e-6f);
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
		return lightView * lightProjection;
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
