using System.Runtime.Versioning;
using System.Text;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Platform;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("macos")]
internal sealed class MetalDevice : IGfxDevice, ITexturePoolDevice
{
	private readonly MTLDevice _device;
	private readonly MTLCommandQueue _commandQueue;
	private readonly MetalDescriptorTable _descriptorTable;
	private readonly Dictionary<PipelineKey, MetalPipeline> _pipelines = new();

	public MetalDevice(MTLDevice device)
	{
		_device = device;
		_commandQueue = _device.NewCommandQueue();
		if (_commandQueue.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to create Metal command queue.");
		}
		_descriptorTable = new MetalDescriptorTable(_device);
	}

	public IGfxDescriptorTable GlobalTable => _descriptorTable;

	public IGfxCommandList BeginGraphics()
	{
		return new MetalCommandList(_commandQueue, _descriptorTable);
	}

	public IGfxCommandList BeginCompute()
	{
		return new MetalCommandList(_commandQueue, _descriptorTable);
	}

	public void Submit(IGfxCommandList commandList)
	{
		if (commandList is not MetalCommandList metalCommandList)
		{
			throw new InvalidOperationException("Command list was not created by the Metal backend.");
		}

		metalCommandList.Commit();
	}

	public IGfxTexture CreateTexture(in TextureDescriptor descriptor)
	{
		if (descriptor.Width <= 0 || descriptor.Height <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(descriptor), "Textures must have positive dimensions.");
		}

		var textureDescriptor = new MTLTextureDescriptor();
		textureDescriptor.Width = (ulong)descriptor.Width;
		textureDescriptor.Height = (ulong)descriptor.Height;
		textureDescriptor.Depth = 1;
		textureDescriptor.MipmapLevelCount = 1;
		textureDescriptor.PixelFormat = ToPixelFormat(descriptor.Format);
		textureDescriptor.TextureType = MTLTextureType.Type2D;
		textureDescriptor.StorageMode = MTLStorageMode.Shared;
		textureDescriptor.Usage = ToUsage(descriptor.Usage);

		var texture = _device.NewTexture(textureDescriptor);
		textureDescriptor.Dispose();
		if (texture.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to create Metal texture.");
		}

		var metalTexture = new MetalTexture(null, descriptor, texture, _descriptorTable);
		var srvHandle = (descriptor.Usage & TextureUsage.ShaderResource) != 0
			? _descriptorTable.AllocateShaderResourceView(metalTexture)
			: DescriptorHandle.Invalid;

		var uavHandle = (descriptor.Usage & TextureUsage.UnorderedAccess) != 0
			? _descriptorTable.AllocateUnorderedAccessView(metalTexture)
			: DescriptorHandle.Invalid;

		metalTexture.SetHandles(srvHandle, uavHandle);
		return metalTexture;
	}

	public IGfxBuffer CreateBuffer(in BufferDescriptor descriptor)
	{
		var options = MTLResourceOptions.ResourceStorageModeShared;
		var buffer = _device.NewBuffer(descriptor.SizeInBytes, options);
		if (buffer.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to create Metal buffer.");
		}

		return new MetalBuffer(null, descriptor, buffer);
	}

	public IGfxPipeline GetOrCreatePipeline(PipelineKey key, in ShaderBytecodeSet shaders)
	{
		if (_pipelines.TryGetValue(key, out var cached))
		{
			return cached;
		}

		if (key.PassKind == PassKind.Compute)
		{
			if (shaders.Compute is null)
			{
				throw new InvalidOperationException("Compute shader source was not provided.");
			}

			var computeLibrary = CreateLibraryFromSource(shaders.Compute.Value);
			var function = computeLibrary.NewFunction(NSStringHelper.From(key.ComputeEntryPoint ?? "CSMain"));
			var computeTextureEncoder = function.NewArgumentEncoder((ulong)MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
			var computeRwTextureEncoder = function.NewArgumentEncoder((ulong)MetalDescriptorTable.BindlessArgumentBufferIndexRWTextures);
			var computeSamplerEncoder = function.NewArgumentEncoder((ulong)MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);
			var pipelineStateError = new NSError(IntPtr.Zero);
			var pipelineState = _device.NewComputePipelineState(function, ref pipelineStateError);
			if (pipelineStateError != IntPtr.Zero)
			{
				throw new InvalidOperationException($"Failed to create Metal compute pipeline state: {pipelineStateError.LocalizedDescription.ToManagedString()}");
			}

			var pipeline = new MetalPipeline(key, PassKind.Compute, default, pipelineState, default,
				computeTextureEncoder, computeRwTextureEncoder, computeSamplerEncoder, key.RenderState);
			_pipelines[key] = pipeline;
			return pipeline;
		}

		if (shaders.Vertex is null || shaders.Pixel is null)
		{
			throw new InvalidOperationException("Graphics pipeline requires vertex and pixel shader sources.");
		}

		var source = shaders.Vertex.Value;
		var graphicsLibrary = CreateLibraryFromSource(source);
		var vertexFunction = graphicsLibrary.NewFunction(NSStringHelper.From(key.VertexEntryPoint ?? "vertexShader"));
		var fragmentFunction = graphicsLibrary.NewFunction(NSStringHelper.From(key.PixelEntryPoint ?? "fragmentShader"));
		var graphicsTextureEncoder = vertexFunction.NewArgumentEncoder((ulong)MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
		var graphicsSamplerEncoder = vertexFunction.NewArgumentEncoder((ulong)MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);

		var pipelineDescriptor = new MTLRenderPipelineDescriptor
		{
			VertexFunction = vertexFunction,
			FragmentFunction = fragmentFunction
		};

		var formats = key.RenderTargets.Formats.Span;
		for (var i = 0; i < formats.Length; i++)
		{
			var attachment = pipelineDescriptor.ColorAttachments.Object((nuint)i);
			attachment.PixelFormat = ToPixelFormat(formats[i]);
			pipelineDescriptor.ColorAttachments.SetObject(attachment, (nuint)i);
		}

		if (key.DepthStencil.Format != TextureFormat.Unknown)
		{
			pipelineDescriptor.DepthAttachmentPixelFormat = ToPixelFormat(key.DepthStencil.Format);
		}

		pipelineDescriptor.VertexDescriptor = CreateVertexDescriptor(key.Layout);

		var renderStateError = new NSError(IntPtr.Zero);
		var renderState = _device.NewRenderPipelineState(pipelineDescriptor, ref renderStateError);
		if (renderStateError != IntPtr.Zero)
		{
			throw new InvalidOperationException($"Failed to create Metal render pipeline state: {renderStateError.LocalizedDescription.ToManagedString()}");
		}

		MTLDepthStencilState depthState = default;
		if (key.DepthStencil.Format != TextureFormat.Unknown)
		{
			var depthDescriptor = new MTLDepthStencilDescriptor();
			depthDescriptor.DepthCompareFunction = key.RenderState.DepthTestEnabled
				? MTLCompareFunction.Less
				: MTLCompareFunction.Always;
			depthDescriptor.DepthWriteEnabled = key.RenderState.DepthWriteEnabled;
			depthState = _device.NewDepthStencilState(depthDescriptor);
			depthDescriptor.Dispose();
		}

		var pipelineObj = new MetalPipeline(key, PassKind.Graphics, renderState, default, depthState,
			graphicsTextureEncoder, default, graphicsSamplerEncoder, key.RenderState);
		_pipelines[key] = pipelineObj;
		return pipelineObj;
	}

	public IGfxDescriptorSetBuilder CreateDescriptorSetBuilder()
	{
		throw new NotSupportedException("Descriptor sets are not supported in the Metal backend.");
	}

	public bool ReturnTexture(IGfxTexture texture, ResourceState lastKnownState)
	{
		return false;
	}

	public void ClearTexturePool()
	{
	}

	private MTLLibrary CreateLibraryFromSource(ReadOnlyMemory<byte> sourceBytes)
	{
		var source = Encoding.UTF8.GetString(sourceBytes.Span);
		var libraryError = new NSError(IntPtr.Zero);
		var options = new MTLCompileOptions(IntPtr.Zero);
		options.LanguageVersion = MTLLanguageVersion.Version32;
		var library = _device.NewLibrary(NSStringHelper.From(source), options, ref libraryError);
		options.Dispose();
		if (libraryError != IntPtr.Zero)
		{
			throw new InvalidOperationException($"Failed to create Metal library: {libraryError.LocalizedDescription.ToManagedString()}");
		}

		return library;
	}

	private static MTLPixelFormat ToPixelFormat(TextureFormat format) => format switch
	{
		TextureFormat.Bgra8Unorm => MTLPixelFormat.BGRA8Unorm,
		TextureFormat.Rgba8Unorm => MTLPixelFormat.RGBA8Unorm,
		TextureFormat.Rgba16Float => MTLPixelFormat.RGBA16Float,
		TextureFormat.D32Float => MTLPixelFormat.Depth32Float,
		TextureFormat.Unknown => MTLPixelFormat.Invalid,
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported texture format.")
	};

	private static MTLTextureUsage ToUsage(TextureUsage usage)
	{
		var result = MTLTextureUsage.ShaderRead;
		if ((usage & TextureUsage.RenderTarget) != 0 || (usage & TextureUsage.DepthStencil) != 0)
		{
			result |= MTLTextureUsage.RenderTarget;
		}
		if ((usage & TextureUsage.UnorderedAccess) != 0)
		{
			result |= MTLTextureUsage.ShaderWrite;
		}

		return result;
	}

	private static MTLVertexDescriptor CreateVertexDescriptor(GraphicsLayoutKind layout)
	{
		var descriptor = new MTLVertexDescriptor();
		var attributes = descriptor.Attributes;
		var layouts = descriptor.Layouts;

		switch (layout)
		{
			case GraphicsLayoutKind.Skybox:
			{
				var position = attributes.Object(0);
				position.Format = MTLVertexFormat.Float3;
				position.Offset = 0;
				position.BufferIndex = 0;
				attributes.SetObject(position, 0);

				var layoutDesc = layouts.Object(0);
				layoutDesc.Stride = 12;
				layoutDesc.StepFunction = MTLVertexStepFunction.PerVertex;
				layoutDesc.StepRate = 1;
				layouts.SetObject(layoutDesc, 0);
				break;
			}
			case GraphicsLayoutKind.Material:
			case GraphicsLayoutKind.Default:
			default:
			{
				var position = attributes.Object(0);
				position.Format = MTLVertexFormat.Float4;
				position.Offset = 0;
				position.BufferIndex = 0;
				attributes.SetObject(position, 0);

				var normal = attributes.Object(1);
				normal.Format = MTLVertexFormat.Float3;
				normal.Offset = 16;
				normal.BufferIndex = 0;
				attributes.SetObject(normal, 1);

				var uv = attributes.Object(2);
				uv.Format = MTLVertexFormat.Float2;
				uv.Offset = 28;
				uv.BufferIndex = 0;
				attributes.SetObject(uv, 2);

				var tangent = attributes.Object(3);
				tangent.Format = MTLVertexFormat.Float4;
				tangent.Offset = 36;
				tangent.BufferIndex = 0;
				attributes.SetObject(tangent, 3);

				var layoutDesc = layouts.Object(0);
				layoutDesc.Stride = 52;
				layoutDesc.StepFunction = MTLVertexStepFunction.PerVertex;
				layoutDesc.StepRate = 1;
				layouts.SetObject(layoutDesc, 0);
				break;
			}
		}

		return descriptor;
	}
}
