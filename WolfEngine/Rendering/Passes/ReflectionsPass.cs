using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Traces glossy reflections using either screen-space ray marching or inline hardware ray
/// tracing, and writes incident radiance plus a replacement weight for deferred lighting to
/// consume as part of its specular term. Runs ahead of deferred lighting, so shaded hit color
/// comes from the previous frame's color pyramid.
/// </summary>
public sealed class ReflectionsPass
{
	/// <summary>Matches REFLECTION_MAX_COLOR_PYRAMID_LEVELS in reflections_common.slang.</summary>
	public const int MaxColorPyramidLevels = 8;

	private readonly IShaderProvider _shaderProvider;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _screenSpacePipeline;
	private IGfxPipeline? _rayTracedPipeline;
	private ReadOnlyMemory<byte> _screenSpaceShader;
	private ReadOnlyMemory<byte> _rayTracedShader;
	private ComputeThreadGroupSize? _screenSpaceThreadGroupSize;
	private ComputeThreadGroupSize? _rayTracedThreadGroupSize;
	private GraphicsBackendKind? _screenSpaceBackendKind;
	private GraphicsBackendKind? _rayTracedBackendKind;
	private ShaderPropertyWriter? _screenSpaceBindlessWriter;
	private ShaderPropertyWriter? _screenSpaceCameraWriter;
	private ShaderPropertyWriter? _screenSpaceSettingsWriter;
	private ShaderPropertyWriter? _rayTracedBindlessWriter;
	private ShaderPropertyWriter? _rayTracedCameraWriter;
	private ShaderPropertyWriter? _rayTracedSettingsWriter;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public ReflectionsPass(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public ReflectionsPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		IRenderer renderer,
		GpuDrawResources gpuDrawResources,
		IRayTracingSceneResources? rayTracingSceneResources)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);

		var settings = resources.Config.Reflections;
		var pipeline = settings.Mode switch
		{
			ReflectionMode.ScreenSpace => EnsureScreenSpacePipeline(device),
			ReflectionMode.RayTraced => EnsureRayTracedPipeline(device),
			_ => throw new InvalidOperationException($"Unsupported reflection mode '{settings.Mode}'.")
		};

		_bindlessRegistry.EnsureInitialized(device);
		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(
				new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}

		var emissiveFallback = context.GetTexture(resources.GBufferEmissive);
		var environment = resources.SkyboxEnvironment.IsValid
			? context.GetTexture(resources.SkyboxEnvironment)
			: emissiveFallback;
		var irradiance = resources.SkyboxIrradiance.IsValid
			? context.GetTexture(resources.SkyboxIrradiance)
			: emissiveFallback;
		var prefiltered = resources.SkyboxPrefilter.IsValid
			? context.GetTexture(resources.SkyboxPrefilter)
			: emissiveFallback;
		var brdfLut = resources.SkyboxBrdfLut.IsValid
			? context.GetTexture(resources.SkyboxBrdfLut)
			: emissiveFallback;
		var isRayTraced = settings.Mode == ReflectionMode.RayTraced;
		var screenSpace = settings.ScreenSpaceSettings;
		var rayTraced = settings.RayTracedSettings;

		var colorPyramidLevels = new DescriptorHandle[MaxColorPyramidLevels];
		var colorPyramidLevelCount = Math.Min(resources.ColorPyramidLevels.Length, MaxColorPyramidLevels);
		for (var level = 0; level < colorPyramidLevels.Length; level++)
		{
			// Pad the tail with the coarsest available level so a dynamic index can never
			// reach an unbound descriptor.
			var source = colorPyramidLevelCount > 0
				? resources.ColorPyramidLevels[Math.Min(level, colorPyramidLevelCount - 1)]
				: default;
			colorPyramidLevels[level] = source.IsValid
				? _bindlessRegistry.GetTextureHandle(context.GetTexture(source))
				: _bindlessRegistry.GetTextureHandle(emissiveFallback);
		}

		return new ReflectionsPassConfig
		{
			Pipeline = pipeline,
			Mode = settings.Mode,
			Depth = _bindlessRegistry.RegisterDepthTexture(context.GetTexture(resources.GBufferDepth)),
			Normal = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.GBufferNormal)),
			Material = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.GBufferMaterial)),
			Velocity = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.GBufferVelocity)),
			Environment = _bindlessRegistry.GetTextureHandle(environment),
			Irradiance = _bindlessRegistry.GetTextureHandle(irradiance),
			PrefilteredEnvironment = _bindlessRegistry.GetTextureHandle(prefiltered),
			BrdfLut = _bindlessRegistry.GetTextureHandle(brdfLut),
			Output = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.ReflectionsRadiance)),
			LinearSampler = _linearSampler,
			ColorPyramidLevels = colorPyramidLevels,
			ColorPyramidLevelCount = colorPyramidLevelCount,
			ColorPyramidValid = resources.ColorPyramidHistoryValid && colorPyramidLevelCount > 0,
			DispatchSize = resources.SceneFramebufferSize,
			MaxSteps = Math.Max(screenSpace.MaxSteps, 1),
			BinarySearchSteps = Math.Max(screenSpace.BinarySearchSteps, 0),
			MaxRayDistance = Math.Max(
				isRayTraced ? rayTraced.MaxRayDistance : screenSpace.MaxRayDistance,
				0.001f),
			Thickness = Math.Max(
				isRayTraced ? rayTraced.ScreenReuseThickness : screenSpace.Thickness,
				0.001f),
			Bias = Math.Max(isRayTraced ? rayTraced.Bias : screenSpace.Bias, 0.0f),
			MaxRoughness = Math.Clamp(
				isRayTraced ? rayTraced.MaxRoughness : screenSpace.MaxRoughness,
				0.001f,
				1.0f),
			EdgeFade = Math.Clamp(screenSpace.EdgeFade, 0.001f, 0.5f),
			ScreenReuseFalloff = Math.Clamp(rayTraced.ScreenReuseFalloff, 0.0f, 1.0f),
			Intensity = Math.Max(isRayTraced ? rayTraced.Intensity : screenSpace.Intensity, 0.0f),
			TopLevelAccelerationStructure = isRayTraced ? rayTracingSceneResources?.TopLevelAccelerationStructure : null,
			InstanceBuffer = isRayTraced ? gpuDrawResources.InstanceBuffer : null,
			MaterialBuffer = isRayTraced ? gpuDrawResources.MaterialBuffer : null,
			InstanceIndexToInstanceHandleBuffer =
				isRayTraced ? rayTracingSceneResources?.InstanceIndexToInstanceHandleBuffer : null,
			MeshBuffer = isRayTraced ? gpuDrawResources.MeshBuffer : null,
			PackedMeshVertexBuffer = isRayTraced ? renderer.GetPackedMeshVertexBuffer() : null,
			PackedMeshIndexBuffer = isRayTraced ? renderer.GetPackedMeshIndexBuffer() : null
		};
	}

	public void Record(RenderGraphContext context, in ReflectionsPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = (config.Mode == ReflectionMode.RayTraced
			? _rayTracedBindlessWriter
			: _screenSpaceBindlessWriter)
			?? throw new InvalidOperationException("Reflections bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.Depth.Value);
		bindlessWriter.SetUInt("normalHandle", config.Normal.Value);
		bindlessWriter.SetUInt("materialHandle", config.Material.Value);
		bindlessWriter.SetUInt("velocityHandle", config.Velocity.Value);
		if (config.Mode == ReflectionMode.RayTraced)
		{
			bindlessWriter.SetUInt("environmentHandle", config.Environment.Value);
			bindlessWriter.SetUInt("irradianceHandle", config.Irradiance.Value);
			bindlessWriter.SetUInt("prefilteredHandle", config.PrefilteredEnvironment.Value);
			bindlessWriter.SetUInt("brdfLutHandle", config.BrdfLut.Value);
		}
		bindlessWriter.SetUInt("outputHandle", config.Output.Value);
		bindlessWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var cameraWriter = (config.Mode == ReflectionMode.RayTraced
			? _rayTracedCameraWriter
			: _screenSpaceCameraWriter)
			?? throw new InvalidOperationException("Reflections camera writer was not initialized.");
		cameraWriter.Clear();
		cameraWriter.SetMatrix4x4("camera.invProjection", sceneData.InverseProjection);
		cameraWriter.SetMatrix4x4("camera.invViewProjection", sceneData.InverseViewProjection);
		cameraWriter.SetVector3("camera.cameraOrigin", sceneData.CameraOrigin);
		cameraWriter.SetMatrix4x4("camera.viewMatrix", sceneData.ViewMatrix);
		cameraWriter.SetFloat("camera.nearPlane", sceneData.NearPlane);
		cameraWriter.SetFloat("camera.farPlane", sceneData.FarPlane);
		cameraWriter.SetUInt("camera.frameSizeX", (uint)Math.Max(sceneData.SceneFramebufferSize.X, 1));
		cameraWriter.SetUInt("camera.frameSizeY", (uint)Math.Max(sceneData.SceneFramebufferSize.Y, 1));
		cameraWriter.SetMatrix4x4("viewProjection", sceneData.ViewProjection);
		commandList.SetComputeConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());

		var settingsWriter = (config.Mode == ReflectionMode.RayTraced
			? _rayTracedSettingsWriter
			: _screenSpaceSettingsWriter)
			?? throw new InvalidOperationException("Reflections settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetUInt("framebufferSizeX", (uint)Math.Max(config.DispatchSize.X, 1));
		settingsWriter.SetUInt("framebufferSizeY", (uint)Math.Max(config.DispatchSize.Y, 1));
		settingsWriter.SetUInt("maxSteps", (uint)config.MaxSteps);
		settingsWriter.SetUInt("binarySearchSteps", (uint)config.BinarySearchSteps);
		settingsWriter.SetFloat("maxRayDistance", config.MaxRayDistance);
		settingsWriter.SetFloat("thickness", config.Thickness);
		settingsWriter.SetFloat("bias", config.Bias);
		settingsWriter.SetFloat("maxRoughness", config.MaxRoughness);
		settingsWriter.SetFloat("edgeFade", config.EdgeFade);
		settingsWriter.SetFloat("intensity", config.Intensity);
		settingsWriter.SetUInt("colorPyramidLevelCount", (uint)Math.Max(config.ColorPyramidLevelCount, 1));
		settingsWriter.SetUInt("colorPyramidValid", config.ColorPyramidValid ? 1u : 0u);
		for (var level = 0; level < config.ColorPyramidLevels.Length; level++)
		{
			settingsWriter.SetUInt($"colorPyramidHandles[{level}]", config.ColorPyramidLevels[level].Value);
		}

		if (config.Mode == ReflectionMode.RayTraced)
		{
			settingsWriter.SetFloat("screenReuseFalloff", config.ScreenReuseFalloff);
			var directionalLightCount = 0;
			for (var i = 0;
			     i < sceneData.Lights.Count &&
			     directionalLightCount < ClusteredLightingShared.MaxDirectionalLights;
			     i++)
			{
				var packet = sceneData.Lights[i];
				var light = packet.Light;
				if (light.Type != LightType.Directional)
				{
					continue;
				}

				var forward = Vector3.TransformNormal(Vector3.UnitZ, packet.Transform);
				if (forward == Vector3.Zero)
				{
					forward = new Vector3(0, -1, 0);
				}
				var intensityScale = DirectionalLightUtility.GetIntensityScale(light, forward);
				settingsWriter.SetColorRGBA(
					$"directionalLights[{directionalLightCount}].colorIntensity",
					new ColorRGBA(
						light.Color.R,
						light.Color.G,
						light.Color.B,
						light.Intensity * intensityScale));
				settingsWriter.SetVector4(
					$"directionalLights[{directionalLightCount}].directionAndType",
					new Vector4(forward, 0.0f));
				directionalLightCount++;
			}
			settingsWriter.SetUInt("directionalLightCount", (uint)directionalLightCount);
		}

		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		if (config.Mode == ReflectionMode.RayTraced)
		{
			var tlas = config.TopLevelAccelerationStructure
				?? throw new InvalidOperationException("Ray traced reflections require a valid top-level acceleration structure.");
			commandList.SynchronizeAccelerationStructureBuildForComputeRead(tlas);
			commandList.SetComputeAccelerationStructure(3, tlas);
			commandList.SetComputeReadOnlyBuffer(4, config.InstanceBuffer
				?? throw new InvalidOperationException("Ray traced reflections instance buffer missing."));
			commandList.SetComputeReadOnlyBuffer(5, config.MaterialBuffer
				?? throw new InvalidOperationException("Ray traced reflections material buffer missing."));
			commandList.SetComputeReadOnlyBuffer(6, config.InstanceIndexToInstanceHandleBuffer
				?? throw new InvalidOperationException("Ray traced reflections instance sidecar missing."));
			commandList.SetComputeReadOnlyBuffer(7, config.MeshBuffer
				?? throw new InvalidOperationException("Ray traced reflections mesh buffer missing."));
			commandList.SetComputeReadOnlyBuffer(8, config.PackedMeshVertexBuffer
				?? throw new InvalidOperationException("Ray traced reflections packed vertex buffer missing."));
			commandList.SetComputeReadOnlyBuffer(9, config.PackedMeshIndexBuffer
				?? throw new InvalidOperationException("Ray traced reflections packed index buffer missing."));
		}

		var threadGroupSize = config.Mode == ReflectionMode.RayTraced
			? _rayTracedThreadGroupSize
			: _screenSpaceThreadGroupSize;
		var size = threadGroupSize
			?? throw new InvalidOperationException("Reflections threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = size.GetDispatchGroupCount(
			(uint)Math.Max(config.DispatchSize.X, 1),
			(uint)Math.Max(config.DispatchSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private IGfxPipeline EnsureScreenSpacePipeline(IGfxDevice device)
	{
		if (_screenSpacePipeline is not null)
		{
			ValidateBackend(_screenSpaceBackendKind, device.BackendKind, "screen-space");
			return _screenSpacePipeline;
		}

		var compiled = _shaderProvider.GetComputeShaderWithReflection(
			EngineShaderPrograms.ReflectionsScreenSpace,
			"ReflectionsScreenSpaceCS",
			device.BackendKind);
		_screenSpaceShader = compiled.Bytecode;
		_screenSpaceThreadGroupSize = compiled.ThreadGroupSize;
		_screenSpaceBindlessWriter = new(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_screenSpaceCameraWriter = new(compiled.ReflectionLayout.GetConstantBuffer("CameraParams"));
		_screenSpaceSettingsWriter = new(compiled.ReflectionLayout.GetConstantBuffer("ReflectionSettings"));
		_screenSpaceBackendKind = device.BackendKind;
		_screenSpacePipeline = CreatePipeline(
			device,
			"ReflectionsScreenSpaceCS",
			"reflections_ssr.compute.slang",
			_screenSpaceShader,
			_screenSpaceThreadGroupSize);
		return _screenSpacePipeline;
	}

	private IGfxPipeline EnsureRayTracedPipeline(IGfxDevice device)
	{
		if (_rayTracedPipeline is not null)
		{
			ValidateBackend(_rayTracedBackendKind, device.BackendKind, "ray-traced");
			return _rayTracedPipeline;
		}
		if (device.SupportsRayTracing == false)
		{
			throw new NotSupportedException("Ray traced reflections require a ray-tracing capable graphics device.");
		}

		var compiled = _shaderProvider.GetComputeShaderWithReflection(
			EngineShaderPrograms.ReflectionsRayTraced,
			"ReflectionsRayTracedCS",
			device.BackendKind);
		_rayTracedShader = compiled.Bytecode;
		_rayTracedThreadGroupSize = compiled.ThreadGroupSize;
		_rayTracedBindlessWriter = new(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_rayTracedCameraWriter = new(compiled.ReflectionLayout.GetConstantBuffer("CameraParams"));
		_rayTracedSettingsWriter = new(compiled.ReflectionLayout.GetConstantBuffer("ReflectionSettings"));
		_rayTracedBackendKind = device.BackendKind;
		_rayTracedPipeline = CreatePipeline(
			device,
			"ReflectionsRayTracedCS",
			"reflections_rt.compute.slang",
			_rayTracedShader,
			_rayTracedThreadGroupSize);
		return _rayTracedPipeline;
	}

	private static IGfxPipeline CreatePipeline(
		IGfxDevice device,
		string entryPoint,
		string variant,
		ReadOnlyMemory<byte> shader,
		ComputeThreadGroupSize? threadGroupSize)
	{
		var key = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: entryPoint,
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: variant);
		return device.GetOrCreatePipeline(
			key,
			new ShaderBytecodeSet(compute: shader, computeThreadGroupSize: threadGroupSize));
	}

	private static void ValidateBackend(
		GraphicsBackendKind? compiledBackend,
		GraphicsBackendKind requestedBackend,
		string mode)
	{
		if (compiledBackend.HasValue && compiledBackend.Value != requestedBackend)
		{
			throw new InvalidOperationException(
				$"ReflectionsPass {mode} pipeline is already compiled for backend '{compiledBackend.Value}', " +
				$"but was requested for '{requestedBackend}'.");
		}
	}
}
