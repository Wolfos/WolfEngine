using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class AmbientOcclusionPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _visibilityBitmaskPipeline;
	private IGfxPipeline? _rayTracedPipeline;
	private ReadOnlyMemory<byte> _visibilityBitmaskComputeShader;
	private ReadOnlyMemory<byte> _rayTracedComputeShader;
	private ComputeThreadGroupSize? _visibilityBitmaskThreadGroupSize;
	private ComputeThreadGroupSize? _rayTracedThreadGroupSize;
	private GraphicsBackendKind? _visibilityBitmaskCompiledBackendKind;
	private GraphicsBackendKind? _rayTracedCompiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _cameraWriter;
	private ShaderPropertyWriter? _settingsWriter;
	private ShaderPropertyWriter? _rayTracedBindlessWriter;
	private ShaderPropertyWriter? _rayTracedCameraWriter;
	private ShaderPropertyWriter? _rayTracedSettingsWriter;
	private uint _frameIndex;
	public AmbientOcclusionPass(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public AmbientOcclusionPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		IRayTracingSceneResources? rayTracingSceneResources)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var settings = resources.Config.AmbientOcclusion;
		var mode = settings.Mode;
		var pipeline = mode switch
		{
			AmbientOcclusionMode.VisibilityBitmask => EnsureVisibilityBitmaskPipeline(device),
			AmbientOcclusionMode.RayTraced => EnsureRayTracedPipeline(device),
			_ => throw new InvalidOperationException($"Unsupported ambient occlusion mode '{mode}'.")
		};
		_bindlessRegistry.EnsureInitialized(device);

		var depth = context.GetTexture(resources.GBufferDepth);
		var normal = context.GetTexture(resources.GBufferNormal);
		var output = context.GetTexture(resources.AmbientOcclusionRaw);
		var vbaoSettings = settings.VisibilityBitmaskSettings;
		var rayTracedSettings = settings.RayTracedSettings;
		var depthHandle = _bindlessRegistry.RegisterDepthTexture(depth);
		var normalHandle = _bindlessRegistry.GetTextureHandle(normal);
		var outputHandle = _bindlessRegistry.RegisterRwTexture(output);
		var hitMaskHandle = DescriptorHandle.Invalid;
		var hitDistanceHandle = DescriptorHandle.Invalid;
		if (mode == AmbientOcclusionMode.RayTraced &&
		    resources.RayTracingHitMask.IsValid &&
		    resources.RayTracingHitDistance.IsValid)
		{
			hitMaskHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.RayTracingHitMask));
			hitDistanceHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(resources.RayTracingHitDistance));
		}

		return new AmbientOcclusionPassConfig
		{
			Pipeline = pipeline,
			Mode = mode,
			DepthHandle = depthHandle,
			NormalHandle = normalHandle,
			OutputHandle = outputHandle,
			RayTracingHitMaskHandle = hitMaskHandle,
			RayTracingHitDistanceHandle = hitDistanceHandle,
			TopLevelAccelerationStructure = rayTracingSceneResources?.TopLevelAccelerationStructure,
			FullResolution = resources.SceneFramebufferSize,
			OutputResolution = new(output.Descriptor.Width, output.Descriptor.Height),
			SliceCount = Math.Max(1, vbaoSettings.SliceCount),
			StepCount = Math.Max(1, vbaoSettings.StepCount),
			Radius = Math.Max(mode == AmbientOcclusionMode.RayTraced ? rayTracedSettings.Radius : vbaoSettings.Radius, 0.001f),
			Thickness = Math.Max(vbaoSettings.Thickness, 0.0f),
			Bias = Math.Max(mode == AmbientOcclusionMode.RayTraced ? rayTracedSettings.Bias : vbaoSettings.Bias, 0.0f),
			Strength = Math.Max(mode == AmbientOcclusionMode.RayTraced ? rayTracedSettings.Strength : vbaoSettings.Strength, 0.0f),
			Power = Math.Max(vbaoSettings.Power, 0.001f),
			FrameIndex = _frameIndex++
		};
	}

	public void Record(RenderGraphContext context, in AmbientOcclusionPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		if (config.Mode == AmbientOcclusionMode.RayTraced)
		{
			RecordRayTraced(context, in config, sceneData);
			return;
		}

		RecordVisibilityBitmask(context, in config, sceneData);
	}

	private void RecordVisibilityBitmask(RenderGraphContext context, in AmbientOcclusionPassConfig config, SceneDrawData sceneData)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("Ambient occlusion bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.DepthHandle.Value);
		bindlessWriter.SetUInt("normalHandle", config.NormalHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var cameraWriter = _cameraWriter
			?? throw new InvalidOperationException("Ambient occlusion camera writer was not initialized.");
		cameraWriter.Clear();
		cameraWriter.SetMatrix4x4("camera.invProjection", sceneData.InverseProjection);
		cameraWriter.SetMatrix4x4("camera.invViewProjection", sceneData.InverseViewProjection);
		cameraWriter.SetVector3("camera.cameraOrigin", sceneData.CameraOrigin);
		cameraWriter.SetMatrix4x4("camera.viewMatrix", sceneData.ViewMatrix);
		cameraWriter.SetFloat("camera.nearPlane", sceneData.NearPlane);
		cameraWriter.SetFloat("camera.farPlane", sceneData.FarPlane);
		cameraWriter.SetUInt("camera.frameSizeX", (uint)Math.Max(sceneData.SceneFramebufferSize.X, 1));
		cameraWriter.SetUInt("camera.frameSizeY", (uint)Math.Max(sceneData.SceneFramebufferSize.Y, 1));
		commandList.SetComputeConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());

		var settingsWriter = _settingsWriter
			?? throw new InvalidOperationException("Ambient occlusion settings writer was not initialized.");
		if (Matrix4x4.Invert(sceneData.InverseProjection, out var projectionMatrix) == false)
		{
			throw new InvalidOperationException("Ambient occlusion projection parameters could not be reconstructed.");
		}

		settingsWriter.Clear();
		settingsWriter.SetUInt("fullResolutionX", (uint)Math.Max(config.FullResolution.X, 1));
		settingsWriter.SetUInt("fullResolutionY", (uint)Math.Max(config.FullResolution.Y, 1));
		settingsWriter.SetUInt("outputResolutionX", (uint)Math.Max(config.OutputResolution.X, 1));
		settingsWriter.SetUInt("outputResolutionY", (uint)Math.Max(config.OutputResolution.Y, 1));
		settingsWriter.SetUInt("sliceCount", (uint)config.SliceCount);
		settingsWriter.SetUInt("stepCount", (uint)config.StepCount);
		settingsWriter.SetFloat("radius", config.Radius);
		settingsWriter.SetFloat("thickness", config.Thickness);
		settingsWriter.SetFloat("bias", config.Bias);
		settingsWriter.SetFloat("strength", config.Strength);
		settingsWriter.SetFloat("power", config.Power);
		settingsWriter.SetFloat("invProjScaleX", sceneData.InverseProjection.M11);
		settingsWriter.SetFloat("invProjScaleY", sceneData.InverseProjection.M22);
		settingsWriter.SetFloat("projZBias", projectionMatrix.M33);
		settingsWriter.SetFloat("projZScale", projectionMatrix.M43);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _visibilityBitmaskThreadGroupSize
			?? throw new InvalidOperationException("Ambient occlusion threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.OutputResolution.X, 1),
			(uint)Math.Max(config.OutputResolution.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private void RecordRayTraced(RenderGraphContext context, in AmbientOcclusionPassConfig config, SceneDrawData sceneData)
	{
		if (config.TopLevelAccelerationStructure is null)
		{
			throw new InvalidOperationException("Ray traced ambient occlusion requires a valid top-level acceleration structure.");
		}

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _rayTracedBindlessWriter
			?? throw new InvalidOperationException("Ray traced ambient occlusion bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.DepthHandle.Value);
		bindlessWriter.SetUInt("normalHandle", config.NormalHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		bindlessWriter.SetUInt("hitMaskHandle", config.RayTracingHitMaskHandle.Value);
		bindlessWriter.SetUInt("hitDistanceHandle", config.RayTracingHitDistanceHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var cameraWriter = _rayTracedCameraWriter
			?? throw new InvalidOperationException("Ray traced ambient occlusion camera writer was not initialized.");
		cameraWriter.Clear();
		cameraWriter.SetMatrix4x4("camera.invProjection", sceneData.InverseProjection);
		cameraWriter.SetMatrix4x4("camera.invViewProjection", sceneData.InverseViewProjection);
		cameraWriter.SetVector3("camera.cameraOrigin", sceneData.CameraOrigin);
		cameraWriter.SetMatrix4x4("camera.viewMatrix", sceneData.ViewMatrix);
		cameraWriter.SetFloat("camera.nearPlane", sceneData.NearPlane);
		cameraWriter.SetFloat("camera.farPlane", sceneData.FarPlane);
		cameraWriter.SetUInt("camera.frameSizeX", (uint)Math.Max(sceneData.SceneFramebufferSize.X, 1));
		cameraWriter.SetUInt("camera.frameSizeY", (uint)Math.Max(sceneData.SceneFramebufferSize.Y, 1));
		commandList.SetComputeConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());

		var settingsWriter = _rayTracedSettingsWriter
			?? throw new InvalidOperationException("Ray traced ambient occlusion settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetUInt("fullResolutionX", (uint)Math.Max(config.FullResolution.X, 1));
		settingsWriter.SetUInt("fullResolutionY", (uint)Math.Max(config.FullResolution.Y, 1));
		settingsWriter.SetUInt("outputResolutionX", (uint)Math.Max(config.OutputResolution.X, 1));
		settingsWriter.SetUInt("outputResolutionY", (uint)Math.Max(config.OutputResolution.Y, 1));
		settingsWriter.SetFloat("radius", config.Radius);
		settingsWriter.SetFloat("bias", config.Bias);
		settingsWriter.SetFloat("strength", config.Strength);
		settingsWriter.SetUInt("frameIndex", config.FrameIndex);
		settingsWriter.SetUInt("debugOutputsEnabled", config.RayTracingHitMaskHandle.IsValid && config.RayTracingHitDistanceHandle.IsValid ? 1u : 0u);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());
		commandList.SynchronizeAccelerationStructureBuildForComputeRead(config.TopLevelAccelerationStructure);
		commandList.SetComputeAccelerationStructure(3, config.TopLevelAccelerationStructure);

		var threadGroupSize = _rayTracedThreadGroupSize
			?? throw new InvalidOperationException("Ray traced ambient occlusion threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.OutputResolution.X, 1),
			(uint)Math.Max(config.OutputResolution.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private IGfxPipeline EnsureVisibilityBitmaskPipeline(IGfxDevice device)
	{
		if (_visibilityBitmaskPipeline is not null)
		{
			if (_visibilityBitmaskCompiledBackendKind.HasValue && _visibilityBitmaskCompiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"AmbientOcclusionPass visibility-bitmask pipeline is already compiled for backend '{_visibilityBitmaskCompiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return _visibilityBitmaskPipeline;
		}

		EnsureVisibilityBitmaskReflectionWriters(device.BackendKind);

		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "AmbientOcclusionVisibilityBitmaskCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "ao_vbao.compute.slang");
		_visibilityBitmaskPipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _visibilityBitmaskComputeShader, computeThreadGroupSize: _visibilityBitmaskThreadGroupSize));
		return _visibilityBitmaskPipeline;
	}

	private IGfxPipeline EnsureRayTracedPipeline(IGfxDevice device)
	{
		if (_rayTracedPipeline is not null)
		{
			if (_rayTracedCompiledBackendKind.HasValue && _rayTracedCompiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"AmbientOcclusionPass ray-traced pipeline is already compiled for backend '{_rayTracedCompiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return _rayTracedPipeline;
		}

		if (device.BackendKind != GraphicsBackendKind.Metal)
		{
			throw new NotImplementedException("Ray traced ambient occlusion is currently implemented for Metal only.");
		}

		EnsureRayTracedReflectionWriters(device.BackendKind);

		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "AmbientOcclusionRayTracedCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "ao_rtao.compute.slang");
		_rayTracedPipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _rayTracedComputeShader, computeThreadGroupSize: _rayTracedThreadGroupSize));
		return _rayTracedPipeline;
	}

	private void EnsureVisibilityBitmaskReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_visibilityBitmaskCompiledBackendKind.HasValue &&
		    _visibilityBitmaskCompiledBackendKind.Value == backendKind &&
		    _visibilityBitmaskComputeShader.IsEmpty == false &&
		    _visibilityBitmaskThreadGroupSize.HasValue &&
		    _bindlessWriter is not null &&
		    _cameraWriter is not null &&
		    _settingsWriter is not null)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.AmbientOcclusionVbao,
			"AmbientOcclusionVisibilityBitmaskCS",
			backendKind);

		_visibilityBitmaskComputeShader = compiled.Bytecode;
		_visibilityBitmaskThreadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_cameraWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("CameraParams"));
		_settingsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("AoSettings"));
		_visibilityBitmaskCompiledBackendKind = backendKind;
	}

	private void EnsureRayTracedReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_rayTracedCompiledBackendKind.HasValue &&
		    _rayTracedCompiledBackendKind.Value == backendKind &&
		    _rayTracedComputeShader.IsEmpty == false &&
		    _rayTracedThreadGroupSize.HasValue &&
		    _rayTracedBindlessWriter is not null &&
		    _rayTracedCameraWriter is not null &&
		    _rayTracedSettingsWriter is not null)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.AmbientOcclusionRayTraced,
			"AmbientOcclusionRayTracedCS",
			backendKind);

		_rayTracedComputeShader = compiled.Bytecode;
		_rayTracedThreadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_rayTracedBindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_rayTracedCameraWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("CameraParams"));
		_rayTracedSettingsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("AoSettings"));
		_rayTracedCompiledBackendKind = backendKind;
	}
}
