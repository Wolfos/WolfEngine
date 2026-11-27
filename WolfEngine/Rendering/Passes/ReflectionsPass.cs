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

	private readonly ComputeKernel _screenSpace;
	private readonly ComputeKernel _rayTraced;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private readonly IRenderer _renderer;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly RayTracingScene _rayTracingScene;
	private readonly Action<RenderGraphContext> _execute;

	internal ReflectionsPass(
		ShaderSource shaderSource,
		BindlessResourceRegistry bindlessRegistry,
		IRenderer renderer,
		GpuDrawResources gpuDrawResources,
		RayTracingScene rayTracingScene)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_screenSpace = new ComputeKernel(shaderSource, EngineShaderPrograms.ReflectionsScreenSpace, "ReflectionsScreenSpaceCS");
		_rayTraced = new ComputeKernel(shaderSource, EngineShaderPrograms.ReflectionsRayTraced, "ReflectionsRayTracedCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_gpuDrawResources = gpuDrawResources ?? throw new ArgumentNullException(nameof(gpuDrawResources));
		_rayTracingScene = rayTracingScene ?? throw new ArgumentNullException(nameof(rayTracingScene));
		_execute = Execute;
	}

	/// <summary>
	/// Adds the reflection trace. It runs before deferred lighting so its radiance can feed the specular term
	/// directly, which is why shaded hit colour comes from the previous frame's colour pyramid.
	/// </summary>
	internal void AddNodes(
		RenderGraph graph,
		in RenderViewResources resources,
		in RenderFrameSharedResources sharedResources)
	{
		var reflectionsBuilder = graph.AddPass(
				resources.Config.Reflections.Mode == ReflectionMode.RayTraced
					? "Reflections (Ray Traced)"
					: "Reflections (Screen Space)",
				PassKind.Compute)
			.ReadTexture(resources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferMaterial, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferEmissive, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferVelocity, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.WriteTexture(resources.ReflectionsTrace, ResourceState.UnorderedAccess);
		foreach (var colorPyramidLevel in resources.ColorPyramidLevels)
		{
			reflectionsBuilder.ReadTexture(colorPyramidLevel, ResourceState.ShaderResource);
		}
		reflectionsBuilder.ReadSkyLighting(resources, sharedResources);
		reflectionsBuilder.SetExecute(_execute);
	}

	private void Execute(RenderGraphContext context)
	{
		var resources = context.ViewState.FrameResources;
		var isRayTraced = resources.Config.Reflections.Mode == ReflectionMode.RayTraced;
		var config = BuildConfig(
			context,
			resources,
			context.SharedResources,
			context.Device,
			_renderer,
			_gpuDrawResources,
			isRayTraced ? _rayTracingScene.Resources : null);
		Record(context, in config, context.SceneData);
	}

	private ReflectionsPassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		RenderFrameSharedResources sharedResources,
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
			ReflectionMode.ScreenSpace => _screenSpace.GetPipeline(device),
			ReflectionMode.RayTraced => GetRayTracedPipeline(device),
			_ => throw new InvalidOperationException($"Unsupported reflection mode '{settings.Mode}'.")
		};

		_bindlessRegistry.EnsureInitialized(device);
		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(
				new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}

		var emissiveFallback = context.GetTexture(resources.GBufferEmissive);
		var irradiance = resources.SkyboxIrradiance.IsValid
			? context.GetTexture(resources.SkyboxIrradiance)
			: emissiveFallback;
		var prefiltered = resources.SkyboxPrefilter.IsValid
			? context.GetTexture(resources.SkyboxPrefilter)
			: emissiveFallback;
		var brdfLut = sharedResources.SkyboxBrdfLut.IsValid
			? context.GetTexture(sharedResources.SkyboxBrdfLut)
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

		var outputTexture = context.GetTexture(resources.ReflectionsTrace);
		return new ReflectionsPassConfig
		{
			Pipeline = pipeline,
			Mode = settings.Mode,
			Depth = _bindlessRegistry.RegisterDepthTexture(context.GetTexture(resources.GBufferDepth)),
			Normal = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.GBufferNormal)),
			Material = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.GBufferMaterial)),
			Velocity = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.GBufferVelocity)),
			Irradiance = _bindlessRegistry.GetTextureHandle(irradiance),
			PrefilteredEnvironment = _bindlessRegistry.GetTextureHandle(prefiltered),
			BrdfLut = _bindlessRegistry.GetTextureHandle(brdfLut),
			Output = _bindlessRegistry.RegisterRwTexture(outputTexture),
			LinearSampler = _linearSampler,
			ColorPyramidLevels = colorPyramidLevels,
			ColorPyramidLevelCount = colorPyramidLevelCount,
			ColorPyramidValid = resources.ColorPyramidHistoryValid && colorPyramidLevelCount > 0,
			FramebufferSize = resources.SceneFramebufferSize,
			DispatchSize = new(outputTexture.Descriptor.Width, outputTexture.Descriptor.Height),
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
			ReprojectionStrength = Math.Clamp(settings.ReprojectionStrength, 0.0f, 1.0f),
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

	private void Record(RenderGraphContext context, in ReflectionsPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var kernel = config.Mode == ReflectionMode.RayTraced
			? _rayTraced
			: _screenSpace;
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = kernel.Writer("BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.Depth.Value);
		bindlessWriter.SetUInt("normalHandle", config.Normal.Value);
		bindlessWriter.SetUInt("materialHandle", config.Material.Value);
		bindlessWriter.SetUInt("velocityHandle", config.Velocity.Value);
		if (config.Mode == ReflectionMode.RayTraced)
		{
			bindlessWriter.SetUInt("irradianceHandle", config.Irradiance.Value);
			bindlessWriter.SetUInt("prefilteredHandle", config.PrefilteredEnvironment.Value);
			bindlessWriter.SetUInt("brdfLutHandle", config.BrdfLut.Value);
		}
		bindlessWriter.SetUInt("outputHandle", config.Output.Value);
		bindlessWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var cameraWriter = kernel.Writer("CameraParams");
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

		var settingsWriter = kernel.Writer("ReflectionSettings");
		settingsWriter.Clear();
		settingsWriter.SetUInt("framebufferSizeX", (uint)Math.Max(config.FramebufferSize.X, 1));
		settingsWriter.SetUInt("framebufferSizeY", (uint)Math.Max(config.FramebufferSize.Y, 1));
		settingsWriter.SetUInt("maxSteps", (uint)config.MaxSteps);
		settingsWriter.SetUInt("binarySearchSteps", (uint)config.BinarySearchSteps);
		settingsWriter.SetFloat("maxRayDistance", config.MaxRayDistance);
		settingsWriter.SetFloat("thickness", config.Thickness);
		settingsWriter.SetFloat("bias", config.Bias);
		settingsWriter.SetFloat("maxRoughness", config.MaxRoughness);
		settingsWriter.SetFloat("edgeFade", config.EdgeFade);
		settingsWriter.SetFloat("intensity", config.Intensity);
		settingsWriter.SetFloat("reprojectionStrength", config.ReprojectionStrength);
		settingsWriter.SetUInt("colorPyramidLevelCount", (uint)Math.Max(config.ColorPyramidLevelCount, 1));
		settingsWriter.SetUInt("colorPyramidValid", config.ColorPyramidValid ? 1u : 0u);
		for (var level = 0; level < config.ColorPyramidLevels.Length; level++)
		{
			settingsWriter.SetUInt($"colorPyramidHandles[{level}]", config.ColorPyramidLevels[level].Value);
		}

		if (config.Mode == ReflectionMode.RayTraced)
		{
			settingsWriter.SetFloat("screenReuseFalloff", config.ScreenReuseFalloff);
			settingsWriter.SetUInt("outputSizeX", (uint)Math.Max(config.DispatchSize.X, 1));
			settingsWriter.SetUInt("outputSizeY", (uint)Math.Max(config.DispatchSize.Y, 1));
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
				var colorScale = sceneData.SunLightAttenuation.GetColorScale(light, forward);
				settingsWriter.SetColorRGBA(
					$"directionalLights[{directionalLightCount}].colorIntensity",
					new ColorRGBA(
						light.Color.R * colorScale.X,
						light.Color.G * colorScale.Y,
						light.Color.B * colorScale.Z,
						light.Intensity));
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

		var size = kernel.ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = size.GetDispatchGroupCount(
			(uint)Math.Max(config.DispatchSize.X, 1),
			(uint)Math.Max(config.DispatchSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private IGfxPipeline GetRayTracedPipeline(IGfxDevice device)
	{
		if (device.SupportsRayTracing == false)
		{
			throw new NotSupportedException("Ray traced reflections require a ray-tracing capable graphics device.");
		}

		return _rayTraced.GetPipeline(device);
	}
}
