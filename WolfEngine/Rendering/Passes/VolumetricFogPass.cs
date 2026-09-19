using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public enum VolumetricFogStage
{
	Inject,
	Temporal,
	Integrate
}

public readonly struct VolumetricFogPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle Current { get; init; }
	public required DescriptorHandle History { get; init; }
	public required DescriptorHandle Output { get; init; }
	public required DescriptorHandle Irradiance { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required DescriptorHandle ShadowMap0 { get; init; }
	public required DescriptorHandle ShadowMap1 { get; init; }
	public required DescriptorHandle ShadowMap2 { get; init; }
	public required DescriptorHandle ShadowSampler { get; init; }
	public required IGfxBuffer VolumeBuffer { get; init; }
	public required IGfxBuffer CellHeaderBuffer { get; init; }
	public required IGfxBuffer VolumeIndexBuffer { get; init; }
	public required Int3 Grid { get; init; }
	public required Int3 CellGrid { get; init; }
	public required int VolumeCount { get; init; }
	public required bool HistoryValid { get; init; }
	public required Matrix4x4 InverseUnjitteredViewProjection { get; init; }
	public required uint FrameIndex { get; init; }
	public required Vector4 LightColorIntensity { get; init; }
	public required Vector4 LightDirectionEnabled { get; init; }
	public required ShadowFrameData ShadowData { get; init; }
	public required VolumetricFogConfig Settings { get; init; }
}

public sealed class VolumetricFogPass
{
	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly IGfxPipeline?[] _pipelines = new IGfxPipeline?[3];
	private readonly ReadOnlyMemory<byte>[] _shaders = new ReadOnlyMemory<byte>[3];
	private readonly ComputeThreadGroupSize?[] _threadGroups = new ComputeThreadGroupSize?[3];
	private GraphicsBackendKind? _backend;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _fogWriter;
	private ShaderPropertyWriter? _lightingWriter;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _shadowSampler = DescriptorHandle.Invalid;
	private uint _frameIndex;
	private FrameState _frame;
	private bool _framePrepared;

	private readonly record struct FrameState(
		Int3 Grid,
		Int3 CellGrid,
		int VolumeCount,
		bool HistoryValid,
		Matrix4x4 InverseUnjitteredViewProjection,
		uint FrameIndex,
		Vector4 LightColorIntensity,
		Vector4 LightDirectionEnabled,
		ShadowFrameData ShadowData,
		VolumetricFogConfig Settings);

	public VolumetricFogPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public static Int3 ComputeGrid(Int2 framebufferSize, in VolumetricFogConfig settings)
	{
		var pixelSize = Math.Clamp(settings.FroxelPixelSize, 4, 32);
		return new Int3(
			Math.Max(1, (framebufferSize.X + pixelSize - 1) / pixelSize),
			Math.Max(1, (framebufferSize.Y + pixelSize - 1) / pixelSize),
			Math.Clamp(settings.SliceCount, 16, 128));
	}

	/// <summary>
	/// Bins fog volumes, uploads them, and resolves frame-constant lighting once per frame.
	/// The three stage configs built afterwards all share this state.
	/// </summary>
	public void PrepareFrame(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		GpuDrawResources gpuDrawResources,
		ShadowFrameData shadowData,
		bool historyValid)
	{
		var fog = resources.Config.VolumetricFog;
		var grid = ComputeGrid(resources.SceneFramebufferSize, fog);
		var binning = VolumetricFogBinner.Build(context.SceneData, grid, fog.MaxDistance);
		gpuDrawResources.EnsureFogVolumeCapacity(device, binning.Volumes.Length, binning.CellHeaders.Length, binning.VolumeIndices.Length);
		Write(gpuDrawResources.FogVolumeBuffer, binning.Volumes);
		Write(gpuDrawResources.FogCellHeaderBuffer, binning.CellHeaders);
		Write(gpuDrawResources.FogVolumeIndexBuffer, binning.VolumeIndices);

		var (lightColorIntensity, lightDirectionEnabled) = SelectDirectionalLight(context.SceneData, shadowData);
		Matrix4x4.Invert(context.SceneData.UnjitteredViewProjection, out var inverseUnjitteredViewProjection);
		_frame = new FrameState(
			grid,
			binning.CellGrid,
			binning.Volumes.Length,
			historyValid && !context.SceneData.ResetHistory,
			inverseUnjitteredViewProjection,
			_frameIndex++,
			lightColorIntensity,
			lightDirectionEnabled,
			shadowData,
			fog);
		_framePrepared = true;
	}

	public VolumetricFogPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		GpuDrawResources gpuDrawResources,
		VolumetricFogStage stage)
	{
		if (!_framePrepared) throw new InvalidOperationException("VolumetricFogPass.PrepareFrame must run before building stage configs.");
		var pipeline = EnsurePipeline(device, stage);
		_bindlessRegistry.EnsureInitialized(device);
		_linearSampler = EnsureSampler(_linearSampler, FilterMode.Trilinear);
		_shadowSampler = EnsureSampler(_shadowSampler, FilterMode.Bilinear);

		// Inject writes Current; Temporal reads Current + HistoryRead into HistoryWrite;
		// Integrate reads the temporally filtered HistoryWrite into Integrated.
		var currentTexture = context.GetTexture(stage == VolumetricFogStage.Integrate
			? resources.FogHistoryWrite
			: resources.FogCurrent);
		var historyTexture = context.GetTexture(resources.FogHistoryRead);
		var outputTexture = context.GetTexture(stage switch
		{
			VolumetricFogStage.Inject => resources.FogCurrent,
			VolumetricFogStage.Temporal => resources.FogHistoryWrite,
			_ => resources.FogIntegrated
		});
		var irradiance = context.GetTexture(resources.SkyboxIrradiance);
		var frame = _frame;
		return new VolumetricFogPassConfig
		{
			Pipeline = pipeline,
			Current = _bindlessRegistry.GetTextureHandle(currentTexture),
			History = _bindlessRegistry.GetTextureHandle(historyTexture),
			Output = _bindlessRegistry.RegisterRwTexture(outputTexture),
			Irradiance = _bindlessRegistry.GetTextureHandle(irradiance),
			LinearSampler = _linearSampler,
			ShadowMap0 = _bindlessRegistry.RegisterDepthTexture(context.GetTexture(resources.ShadowMapDepth0)),
			ShadowMap1 = _bindlessRegistry.RegisterDepthTexture(context.GetTexture(resources.ShadowMapDepth1)),
			ShadowMap2 = _bindlessRegistry.RegisterDepthTexture(context.GetTexture(resources.ShadowMapDepth2)),
			ShadowSampler = _shadowSampler,
			VolumeBuffer = gpuDrawResources.FogVolumeBuffer!,
			CellHeaderBuffer = gpuDrawResources.FogCellHeaderBuffer!,
			VolumeIndexBuffer = gpuDrawResources.FogVolumeIndexBuffer!,
			Grid = frame.Grid,
			CellGrid = frame.CellGrid,
			VolumeCount = frame.VolumeCount,
			HistoryValid = frame.HistoryValid,
			InverseUnjitteredViewProjection = frame.InverseUnjitteredViewProjection,
			FrameIndex = frame.FrameIndex,
			LightColorIntensity = frame.LightColorIntensity,
			LightDirectionEnabled = frame.LightDirectionEnabled,
			ShadowData = frame.ShadowData,
			Settings = frame.Settings
		};
	}

	/// <summary>
	/// Picks the shadowed directional light when there is one, otherwise the first directional light.
	/// </summary>
	internal static (Vector4 ColorIntensity, Vector4 DirectionEnabled) SelectDirectionalLight(
		SceneDrawData sceneData,
		ShadowFrameData shadowData)
	{
		var targetIndex = shadowData.ShadowedDirectionalLightIndex;
		var directionalIndex = -1;
		for (var i = 0; i < sceneData.Lights.Count; i++)
		{
			var packet = sceneData.Lights[i];
			if (packet.Light.Type != LightType.Directional) continue;
			directionalIndex++;
			if (targetIndex >= 0 && directionalIndex != targetIndex) continue;
			var forward = Vector3.TransformNormal(Vector3.UnitZ, packet.Transform);
			if (forward == Vector3.Zero) forward = new Vector3(0, -1, 0);
			forward = Vector3.Normalize(forward);
			var scale = DirectionalLightUtility.GetIntensityScale(packet.Light, forward);
			return (
				new Vector4(packet.Light.Color.R, packet.Light.Color.G, packet.Light.Color.B, packet.Light.Intensity * scale),
				new Vector4(forward, 1.0f));
		}
		return (Vector4.Zero, Vector4.Zero);
	}

	public void Record(RenderGraphContext context, VolumetricFogStage stage, in VolumetricFogPassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);
		var bindless = _bindlessWriter!;
		bindless.Clear();
		bindless.SetUInt("currentHandle", config.Current.Value);
		bindless.SetUInt("historyHandle", config.History.Value);
		bindless.SetUInt("outputHandle", config.Output.Value);
		bindless.SetUInt("irradianceHandle", config.Irradiance.Value);
		bindless.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		bindless.SetUInt("shadowMapHandle0", config.ShadowMap0.Value);
		bindless.SetUInt("shadowMapHandle1", config.ShadowMap1.Value);
		bindless.SetUInt("shadowMapHandle2", config.ShadowMap2.Value);
		bindless.SetUInt("shadowSamplerHandle", config.ShadowSampler.Value);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());

		var scene = context.SceneData;
		var fog = config.Settings;
		var parameters = _fogWriter!;
		parameters.Clear();
		parameters.SetMatrix4x4("inverseUnjitteredViewProjection", config.InverseUnjitteredViewProjection);
		parameters.SetMatrix4x4("viewMatrix", scene.ViewMatrix);
		parameters.SetMatrix4x4("previousViewProjection", scene.PreviousViewProjection);
		parameters.SetVector3("cameraOrigin", scene.CameraOrigin);
		parameters.SetFloat("extinction", Math.Max(fog.Extinction, 0.0f));
		parameters.SetVector3("previousCameraOrigin", scene.PreviousCameraOrigin);
		parameters.SetFloat("baseHeight", fog.BaseHeight);
		parameters.SetVector3("albedo", Vector3.Max(fog.Albedo, Vector3.Zero));
		parameters.SetFloat("heightFalloff", Math.Max(fog.HeightFalloff, 0.0f));
		parameters.SetUInt("gridSizeX", (uint)config.Grid.X);
		parameters.SetUInt("gridSizeY", (uint)config.Grid.Y);
		parameters.SetUInt("gridSizeZ", (uint)config.Grid.Z);
		parameters.SetUInt("frameIndex", config.FrameIndex);
		parameters.SetUInt("cellGridSizeX", (uint)config.CellGrid.X);
		parameters.SetUInt("cellGridSizeY", (uint)config.CellGrid.Y);
		parameters.SetUInt("cellGridSizeZ", (uint)config.CellGrid.Z);
		parameters.SetUInt("historyValid", config.HistoryValid ? 1u : 0u);
		parameters.SetUInt("volumeCount", (uint)config.VolumeCount);
		parameters.SetFloat("anisotropy", Math.Clamp(fog.Anisotropy, -0.95f, 0.95f));
		parameters.SetFloat("nearPlane", Math.Max(scene.NearPlane, 0.001f));
		parameters.SetFloat("maxDistance", Math.Max(fog.MaxDistance, scene.NearPlane + 0.001f));
		parameters.SetFloat("historyWeight", Math.Clamp(fog.HistoryWeight, 0.0f, 0.999f));
		commandList.SetComputeConstants(parameters.RegisterIndex, parameters.AsBytes());

		var shadow = config.ShadowData;
		var lighting = _lightingWriter!;
		lighting.Clear();
		lighting.SetVector4("lightColorIntensity", config.LightColorIntensity);
		lighting.SetVector4("lightDirectionEnabled", config.LightDirectionEnabled);
		lighting.SetMatrix4x4("shadowViewProjection0", shadow.CascadeViewProjection0);
		lighting.SetMatrix4x4("shadowViewProjection1", shadow.CascadeViewProjection1);
		lighting.SetMatrix4x4("shadowViewProjection2", shadow.CascadeViewProjection2);
		lighting.SetVector4("shadowSplitsBlend", new Vector4(shadow.CascadeSplit0, shadow.CascadeSplit1, shadow.CascadeSplit2, shadow.CascadeBlendDistance));
		var resolution = Math.Max(shadow.MapResolution, 1);
		lighting.SetVector4("shadowTexelSizeEnabled", new Vector4(1.0f / resolution, 1.0f / resolution, shadow.Enabled ? 1.0f : 0.0f, shadow.Strength));
		lighting.SetVector4("shadowDepthBiases", new Vector4(shadow.DepthBiases, 0.0f));
		lighting.SetFloat("shadowMaxDistance", shadow.MaxDistance);
		commandList.SetComputeConstants(lighting.RegisterIndex, lighting.AsBytes());
		commandList.SetComputeReadOnlyBuffer(10, config.VolumeBuffer);
		commandList.SetComputeReadOnlyBuffer(11, config.CellHeaderBuffer);
		commandList.SetComputeReadOnlyBuffer(12, config.VolumeIndexBuffer);
		var group = _threadGroups[(int)stage]!.Value;
		var (x, y, z) = group.GetDispatchGroupCount((uint)config.Grid.X, (uint)config.Grid.Y, stage == VolumetricFogStage.Integrate ? 1u : (uint)config.Grid.Z);
		commandList.Dispatch(x, y, z);
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device, VolumetricFogStage stage)
	{
		if (_backend.HasValue && _backend != device.BackendKind) throw new InvalidOperationException("Volumetric fog backend changed after pipeline compilation.");
		var index = (int)stage;
		if (_pipelines[index] is not null) return _pipelines[index]!;
		var entry = stage switch { VolumetricFogStage.Inject => "VolumetricFogInjectCS", VolumetricFogStage.Temporal => "VolumetricFogTemporalCS", _ => "VolumetricFogIntegrateCS" };
		var compiled = _shaderCompiler.GetComputeShaderWithReflection(EngineShaderPrograms.VolumetricFog, entry, device.BackendKind);
		_shaders[index] = compiled.Bytecode;
		_threadGroups[index] = compiled.ThreadGroupSize;
		_bindlessWriter ??= new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_fogWriter ??= new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("FogParams"));
		_lightingWriter ??= new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("FogLightingParams"));
		_backend = device.BackendKind;
		var key = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: entry,
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "volumetric_fog.compute.slang");
		return _pipelines[index] = device.GetOrCreatePipeline(key, new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: compiled.ThreadGroupSize));
	}

	private DescriptorHandle EnsureSampler(DescriptorHandle current, FilterMode filter) => current.IsValid
		? current
		: _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(filter, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));

	private static void Write<T>(IGfxBuffer? buffer, T[] values) where T : unmanaged
	{
		if (values.Length > 0 && buffer is IWritableGpuBuffer writable) writable.Write<T>(values);
	}
}
