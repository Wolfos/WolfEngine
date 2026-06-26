#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class ShadowMapPass
{
	public const int CascadeCount = 3;
	public const int CascadeResolution = 2048;
	public const float MaxShadowDistance = 150.0f;

	private const float DefaultDepthBiasWorld = 0.05f;
	private const float DefaultDepthBiasTexelScale = 1.0f;
	private const float DefaultStrength = 1.0f;
	private const float CasterPaddingNear = 96.0f;
	private const float CasterPaddingFar = 24.0f;
	private const float DefaultCascadeBlendDistance = 2.0f;

	private static readonly List<float> CascadeSplitDistances =
	[
		6.0f,
		30.0f,
		MaxShadowDistance
	];

	private readonly IShaderCompiler _shaderCompiler;
	private readonly Dictionary<(int CascadeIndex, GpuDrawExecutionKey ExecutionKey), IGfxPipeline> _pipelinesByCascadeExecutionKey = new();
	private readonly Dictionary<(int CascadeIndex, GpuDrawExecutionKey ExecutionKey), SharedDrawGraphicsBufferBindings> _bufferBindingsByCascadeExecutionKey = new();
	private readonly SharedDrawIndirectCommandSet[] _indirectCommandSets =
	[
		new(),
		new(),
		new()
	];
	private ShadowFrameData _currentFrameData = CreateDisabledFrameData();
	private GraphicsBackendKind? _reflectionBackendKind;
	private ShaderPropertyWriter? _cameraWriter;

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
				depthBiases: ComputeCascadeDepthBiases(splits),
				strength: DefaultStrength,
				mapResolution: CascadeResolution);
			return;
		}

		_currentFrameData = CreateDisabledFrameData();
	}

	public ShadowFrameData GetCurrentFrameData() => _currentFrameData;

	public SharedDrawIndirectCommandSet GetIndirectCommandSet(int cascadeIndex)
	{
		ValidateCascadeIndex(cascadeIndex);
		return _indirectCommandSets[cascadeIndex];
	}

	public void EnsureIndirectResources(IGfxDevice device, int cascadeIndex)
	{
		ArgumentNullException.ThrowIfNull(device);
		ValidateCascadeIndex(cascadeIndex);
		var laneDefinitions = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.ShadowCaster);
		for (var i = 0; i < laneDefinitions.Length; i++)
		{
			EnsurePipeline(device, laneDefinitions[i], cascadeIndex);
		}

		_indirectCommandSets[cascadeIndex].EnsureCreated(device);
	}

	public bool HasIndirectLane(int cascadeIndex, GpuDrawExecutionLaneDefinition lane)
	{
		ValidateCascadeIndex(cascadeIndex);
		return _pipelinesByCascadeExecutionKey.ContainsKey((cascadeIndex, lane.Key));
	}

	public SharedDrawGraphicsBufferBindings? GetBufferBindings(int cascadeIndex, GpuDrawExecutionLaneDefinition lane)
	{
		ValidateCascadeIndex(cascadeIndex);
		return _bufferBindingsByCascadeExecutionKey.TryGetValue((cascadeIndex, lane.Key), out var bindings)
			? bindings
			: null;
	}

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
		EnsureCameraWriter(device.BackendKind);

		ValidateCascadeIndex(cascadeIndex);

		var laneDefinitions = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.ShadowCaster);
		var buckets = new List<ShadowMapExecutionBucket>(laneDefinitions.Length);
		var activeIndirectSlot = gpuDrawResources.ActiveIndirectCommandSlot;
		var commandSet = _indirectCommandSets[cascadeIndex];
		commandSet.EnsureCreated(device);
		for (var i = 0; i < laneDefinitions.Length; i++)
		{
			var laneDefinition = laneDefinitions[i];
			var pipeline = EnsurePipeline(device, laneDefinition, cascadeIndex);
			buckets.Add(new ShadowMapExecutionBucket(
				laneDefinition.DrawKind,
				laneDefinition.BucketId,
				laneDefinition.ExecutionIndex,
				laneDefinition.DebugName,
				_bufferBindingsByCascadeExecutionKey[(cascadeIndex, laneDefinition.Key)],
				pipeline,
				commandSet.GetCommandBuffer(activeIndirectSlot, laneDefinition)));
		}

		return new ShadowMapPassConfig
		{
			CascadeIndex = cascadeIndex,
			FramebufferWidth = depthTarget.Descriptor.Width,
			FramebufferHeight = depthTarget.Descriptor.Height,
			DepthTarget = depthTarget,
			InstanceBuffer = gpuDrawResources.InstanceBuffer,
			MaterialBuffer = gpuDrawResources.MaterialBuffer,
			TerrainMaterialBuffer = gpuDrawResources.TerrainMaterialBuffer,
			TerrainLayerBuffer = gpuDrawResources.TerrainLayerBuffer,
			DrawArgsBuffer = gpuDrawResources.DrawArgsBuffer,
			CameraBuffer = gpuDrawResources.ShadowCameraBuffer,
			MaterialGenerationBuffer = gpuDrawResources.MaterialGenerationBuffer,
			VisibleDrawIdsPerExecutionLaneBuffer = gpuDrawResources.VisibleDrawIdsPerExecutionLaneBuffer,
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

		var buckets = config.Buckets.Span;
		if (buckets.Length == 0)
		{
			commandList.EndPass();
			return;
		}
		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);

		for (var i = 0; i < buckets.Length; i++)
		{
			var bucket = buckets[i];
			using (FrameProfiler.Instance.Measure($"Shadow.C{config.CascadeIndex}.{bucket.DebugName}"))
			{
				commandList.BindPipeline(bucket.Pipeline);
				commandList.BindConstantBuffer(bucket.BufferBindings.InstanceRegisterIndex, config.InstanceBuffer);
				commandList.BindConstantBuffer(bucket.BufferBindings.MaterialRegisterIndex, config.MaterialBuffer);
				commandList.BindConstantBuffer(bucket.BufferBindings.DrawArgsRegisterIndex, config.DrawArgsBuffer);
				if (config.MaterialGenerationBuffer is not null)
				{
					commandList.BindConstantBuffer(bucket.BufferBindings.MaterialGenerationRegisterIndex, config.MaterialGenerationBuffer);
				}
				if (config.TerrainMaterialBuffer is not null &&
				    bucket.BufferBindings.TerrainMaterialRegisterIndex is { } terrainMaterialRegisterIndex)
				{
					commandList.BindConstantBuffer(terrainMaterialRegisterIndex, config.TerrainMaterialBuffer);
				}
				if (config.TerrainLayerBuffer is not null &&
				    bucket.BufferBindings.TerrainLayerRegisterIndex is { } terrainLayerRegisterIndex)
				{
					commandList.BindConstantBuffer(terrainLayerRegisterIndex, config.TerrainLayerBuffer);
				}

				commandList.BindConstantBuffer(
					_cameraWriter?.RegisterIndex ?? throw new InvalidOperationException("Shadow camera writer was not initialized."),
					config.CameraBuffer);
				if (config.VisibleDrawIdsPerExecutionLaneBuffer is not null &&
				    config.DrawExecutionRangePerBucketBuffer is not null)
				{
					var indicesOffsetBytes = (ulong)(bucket.ExecutionIndex * GpuDrawResources.MaxDrawCount * sizeof(uint));
					var rangeOffsetBytes = (ulong)(bucket.ExecutionIndex * 2 * sizeof(uint));
					commandList.ExecuteIndirectCommandBufferIndexed(
						bucket.IndirectCommandBuffer,
						config.VisibleDrawIdsPerExecutionLaneBuffer,
						indicesOffsetBytes,
						config.DrawExecutionRangePerBucketBuffer,
						rangeOffsetBytes);
				}
			}
		}

		commandList.EndPass();
	}

	private IGfxPipeline EnsurePipeline(
		IGfxDevice device,
		in GpuDrawExecutionLaneDefinition lane,
		int cascadeIndex)
	{
		var pipelineKeyByCascadeBucket = (cascadeIndex, lane.Key);
		if (_pipelinesByCascadeExecutionKey.TryGetValue(pipelineKeyByCascadeBucket, out var pipeline))
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
			shaderVariant: $"Shadow:{lane.ShaderVariant}:C{cascadeIndex}");

		var cascadeDefine = $"WOLF_SHADOW_CASCADE_INDEX={cascadeIndex}";
		var compiled = GraphicsShaderCompiler.CompileWithReflection(
			_shaderCompiler,
			device.BackendKind,
			"shadow_map.slang",
			"vertexShader",
			"fragmentShader",
			lane.PreprocessorDefine,
			cascadeDefine);
		pipeline = device.GetOrCreatePipeline(key, compiled.Bytecode);
		_pipelinesByCascadeExecutionKey[pipelineKeyByCascadeBucket] = pipeline;
		_bufferBindingsByCascadeExecutionKey[pipelineKeyByCascadeBucket] =
			SharedDrawGraphicsBufferBindings.FromShadowReflection(compiled.ReflectionLayout);
		return pipeline;
	}

	private void UploadCameraConstants(RenderGraphContext context, in ShadowMapPassConfig config, IGfxCommandList commandList)
	{
		var cameraWriter = _cameraWriter
			?? throw new InvalidOperationException("Shadow camera writer was not initialized.");
		cameraWriter.Clear();
		cameraWriter.SetMatrix4x4("viewProjection0", _currentFrameData.CascadeViewProjection0);
		cameraWriter.SetMatrix4x4("viewProjection1", _currentFrameData.CascadeViewProjection1);
		cameraWriter.SetMatrix4x4("viewProjection2", _currentFrameData.CascadeViewProjection2);
		var sceneData = context.SceneData!;
		cameraWriter.SetVector3("cameraPosition", sceneData.CameraOrigin);

		if (config.CameraBuffer is IWritableGpuBuffer writableCameraBuffer)
		{
			writableCameraBuffer.Write<byte>(cameraWriter.AsBytes());
			return;
		}

		commandList.SetGraphicsConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());
	}

	private void EnsureCameraWriter(GraphicsBackendKind backendKind)
	{
		if (_reflectionBackendKind.HasValue &&
		    _reflectionBackendKind.Value == backendKind &&
		    _cameraWriter is not null)
		{
			return;
		}

		var compiled = GraphicsShaderCompiler.CompileWithReflection(
			_shaderCompiler,
			backendKind,
			"shadow_map.slang",
			"vertexShader",
			"fragmentShader");
		_cameraWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("CameraParams"));
		_reflectionBackendKind = backendKind;
	}

	private static void ValidateCascadeIndex(int cascadeIndex)
	{
		if (cascadeIndex < 0 || cascadeIndex >= CascadeCount)
		{
			throw new ArgumentOutOfRangeException(nameof(cascadeIndex), cascadeIndex, "Cascade index is out of range.");
		}
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
		depthBiases: ComputeDefaultCascadeDepthBiases(),
		strength: DefaultStrength,
		mapResolution: CascadeResolution);

	private static Vector3 ComputeDefaultCascadeDepthBiases()
	{
		Span<float> splits = stackalloc float[CascadeCount];
		BuildConfiguredCascadeSplits(splits);
		return ComputeCascadeDepthBiases(splits);
	}

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
		var directionalLightCount = 0;
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
			shadowedLightIndex = directionalLightCount;
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

	private static Vector3 ComputeCascadeDepthBiases(ReadOnlySpan<float> cascadeSplits)
	{
		if (cascadeSplits.Length < CascadeCount)
		{
			throw new ArgumentException("Cascade split span must contain all cascade splits.", nameof(cascadeSplits));
		}

		return new Vector3(
			ComputeCascadeDepthBias(cascadeSplits[0]),
			ComputeCascadeDepthBias(cascadeSplits[1]),
			ComputeCascadeDepthBias(cascadeSplits[2]));
	}

	private static float ComputeCascadeDepthBias(float receiverRadius)
	{
		var worldUnitsPerTexel = MathF.Max((receiverRadius * 2.0f) / CascadeResolution, 1e-6f);
		var worldBias = DefaultDepthBiasWorld + (worldUnitsPerTexel * DefaultDepthBiasTexelScale);
		return worldBias / ComputeCascadeDepthSpan(receiverRadius);
	}

	private static float ComputeCascadeDepthSpan(float receiverRadius) =>
		MathF.Max((receiverRadius * 2.0f) + CasterPaddingNear + CasterPaddingFar, 1.0f);
}
