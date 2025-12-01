
using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic deferred lighting compute pass that shades the G-buffer into the output texture.
/// </summary>
public sealed class DeferredLightingPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private IGfxPipeline _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private IGfxDescriptorSet? _descriptorSet;
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

		_descriptorSet?.Dispose();
		_descriptorSet = null;

		var srvTableBuilder = device.CreateDescriptorSetBuilder();
		srvTableBuilder.AddShaderResource(0, context.GetTexture(resources.GBufferAlbedo));
		srvTableBuilder.AddShaderResource(1, context.GetTexture(resources.GBufferNormal));
		srvTableBuilder.AddShaderResource(2, context.GetTexture(resources.GBufferMaterial));
		srvTableBuilder.AddShaderResource(3, context.GetTexture(resources.GBufferEmissive));
		srvTableBuilder.AddShaderResource(4, context.GetTexture(resources.GBufferDepth));

		// Environment map for reflections. If we don't have one, fall back to emissive so the slot stays valid.
		if (resources.SkyboxEnvironment.IsValid)
		{
			srvTableBuilder.AddShaderResource(5, context.GetTexture(resources.SkyboxEnvironment));
		}
		else
		{
			srvTableBuilder.AddShaderResource(5, context.GetTexture(resources.GBufferEmissive));
		}

		srvTableBuilder.AddUnorderedAccess(6, context.GetTexture(resources.LightingBuffer));
		var descriptorSet = srvTableBuilder.Build();
		_descriptorSet = descriptorSet;

		return new DeferredLightingPassConfig
		{
			Pipeline = pipeline,
			DescriptorSet = descriptorSet,
			DispatchSize = resources.FramebufferSize
		};
	}

	public unsafe void Record(RenderGraphContext context, ref DeferredLightingPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;

		// Bind pipeline
		commandList.BindPipeline(config.Pipeline);

		// Bind descriptor sets for SRVs and UAVs
		commandList.BindComputeDescriptorSet(0, config.DescriptorSet);

		// Set camera constants (Root parameter 1)
		Span<float> cameraConstants = stackalloc float[20];
		WriteMatrix(cameraConstants, sceneData.InverseProjection);
		cameraConstants[16] = sceneData.CameraOrigin.X;
		cameraConstants[17] = sceneData.CameraOrigin.Y;
		cameraConstants[18] = sceneData.CameraOrigin.Z;
		cameraConstants[19] = 1.0f;
		commandList.SetComputeConstants(1, MemoryMarshal.AsBytes(cameraConstants));

		Span<ShaderLight> shaderLights = stackalloc ShaderLight[MaxLights];
		var lightCount = Math.Min(sceneData.Lights.Count, MaxLights);
		for (var i = 0; i < lightCount; i++)
		{
			var packet = sceneData.Lights[i];
			var light = packet.Light;
			var rotation = packet.Transform.LocalRotation;
			var forward = Vector3.Transform(-Vector3.UnitZ, rotation);
			if (forward == Vector3.Zero)
			{
				forward = new Vector3(0, -1, 0);
			}
			forward = Vector3.Normalize(forward);

			var position = packet.Transform.LocalPosition;

			shaderLights[i] = new ShaderLight
			{
				ColorIntensity = new Vector4(light.Color.X, light.Color.Y, light.Color.Z, light.Intensity),
				DirectionType = new Vector4(forward, (float) light.Type),
				PositionRange = new Vector4(position, 25.0f) // TODO: light range from component when available
			};
		}

		Span<byte> lightingConstants = stackalloc byte[16 + MaxLights * Marshal.SizeOf<ShaderLight>()];
		MemoryMarshal.Write(lightingConstants, ref lightCount);
		var lightBytes = MemoryMarshal.AsBytes(shaderLights[..lightCount]);
		lightBytes.CopyTo(lightingConstants.Slice(16));
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

		_computeShader = _computeShader.IsEmpty
			? _shaderCompiler.GetComputeShader("deferred_lighting.compute.slang", "CSMain")
			: _computeShader;

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

	private readonly struct ShaderLight
	{
		public Vector4 ColorIntensity { get; init; }
		public Vector4 DirectionType { get; init; }
		public Vector4 PositionRange { get; init; }
	}
}
