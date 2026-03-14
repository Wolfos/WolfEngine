using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Platform;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("macos")]
internal sealed class MetalDevice : IGfxDevice, ITexturePoolDevice, IGpuSubmissionTimeline
{
	private const int MaxPendingCommandLists = GpuDrawResources.MaxFramesInFlight * 16;
	private const int MaxPooledTextures = 256;
	private const int MaxPooledTexturesPerDescriptor = 2;

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
	private readonly Dictionary<PipelineKey, MetalPipeline> _pipelines = new();
	private readonly string _metallibCacheDirectory;
	private readonly Dictionary<TexturePoolKey, Stack<MetalTexture>> _texturePool = new();
	private readonly Queue<PendingSubmission> _pendingSubmissions = new();
	private readonly object _submissionSync = new();
	private int _pooledTextureCount;
	private ulong _lastSubmittedId;
	private ulong _completedId;

	private readonly struct PendingSubmission
	{
		public PendingSubmission(ulong id, MetalCommandList commandList)
		{
			Id = id;
			CommandList = commandList;
		}

		public ulong Id { get; }
		public MetalCommandList CommandList { get; }
	}

	public MetalDevice(MTLDevice device)
	{
		_device = device;
		_commandQueue = _device.NewCommandQueue();
		if (_commandQueue.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to create Metal command queue.");
		}
		_descriptorTable = new MetalDescriptorTable(_device);
		_metallibCacheDirectory = Path.Combine(Path.GetTempPath(), "WolfEngine", "metallib-cache");
		Directory.CreateDirectory(_metallibCacheDirectory);
	}

	public IGfxDescriptorTable GlobalTable => _descriptorTable;

	public GraphicsBackendKind BackendKind => GraphicsBackendKind.Metal;

	public ulong LastSubmittedId
	{
		get
		{
			lock (_submissionSync)
			{
				return _lastSubmittedId;
			}
		}
	}

	public ulong CompletedId
	{
		get
		{
			lock (_submissionSync)
			{
				return _completedId;
			}
		}
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
		while (TryDequeueSubmission(onlyWhenOverLimit: false, out var submission))
		{
			RetireSubmission(submission, waitForCompletion: true);
		}
	}

	public void Submit(IGfxCommandList commandList)
	{
		if (commandList is not MetalCommandList metalCommandList)
		{
			throw new InvalidOperationException("Command list was not created by the Metal backend.");
		}

		metalCommandList.Commit();
		ulong submissionId;
		lock (_submissionSync)
		{
			submissionId = ++_lastSubmittedId;
			_pendingSubmissions.Enqueue(new PendingSubmission(submissionId, metalCommandList));
		}
		PumpCompleted();
	}

	public void PumpCompleted()
	{
		// SharpMetal does not currently expose a portable non-blocking completion query in this layer.
		// Keep a bounded in-flight queue and retire oldest submissions when the queue is full.
		while (TryDequeueSubmission(onlyWhenOverLimit: true, out var submission))
		{
			RetireSubmission(submission, waitForCompletion: true);
		}
	}

	private bool TryDequeueSubmission(bool onlyWhenOverLimit, out PendingSubmission submission)
	{
		lock (_submissionSync)
		{
			if (_pendingSubmissions.Count == 0)
			{
				submission = default;
				return false;
			}

			if (onlyWhenOverLimit && _pendingSubmissions.Count <= MaxPendingCommandLists)
			{
				submission = default;
				return false;
			}

			submission = _pendingSubmissions.Dequeue();
			return true;
		}
	}

	private void RetireSubmission(PendingSubmission submission, bool waitForCompletion)
	{
		var commandList = submission.CommandList;
		if (commandList is null)
		{
			MarkSubmissionCompleted(submission.Id);
			return;
		}

		try
		{
			if (waitForCompletion)
			{
				commandList.WaitUntilCompleted();
			}
		}
		catch (NullReferenceException)
		{
			// SharpMetal can surface managed null wrappers when native command buffers are torn down unexpectedly.
		}
		finally
		{
			try
			{
				commandList.Dispose();
			}
			catch (NullReferenceException)
			{
			}

			MarkSubmissionCompleted(submission.Id);
		}
	}

	private void MarkSubmissionCompleted(ulong submissionId)
	{
		lock (_submissionSync)
		{
			_completedId = Math.Max(_completedId, submissionId);
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
			var pooled = pool.Pop();
			_pooledTextureCount = Math.Max(0, _pooledTextureCount - 1);
			if (pool.Count == 0)
			{
				_texturePool.Remove(poolKey);
			}

			return pooled;
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
		var depthSrvHandle = (descriptor.Usage & TextureUsage.ShaderResource) != 0 &&
		                     (descriptor.Usage & TextureUsage.DepthStencil) != 0
			? _descriptorTable.AllocateDepthShaderResourceView(metalTexture)
			: DescriptorHandle.Invalid;

		var uavHandle = (descriptor.Usage & TextureUsage.UnorderedAccess) != 0
			? _descriptorTable.AllocateUnorderedAccessView(metalTexture)
			: DescriptorHandle.Invalid;

		metalTexture.SetHandles(srvHandle, depthSrvHandle, uavHandle);
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

	public IGfxIndirectCommandBuffer CreateIndirectCommandBuffer(in IndirectCommandBufferDescriptor descriptor)
	{
		if (descriptor.PassKind != PassKind.Graphics)
		{
			throw new NotSupportedException("Metal indirect command buffers currently support graphics pass encoding only.");
		}

		var indirectDescriptor = new MTLIndirectCommandBufferDescriptor();
		try
		{
			indirectDescriptor.CommandTypes = MTLIndirectCommandType.DrawIndexed;
			indirectDescriptor.InheritPipelineState = true;
			indirectDescriptor.InheritBuffers = false;
			indirectDescriptor.MaxVertexBufferBindCount = 31;
			indirectDescriptor.MaxFragmentBufferBindCount = 31;

			var commandBuffer = _device.NewIndirectCommandBuffer(
				indirectDescriptor,
				descriptor.MaxCommandCount,
				MTLResourceOptions.ResourceStorageModeShared);
			if (commandBuffer.NativePtr == IntPtr.Zero)
			{
				throw new InvalidOperationException("Failed to create Metal indirect command buffer.");
			}

			commandBuffer.Reset(new NSRange { location = 0, length = descriptor.MaxCommandCount });
			return new MetalIndirectCommandBuffer(null, descriptor, commandBuffer);
		}
		finally
		{
			indirectDescriptor.Dispose();
		}
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

			using var computeLibrary = CreateLibraryFromMetallib(shaders.Compute.Value);
			using var computeEntry = NSStringHelper.From(key.ComputeEntryPoint ?? "CSMain");
			using var function = computeLibrary.NewFunction(computeEntry);
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
		using var graphicsLibrary = CreateLibraryFromMetallib(source);
		using var vertexEntry = NSStringHelper.From(key.VertexEntryPoint ?? "vertexShader");
		using var fragmentEntry = NSStringHelper.From(key.PixelEntryPoint ?? "fragmentShader");
		using var vertexFunction = graphicsLibrary.NewFunction(vertexEntry);
		using var fragmentFunction = graphicsLibrary.NewFunction(fragmentEntry);

		var pipelineDescriptor = new MTLRenderPipelineDescriptor
		{
			VertexFunction = vertexFunction,
			FragmentFunction = fragmentFunction,
			SupportIndirectCommandBuffers = true
		};

		try
		{
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

			using var vertexDescriptor = CreateVertexDescriptor(key.Layout);
			pipelineDescriptor.VertexDescriptor = vertexDescriptor;

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
		finally
		{
			pipelineDescriptor.Dispose();
		}
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

		// Resize-heavy workloads (editor scene viewport) can generate many unique descriptors.
		// Keep the pool bounded so descriptor-table high-water does not grow unbounded.
		if (_pooledTextureCount >= MaxPooledTextures)
		{
			metalTexture.Dispose();
			return true;
		}

		var key = new TexturePoolKey(metalTexture.Descriptor);
		if (_texturePool.TryGetValue(key, out var pool) == false)
		{
			pool = new Stack<MetalTexture>();
			_texturePool[key] = pool;
		}

		if (pool.Count >= MaxPooledTexturesPerDescriptor)
		{
			metalTexture.Dispose();
			return true;
		}

		pool.Push(metalTexture);
		_pooledTextureCount++;
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
		_pooledTextureCount = 0;
	}

	private MTLLibrary CreateLibraryFromMetallib(ReadOnlyMemory<byte> metallibBytes)
	{
		var cacheKey = Convert.ToHexString(SHA256.HashData(metallibBytes.Span)).ToLowerInvariant();
		var metallibPath = Path.Combine(_metallibCacheDirectory, $"{cacheKey}.metallib");
		if (File.Exists(metallibPath) == false)
		{
			var tempPath = $"{metallibPath}.tmp-{Guid.NewGuid():N}";
			File.WriteAllBytes(tempPath, metallibBytes.ToArray());
			File.Move(tempPath, metallibPath, overwrite: true);
		}

		var libraryError = new NSError(IntPtr.Zero);
		using var libraryPath = NSStringHelper.From(metallibPath);
		var library = _device.NewLibrary(libraryPath, ref libraryError);
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
		TextureFormat.Rg16Float => MTLPixelFormat.RG16Float,
		TextureFormat.Rgba16Float => MTLPixelFormat.RGBA16Float,
		TextureFormat.R32Float => MTLPixelFormat.R32Float,
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
