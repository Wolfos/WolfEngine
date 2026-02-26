
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic deferred lighting compute pass that shades the G-buffer into the output texture.
/// </summary>
public sealed class DeferredLightingPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _shadowSampler = DescriptorHandle.Invalid;
	private const int MaxLights = 3;
	private const int LightingConstantsCount = 104;

	public DeferredLightingPass(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public DeferredLightingPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		ShadowFrameData shadowData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		if (_linearSampler.IsValid == false)
		{
			var sampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
			_linearSampler = _bindlessRegistry.GetSamplerHandle(sampler);
		}
		if (_shadowSampler.IsValid == false)
		{
			var shadowSampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
			_shadowSampler = _bindlessRegistry.GetSamplerHandle(shadowSampler);
		}

		var albedo = context.GetTexture(resources.GBufferAlbedo);
		var normal = context.GetTexture(resources.GBufferNormal);
		var material = context.GetTexture(resources.GBufferMaterial);
		var emissive = context.GetTexture(resources.GBufferEmissive);
		var depth = context.GetTexture(resources.GBufferDepth);
		var shadowMapDepth0 = context.GetTexture(resources.ShadowMapDepth0);
		var shadowMapDepth1 = context.GetTexture(resources.ShadowMapDepth1);
		var shadowMapDepth2 = context.GetTexture(resources.ShadowMapDepth2);

		var environment = resources.SkyboxEnvironment.IsValid
			? context.GetTexture(resources.SkyboxEnvironment)
			: emissive;
		var irradiance = resources.SkyboxIrradiance.IsValid
			? context.GetTexture(resources.SkyboxIrradiance)
			: emissive;
		var prefilter = resources.SkyboxPrefilter.IsValid
			? context.GetTexture(resources.SkyboxPrefilter)
			: emissive;
		var brdfLut = resources.SkyboxBrdfLut.IsValid
			? context.GetTexture(resources.SkyboxBrdfLut)
			: emissive;

		var lighting = context.GetTexture(resources.LightingBuffer);
		var shadowResolution = Math.Max(1, shadowData.MapResolution);

		return new DeferredLightingPassConfig
		{
			Pipeline = pipeline,
			GBufferAlbedo = _bindlessRegistry.GetTextureHandle(albedo),
			GBufferNormal = _bindlessRegistry.GetTextureHandle(normal),
			GBufferMaterial = _bindlessRegistry.GetTextureHandle(material),
			GBufferEmissive = _bindlessRegistry.GetTextureHandle(emissive),
			GBufferDepth = _bindlessRegistry.GetTextureHandle(depth),
			ShadowMapDepth0 = _bindlessRegistry.RegisterDepthTexture(shadowMapDepth0),
			ShadowMapDepth1 = _bindlessRegistry.RegisterDepthTexture(shadowMapDepth1),
			ShadowMapDepth2 = _bindlessRegistry.RegisterDepthTexture(shadowMapDepth2),
			SkyboxEnvironment = _bindlessRegistry.GetTextureHandle(environment),
			SkyboxIrradiance = _bindlessRegistry.GetTextureHandle(irradiance),
			SkyboxPrefilter = _bindlessRegistry.GetTextureHandle(prefilter),
			SkyboxBrdfLut = _bindlessRegistry.GetTextureHandle(brdfLut),
			LightingOutput = _bindlessRegistry.RegisterRwTexture(lighting),
			LinearSampler = _linearSampler,
			ShadowSampler = _shadowSampler,
			ShadowViewProjection0 = shadowData.CascadeViewProjection0,
			ShadowViewProjection1 = shadowData.CascadeViewProjection1,
			ShadowViewProjection2 = shadowData.CascadeViewProjection2,
			ShadowSplit0 = shadowData.CascadeSplit0,
			ShadowSplit1 = shadowData.CascadeSplit1,
			ShadowSplit2 = shadowData.CascadeSplit2,
			ShadowCascadeBlendDistance = shadowData.CascadeBlendDistance,
			ShadowedDirectionalLightIndex = shadowData.ShadowedDirectionalLightIndex,
			ShadowDepthBias = shadowData.DepthBias,
			ShadowStrength = shadowData.Strength,
			ShadowsEnabled = shadowData.Enabled,
			ShadowTexelSizeX = 1.0f / shadowResolution,
			ShadowTexelSizeY = 1.0f / shadowResolution,
			DispatchSize = resources.SceneFramebufferSize
		};
	}

	public unsafe void Record(RenderGraphContext context, ref DeferredLightingPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;

		// Bind pipeline
		commandList.BindPipeline(config.Pipeline);

		Span<uint> textureHandles = stackalloc uint[15];
		textureHandles[0] = config.GBufferAlbedo.Value;
		textureHandles[1] = config.GBufferNormal.Value;
		textureHandles[2] = config.GBufferMaterial.Value;
		textureHandles[3] = config.GBufferEmissive.Value;
		textureHandles[4] = config.GBufferDepth.Value;
		textureHandles[5] = config.SkyboxEnvironment.Value;
		textureHandles[6] = config.SkyboxIrradiance.Value;
		textureHandles[7] = config.SkyboxPrefilter.Value;
		textureHandles[8] = config.SkyboxBrdfLut.Value;
		textureHandles[9] = config.LightingOutput.Value;
		textureHandles[10] = config.LinearSampler.Value;
		textureHandles[11] = config.ShadowMapDepth0.Value;
		textureHandles[12] = config.ShadowMapDepth1.Value;
		textureHandles[13] = config.ShadowMapDepth2.Value;
		textureHandles[14] = config.ShadowSampler.Value;
		commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(textureHandles));

		// Set camera constants (Root parameter 1)
		Span<float> cameraConstants = stackalloc float[40];
		WriteMatrix(cameraConstants, sceneData.InverseProjection);
		WriteMatrix(cameraConstants.Slice(16), sceneData.InverseViewProjection);
		cameraConstants[32] = sceneData.CameraOrigin.X;
		cameraConstants[33] = sceneData.CameraOrigin.Y;
		cameraConstants[34] = sceneData.CameraOrigin.Z;
		cameraConstants[35] = 0.0f;
		cameraConstants[36] = 0.0f;
		cameraConstants[37] = 0.0f;
		cameraConstants[38] = 0.0f;
		cameraConstants[39] = 0.0f;
		commandList.SetComputeConstants(1, MemoryMarshal.AsBytes(cameraConstants));

		Span<ShaderLight> shaderLights = stackalloc ShaderLight[MaxLights];
		shaderLights.Clear();
		var lightCountInt = Math.Min(sceneData.Lights.Count, MaxLights);
		for (var i = 0; i < lightCountInt; i++)
		{
			var packet = sceneData.Lights[i];
			var light = packet.Light;
			
			var forward = Vector3.TransformNormal(Vector3.UnitZ, packet.Transform);
			if (forward == Vector3.Zero)
			{
				forward = new Vector3(0, -1, 0);
			}
			
			var position = packet.Transform.Translation;

			shaderLights[i] = new ShaderLight
			{
				ColorIntensity = new Vector4(light.Color.X, light.Color.Y, light.Color.Z, light.Intensity),
				DirectionType = new Vector4(forward, (float) light.Type),
				PositionRange = new Vector4(position, 25.0f) // TODO: light range from component when available
			};
		}

		Span<uint> lightingConstants = stackalloc uint[LightingConstantsCount];
		lightingConstants.Clear();
		lightingConstants[0] = (uint)lightCountInt;
		var lightWords = MemoryMarshal.Cast<ShaderLight, uint>(shaderLights);
		lightWords.CopyTo(lightingConstants.Slice(8));

		WriteMatrix(MemoryMarshal.Cast<uint, float>(lightingConstants.Slice(44, 16)), config.ShadowViewProjection0);
		WriteMatrix(MemoryMarshal.Cast<uint, float>(lightingConstants.Slice(60, 16)), config.ShadowViewProjection1);
		WriteMatrix(MemoryMarshal.Cast<uint, float>(lightingConstants.Slice(76, 16)), config.ShadowViewProjection2);
		lightingConstants[92] = BitConverter.SingleToUInt32Bits(config.ShadowSplit0);
		lightingConstants[93] = BitConverter.SingleToUInt32Bits(config.ShadowSplit1);
		lightingConstants[94] = BitConverter.SingleToUInt32Bits(config.ShadowSplit2);
		lightingConstants[95] = BitConverter.SingleToUInt32Bits(config.ShadowCascadeBlendDistance);
		lightingConstants[96] = BitConverter.SingleToUInt32Bits(config.ShadowTexelSizeX);
		lightingConstants[97] = BitConverter.SingleToUInt32Bits(config.ShadowTexelSizeY);
		lightingConstants[98] = BitConverter.SingleToUInt32Bits(config.ShadowDepthBias);
		lightingConstants[99] = BitConverter.SingleToUInt32Bits(config.ShadowStrength);
		lightingConstants[100] = config.ShadowsEnabled ? (uint)Math.Max(config.ShadowedDirectionalLightIndex, 0) : 0;
		lightingConstants[101] = config.ShadowsEnabled ? 1u : 0u;
		lightingConstants[102] = BitConverter.SingleToUInt32Bits(ShadowMapPass.MaxShadowDistance);
		lightingConstants[103] = 0;
		commandList.SetComputeConstants(2, MemoryMarshal.AsBytes(lightingConstants));

		// Dispatch the compute shader
		var dispatchX = (uint)((config.DispatchSize.X + 7) / 8);
		var dispatchY = (uint)((config.DispatchSize.Y + 7) / 8);
		commandList.Dispatch(dispatchX, dispatchY, 1);
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			return _pipeline;
		}

		if (_computeShader.IsEmpty)
		{
			if (device.BackendKind == GraphicsBackendKind.Metal)
			{
				var source = _shaderCompiler.GetMetalComputeSource("deferred_lighting.compute.slang", "CSMain");
				_computeShader = Encoding.UTF8.GetBytes(source);
			}
			else
			{
				_computeShader = _shaderCompiler.GetComputeShader("deferred_lighting.compute.slang", "CSMain");
			}
		}

		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "CSMain",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default);

		var shaderSet = new ShaderBytecodeSet(compute: _computeShader);
		_pipeline = device.GetOrCreatePipeline(pipelineKey, shaderSet);
		return _pipeline;
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

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	private struct ShaderLight
	{
		public Vector4 ColorIntensity;
		public Vector4 DirectionType;
		public Vector4 PositionRange;
	}
}
