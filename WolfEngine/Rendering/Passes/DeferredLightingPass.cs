
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.Metal;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic deferred lighting compute pass that shades the G-buffer into the output texture.
/// </summary>
public sealed class DeferredLightingPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private IGfxPipeline _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private const int MaxLights = 3;

	public DeferredLightingPass(IShaderCompiler shaderCompiler)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
	}

	public DeferredLightingPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);

		if (_linearSampler.IsValid == false)
		{
			var sampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
			_linearSampler = device.GlobalTable.AllocateSampler(sampler);
		}

		var albedo = context.GetTexture(resources.GBufferAlbedo);
		var normal = context.GetTexture(resources.GBufferNormal);
		var material = context.GetTexture(resources.GBufferMaterial);
		var emissive = context.GetTexture(resources.GBufferEmissive);
		var depth = context.GetTexture(resources.GBufferDepth);

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

		return new DeferredLightingPassConfig
		{
			Pipeline = pipeline,
			GBufferAlbedo = albedo.ShaderResourceView,
			GBufferNormal = normal.ShaderResourceView,
			GBufferMaterial = material.ShaderResourceView,
			GBufferEmissive = emissive.ShaderResourceView,
			GBufferDepth = depth.ShaderResourceView,
			SkyboxEnvironment = environment.ShaderResourceView,
			SkyboxIrradiance = irradiance.ShaderResourceView,
			SkyboxPrefilter = prefilter.ShaderResourceView,
			SkyboxBrdfLut = brdfLut.ShaderResourceView,
			LightingOutput = lighting.UnorderedAccessView,
			LinearSampler = _linearSampler,
			DispatchSize = resources.FramebufferSize
		};
	}

	public unsafe void Record(RenderGraphContext context, ref DeferredLightingPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;

		// Bind pipeline
		commandList.BindPipeline(config.Pipeline);

		Span<uint> textureHandles = stackalloc uint[12];
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
		textureHandles[11] = 0;
		commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(textureHandles));

		// Set camera constants (Root parameter 1)
		var cameraConstantCount = commandList is MetalCommandList ? 40 : 36;
		Span<float> cameraConstants = stackalloc float[cameraConstantCount];
		WriteMatrix(cameraConstants, sceneData.InverseProjection);
		WriteMatrix(cameraConstants.Slice(16), sceneData.InverseViewProjection);
		cameraConstants[32] = sceneData.CameraOrigin.X;
		cameraConstants[33] = sceneData.CameraOrigin.Y;
		cameraConstants[34] = sceneData.CameraOrigin.Z;
		cameraConstants[35] = 0.0f;
		for (var i = 36; i < cameraConstants.Length; i++)
		{
			cameraConstants[i] = 0.0f;
		}
		commandList.SetComputeConstants(1, MemoryMarshal.AsBytes(cameraConstants));

		Span<ShaderLight> shaderLights = stackalloc ShaderLight[MaxLights];
		shaderLights.Clear();
		var lightCountInt = Math.Min(sceneData.Lights.Count, MaxLights);
		for (var i = 0; i < lightCountInt; i++)
		{
			var packet = sceneData.Lights[i];
			var light = packet.Light;
			
			var forward = Vector3.Transform(-Vector3.UnitZ, packet.Transform);
			if (forward == Vector3.Zero)
			{
				forward = new Vector3(0, -1, 0);
			}
			forward = Vector3.Normalize(forward);

			var position = packet.Transform.Translation;

			shaderLights[i] = new ShaderLight
			{
				ColorIntensity = new Vector4(light.Color.X, light.Color.Y, light.Color.Z, light.Intensity),
				DirectionType = new Vector4(forward, (float) light.Type),
				PositionRange = new Vector4(position, 25.0f) // TODO: light range from component when available
			};
		}

		var lightBytes = MemoryMarshal.AsBytes(shaderLights);
		const int headerSize = 32; // uint + float3 padding, float3 aligns to 16 in MSL.
		var lightingConstantsSize = headerSize + lightBytes.Length;
		Span<byte> lightingConstants = stackalloc byte[lightingConstantsSize];
		lightingConstants.Clear();
		var lightCount = (uint)lightCountInt;
		MemoryMarshal.Write(lightingConstants, ref lightCount);
		lightBytes.CopyTo(lightingConstants.Slice(headerSize));
		commandList.SetComputeConstants(2, lightingConstants);

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
			if (device is MetalDevice)
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
