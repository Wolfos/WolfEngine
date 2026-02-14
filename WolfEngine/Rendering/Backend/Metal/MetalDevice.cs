using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Platform;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("macos")]
internal sealed class MetalDevice : IGfxDevice, ITexturePoolDevice
{
	public readonly record struct MetalDiagnosticsSnapshot(
		int TexturePoolBuckets,
		int TexturePoolTextures,
		int SrvCount,
		int UavCount,
		int SamplerCount,
		int FreeSrvCount,
		int FreeUavCount,
		int FreeSamplerCount,
		ulong TextureArgumentBufferBytes,
		ulong RwTextureArgumentBufferBytes,
		ulong SamplerArgumentBufferBytes,
		uint BindlessVersion);

	private readonly struct TexturePoolKey : IEquatable<TexturePoolKey>
	{
		public TexturePoolKey(in TextureDescriptor descriptor)
		{
			Width = descriptor.Width;
			Height = descriptor.Height;
			Format = descriptor.Format;
			Usage = descriptor.Usage;
		}

		public int Width { get; }
		public int Height { get; }
		public TextureFormat Format { get; }
		public TextureUsage Usage { get; }

		public bool Equals(TexturePoolKey other) =>
			Width == other.Width &&
			Height == other.Height &&
			Format == other.Format &&
			Usage == other.Usage;

		public override bool Equals(object obj) => obj is TexturePoolKey other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(Width, Height, Format, Usage);
	}

	private MTLDevice _device;
	private readonly MTLCommandQueue _commandQueue;
	private readonly MetalDescriptorTable _descriptorTable;
	private MetalIndirectCommandBuffer? _sharedIndirectCommandBuffer;
	private readonly Dictionary<PipelineKey, MetalPipeline> _pipelines = new();
	private readonly Dictionary<TexturePoolKey, Stack<MetalTexture>> _texturePool = new();

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

	public string GetDiagnosticsSnapshot()
	{
		var snapshot = CaptureDiagnosticsSnapshot();
		return $"TexturePool: buckets={snapshot.TexturePoolBuckets}, textures={snapshot.TexturePoolTextures}, " +
		       $"Bindless: srv={snapshot.SrvCount}, uav={snapshot.UavCount}, samp={snapshot.SamplerCount}, " +
		       $"freeSrv={snapshot.FreeSrvCount}, freeUav={snapshot.FreeUavCount}, freeSamp={snapshot.FreeSamplerCount}, " +
		       $"bindlessVer={snapshot.BindlessVersion}, " +
		       $"ArgBuffers: textures={snapshot.TextureArgumentBufferBytes / (1024.0 * 1024.0):F1} MiB, " +
		       $"rwTextures={snapshot.RwTextureArgumentBufferBytes / (1024.0 * 1024.0):F1} MiB, " +
		       $"samplers={snapshot.SamplerArgumentBufferBytes / (1024.0 * 1024.0):F1} MiB";
	}

	public MetalDiagnosticsSnapshot CaptureDiagnosticsSnapshot()
	{
		var totalPools = 0;
		var totalTextures = 0;
		foreach (var entry in _texturePool)
		{
			totalPools++;
			totalTextures += entry.Value.Count;
		}

		return new MetalDiagnosticsSnapshot(
			totalPools,
			totalTextures,
			_descriptorTable.SrvCount,
			_descriptorTable.UavCount,
			_descriptorTable.SamplerCount,
			_descriptorTable.FreeSrvCount,
			_descriptorTable.FreeUavCount,
			_descriptorTable.FreeSamplerCount,
			_descriptorTable.TextureArgumentBufferBytes,
			_descriptorTable.RwTextureArgumentBufferBytes,
			_descriptorTable.SamplerArgumentBufferBytes,
			_descriptorTable.BindlessVersion);
	}

	public IGfxCommandList BeginGraphics()
	{
		return new MetalCommandList(_commandQueue, _descriptorTable);
	}

	public IGfxCommandList BeginCompute()
	{
		return new MetalCommandList(_commandQueue, _descriptorTable);
	}

	public void WaitForIdle()
	{
		var buffer = _commandQueue.CommandBuffer();
		if (buffer.NativePtr == IntPtr.Zero)
		{
			return;
		}

		buffer.Commit();
		buffer.WaitUntilCompleted();
		buffer.Dispose();
	}

	public void Submit(IGfxCommandList commandList)
	{
		if (commandList is not MetalCommandList metalCommandList)
		{
			throw new InvalidOperationException("Command list was not created by the Metal backend.");
		}

		try
		{
			metalCommandList.Commit();
		}
		finally
		{
			metalCommandList.Dispose();
		}
	}

	public IGfxTexture CreateTexture(in TextureDescriptor descriptor)
	{
		if (descriptor.Width <= 0 || descriptor.Height <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(descriptor), "Textures must have positive dimensions.");
		}

		var poolKey = new TexturePoolKey(descriptor);
		if (_texturePool.TryGetValue(poolKey, out var pool) && pool.Count > 0)
		{
			return pool.Pop();
		}

		var textureDescriptor = new MTLTextureDescriptor();
		textureDescriptor.Width = (ulong)descriptor.Width;
		textureDescriptor.Height = (ulong)descriptor.Height;
		textureDescriptor.Depth = 1;
		textureDescriptor.MipmapLevelCount = 1;
		textureDescriptor.PixelFormat = ToPixelFormat(descriptor.Format);
		textureDescriptor.TextureType = MTLTextureType.Type2D;
		textureDescriptor.StorageMode = MTLStorageMode.Managed;
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

	public IIndirectCommandBuffer GetOrCreateIndirectCommandBuffer(uint maxCommands)
	{
		if (_sharedIndirectCommandBuffer is not null && _sharedIndirectCommandBuffer.MaxCommandCount >= maxCommands)
		{
			return _sharedIndirectCommandBuffer;
		}

		_sharedIndirectCommandBuffer?.Dispose();

		var descriptor = new MTLIndirectCommandBufferDescriptor
		{
			CommandTypes = MTLIndirectCommandType.DrawIndexed,
			InheritPipelineState = false,
			InheritBuffers = false,
			SupportDynamicAttributeStride = true,
			MaxVertexBufferBindCount = 31,
			MaxFragmentBufferBindCount = 31
		};

		var buffer = _device.NewIndirectCommandBuffer(descriptor, maxCommands, MTLResourceOptions.ResourceStorageModeShared);
		descriptor.Dispose();
		_sharedIndirectCommandBuffer = new MetalIndirectCommandBuffer(buffer, maxCommands);
		return _sharedIndirectCommandBuffer;
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
			var pipelineStateError = new NSError(IntPtr.Zero);
			var computeReflection = CreateComputeReflection(function, ref pipelineStateError, out var pipelineState);
			if (pipelineStateError != IntPtr.Zero)
			{
				throw new InvalidOperationException($"Failed to create Metal compute pipeline state: {pipelineStateError.LocalizedDescription.ToManagedString()}");
			}

			var computeTextureEncoder = CreateArgumentEncoder(function, computeReflection?.Arguments, MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
			var computeRwTextureEncoder = CreateArgumentEncoder(function, computeReflection?.Arguments, MetalDescriptorTable.BindlessArgumentBufferIndexRWTextures);
			var computeSamplerEncoder = CreateArgumentEncoder(function, computeReflection?.Arguments, MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);

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

		var pipelineDescriptor = new MTLRenderPipelineDescriptor
		{
			VertexFunction = vertexFunction,
			FragmentFunction = fragmentFunction,
			SupportIndirectCommandBuffers = true
		};

		var formats = key.RenderTargets.Formats.Span;
		for (var i = 0; i < formats.Length; i++)
		{
			var attachment = pipelineDescriptor.ColorAttachments.Object((nuint)i);
			attachment.PixelFormat = ToPixelFormat(formats[i]);
			ApplyBlendState(attachment, key.RenderState.BlendMode);
			pipelineDescriptor.ColorAttachments.SetObject(attachment, (nuint)i);
		}

		if (key.DepthStencil.Format != TextureFormat.Unknown)
		{
			pipelineDescriptor.DepthAttachmentPixelFormat = ToPixelFormat(key.DepthStencil.Format);
		}

		pipelineDescriptor.VertexDescriptor = CreateVertexDescriptor(key.Layout);

		var renderStateError = new NSError(IntPtr.Zero);
		var renderReflection = CreateRenderReflection(pipelineDescriptor, ref renderStateError, out var renderState);
		if (renderStateError != IntPtr.Zero)
		{
			throw new InvalidOperationException($"Failed to create Metal render pipeline state: {renderStateError.LocalizedDescription.ToManagedString()}");
		}

		var graphicsTextureEncoder = CreateArgumentEncoder(fragmentFunction, renderReflection?.FragmentArguments, MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
		var graphicsSamplerEncoder = CreateArgumentEncoder(fragmentFunction, renderReflection?.FragmentArguments, MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);

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
		if (texture is not MetalTexture metalTexture || metalTexture.IsDisposed)
		{
			return false;
		}

		var key = new TexturePoolKey(metalTexture.Descriptor);
		if (_texturePool.TryGetValue(key, out var pool) == false)
		{
			pool = new Stack<MetalTexture>();
			_texturePool[key] = pool;
		}

		pool.Push(metalTexture);
		return true;
	}

	public void ClearTexturePool()
	{
		foreach (var (_, pool) in _texturePool)
		{
			while (pool.Count > 0)
			{
				pool.Pop().Dispose();
			}
		}

		_texturePool.Clear();
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

	private MTLComputePipelineReflection? CreateComputeReflection(
		MTLFunction function,
		ref NSError error,
		out MTLComputePipelineState pipelineState)
	{
		var reflectionStorage = AllocateReflectionStorage();
		pipelineState = _device.NewComputePipelineState(function, MTLPipelineOption.ArgumentInfo, reflectionStorage, ref error);
		return ReadComputeReflection(reflectionStorage);
	}

	private MTLRenderPipelineReflection? CreateRenderReflection(
		MTLRenderPipelineDescriptor descriptor,
		ref NSError error,
		out MTLRenderPipelineState pipelineState)
	{
		var reflectionStorage = AllocateReflectionStorage();
		pipelineState = _device.NewRenderPipelineState(descriptor, MTLPipelineOption.ArgumentInfo, reflectionStorage, ref error);
		return ReadRenderReflection(reflectionStorage);
	}

	private static IntPtr AllocateReflectionStorage()
	{
		var storage = Marshal.AllocHGlobal(IntPtr.Size);
		Marshal.WriteIntPtr(storage, IntPtr.Zero);
		return storage;
	}

	private static MTLComputePipelineReflection? ReadComputeReflection(IntPtr storage)
	{
		var reflectionPtr = Marshal.ReadIntPtr(storage);
		Marshal.FreeHGlobal(storage);
		return reflectionPtr == IntPtr.Zero ? null : new MTLComputePipelineReflection(reflectionPtr);
	}

	private static MTLRenderPipelineReflection? ReadRenderReflection(IntPtr storage)
	{
		var reflectionPtr = Marshal.ReadIntPtr(storage);
		Marshal.FreeHGlobal(storage);
		return reflectionPtr == IntPtr.Zero ? null : new MTLRenderPipelineReflection(reflectionPtr);
	}

	private static MTLArgumentEncoder CreateArgumentEncoder(
		MTLFunction function,
		NSArray? arguments,
		int bufferIndex)
	{
		if (arguments is null || HasArgumentBuffer(arguments.Value, (ulong)bufferIndex) == false)
		{
			return default;
		}

		return function.NewArgumentEncoder((ulong)bufferIndex);
	}

	private static bool HasArgumentBuffer(NSArray arguments, ulong bufferIndex)
	{
		var count = arguments.Count;
		for (ulong i = 0; i < count; i++)
		{
			var argumentPtr = arguments.Object(i);
			if (argumentPtr == IntPtr.Zero)
			{
				continue;
			}

			var argument = new MTLArgument(argumentPtr);
			if (argument.Type == MTLArgumentType.Buffer &&
			    argument.Index == bufferIndex &&
			    argument.BufferPointerType.ElementIsArgumentBuffer)
			{
				return true;
			}
		}

		return false;
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
				position.Format = MTLVertexFormat.Float4;
				position.Offset = 0;
				position.BufferIndex = 0;
				attributes.SetObject(position, 0);

				var layoutDesc = layouts.Object(0);
				layoutDesc.Stride = 52;
				layoutDesc.StepFunction = MTLVertexStepFunction.PerVertex;
				layoutDesc.StepRate = 1;
				layouts.SetObject(layoutDesc, 0);
				break;
			}
			case GraphicsLayoutKind.ImGui:
			{
				var position = attributes.Object(0);
				position.Format = MTLVertexFormat.Float2;
				position.Offset = 0;
				position.BufferIndex = 2;
				attributes.SetObject(position, 0);

				var uv = attributes.Object(1);
				uv.Format = MTLVertexFormat.Float2;
				uv.Offset = 8;
				uv.BufferIndex = 2;
				attributes.SetObject(uv, 1);

				var color = attributes.Object(2);
				color.Format = MTLVertexFormat.UChar4Normalized;
				color.Offset = 16;
				color.BufferIndex = 2;
				attributes.SetObject(color, 2);

				var layoutDesc = layouts.Object(2);
				layoutDesc.Stride = 20;
				layoutDesc.StepFunction = MTLVertexStepFunction.PerVertex;
				layoutDesc.StepRate = 1;
				layouts.SetObject(layoutDesc, 2);
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

	private static void ApplyBlendState(MTLRenderPipelineColorAttachmentDescriptor attachment, BlendMode blendMode)
	{
        switch (blendMode)
        {
            case BlendMode.Additive:
                attachment.IsBlendingEnabled = true;
                attachment.RgbBlendOperation = MTLBlendOperation.Add;
                attachment.AlphaBlendOperation = MTLBlendOperation.Add;
                attachment.SourceRGBBlendFactor = MTLBlendFactor.One;
                attachment.DestinationRGBBlendFactor = MTLBlendFactor.One;
                attachment.SourceAlphaBlendFactor = MTLBlendFactor.One;
                attachment.DestinationAlphaBlendFactor = MTLBlendFactor.One;
                attachment.WriteMask = MTLColorWriteMask.All;
                break;
            case BlendMode.AlphaBlend:
                attachment.IsBlendingEnabled = true;
                attachment.RgbBlendOperation = MTLBlendOperation.Add;
                attachment.AlphaBlendOperation = MTLBlendOperation.Add;
                attachment.SourceRGBBlendFactor = MTLBlendFactor.SourceAlpha;
                attachment.DestinationRGBBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
                attachment.SourceAlphaBlendFactor = MTLBlendFactor.One;
                attachment.DestinationAlphaBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
                attachment.WriteMask = MTLColorWriteMask.All;
                break;
            case BlendMode.Opaque:
            default:
                attachment.IsBlendingEnabled = false;
                attachment.WriteMask = MTLColorWriteMask.All;
                break;
        }
	}
}
