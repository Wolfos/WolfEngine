#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using WolfEngine.Rendering.Abstraction;
using Silk.NET.DXGI;
using WolfEngine.Backend.D3D12;
using AbstractionFillMode = WolfEngine.Rendering.Abstraction.FillMode;
using AbstractionCullMode = WolfEngine.Rendering.Abstraction.CullMode;
using D3D12Api = Silk.NET.Direct3D12.D3D12;
using D3DFillMode = Silk.NET.Direct3D12.FillMode;
using D3DCullMode = Silk.NET.Direct3D12.CullMode;

using FenceFlags = Silk.NET.Direct3D12.FenceFlags;
using Fence = Silk.NET.Direct3D12.ID3D12Fence;

namespace WolfEngine.Rendering.Backend.D3D12;

public sealed unsafe class D3D12Device : IGfxDevice, ITexturePoolDevice, IGpuSubmissionTimeline, IGpuProfilerDevice, IDisposable
{
	private const ulong DefaultConstantUploadPageSize = 256UL * 1024UL;
	private const ulong ConstantUploadAlignment = 256UL;

	private readonly ComPtr<ID3D12Device> _device;
	private readonly ComPtr<ID3D12Device5> _rayTracingDevice;
	private readonly ComPtr<ID3D12CommandQueue> _graphicsQueue;
	private readonly ComPtr<ID3D12CommandQueue> _computeQueue;
	private readonly D3D12Api _d3d12 = D3D12Api.GetApi();

	private readonly D3D12DescriptorTable _globalTable;
	private readonly D3D12GpuProfilerBackend _gpuProfilerBackend;

	private readonly List<CommandListSubmission> _inFlightCommandLists = new();
	private readonly object _commandListLock = new();
	private readonly object _submissionLock = new();
	private readonly object _submissionFenceWaitLock = new();
	private readonly object _constantUploadLock = new();
	private readonly object _uploadLock = new();
	private readonly ComPtr<Fence> _submissionFence;
	private ulong _submissionFenceValue;
	
	private readonly Dictionary<PipelineKey, IGfxPipeline> _pipelineCache = new();
	private readonly object _pipelineLock = new();
	private readonly Dictionary<GraphicsLayoutKind, ComPtr<ID3D12RootSignature>> _graphicsRootSignatures = new();
	private ComPtr<ID3D12RootSignature> _computeRootSignature;
	private readonly Dictionary<CommandListType, Queue<D3D12CommandList>> _commandListPool = new();
	private readonly Dictionary<ulong, Stack<D3D12ConstantUploadPage>> _constantUploadPagePool = new();
	private readonly Dictionary<TextureDescriptor, Queue<PooledTexture>> _texturePool = new(new TextureDescriptorComparer());
	private readonly Queue<ExternalD3D12Texture> _externalTexturePool = new();
	private readonly object _texturePoolLock = new();
	private ComPtr<ID3D12CommandAllocator> _uploadAllocator;
	private ComPtr<ID3D12GraphicsCommandList> _uploadCommandList;
	private ComPtr<ID3D12CommandSignature> _drawIndexedIndirectSignature;
	private ComPtr<ID3D12CommandSignature> _graphicsExecuteIndirectSignature;
	private nint _submissionFenceEvent;
	private bool _isDisposed;
	private D3D12ConstantUploadStats _constantUploadStats;

	private readonly struct PooledTexture
	{
		public PooledTexture(D3D12Texture texture, ResourceStates state)
		{
			Texture = texture;
			LastKnownState = state;
		}

		public D3D12Texture Texture { get; }

		public ResourceStates LastKnownState { get; }
	}

	private sealed class TextureDescriptorComparer : IEqualityComparer<TextureDescriptor>
	{
		public bool Equals(TextureDescriptor x, TextureDescriptor y)
		{
			return x.Width == y.Width &&
			       x.Height == y.Height &&
			       x.Format == y.Format &&
			       x.Usage == y.Usage &&
			       x.MipLevels == y.MipLevels &&
			       x.IsSrgb == y.IsSrgb &&
			       x.ClearColor.Equals(y.ClearColor) &&
			       Math.Abs(x.DepthClear - y.DepthClear) < float.Epsilon;
		}

		public int GetHashCode(TextureDescriptor obj)
		{
			return HashCode.Combine(
				obj.Width,
				obj.Height,
				(int)obj.Format,
				(int)obj.Usage,
				obj.MipLevels,
				obj.IsSrgb,
				obj.ClearColor,
				obj.DepthClear);
		}
	}
	
	private readonly struct CommandListSubmission
	{
		public CommandListSubmission(D3D12CommandList commandList, ulong fenceValue)
		{
			CommandList = commandList;
			FenceValue = fenceValue;
		}

		public D3D12CommandList CommandList { get; }

		public ulong FenceValue { get; }
	}

	internal readonly record struct D3D12ConstantUploadStats(
		ulong AllocationCount,
		ulong RequestedBytes,
		ulong CommittedPageBytesInUse,
		uint NewPageCreations,
		uint PageReuses,
		uint OversizeDedicatedPageRents);

	public D3D12Device(
		ComPtr<ID3D12Device> device,
		ComPtr<ID3D12CommandQueue> graphicsQueue, ComPtr<ID3D12CommandQueue>? computeQueue = null)
	{
		_device = device;
		var rayTracingDevice = _device.QueryInterface<ID3D12Device5>();
		if (SupportsInlineRayTracing(_device) == false && rayTracingDevice.Handle is not null)
		{
			rayTracingDevice.Dispose();
			rayTracingDevice = default;
		}
		_rayTracingDevice = rayTracingDevice;
		_graphicsQueue = graphicsQueue;
		_computeQueue = computeQueue ?? graphicsQueue;
		_globalTable = new D3D12DescriptorTable(_device);
		SilkMarshal.ThrowHResult(_device.CreateFence(0, FenceFlags.None, out _submissionFence));
		_submissionFenceValue = 0;
		_gpuProfilerBackend = new D3D12GpuProfilerBackend(this, _device, _graphicsQueue);
	}

	public GraphicsBackendKind BackendKind => GraphicsBackendKind.D3D12;
	public bool SupportsRayTracing => _rayTracingDevice.Handle is not null;
	IGpuProfilerBackend IGpuProfilerDevice.GpuProfilerBackend => _gpuProfilerBackend;

	public ulong LastSubmittedId
	{
		get
		{
			lock (_submissionLock)
			{
				return _submissionFenceValue;
			}
		}
	}

	public ulong CompletedId =>
		_submissionFence.Handle is null ? 0UL : _submissionFence.Handle->GetCompletedValue();

	internal D3D12ConstantUploadStats ConstantUploadStats => _constantUploadStats;

	public IGfxCommandList BeginGraphics()
	{
		return CreateCommandList(CommandListType.Direct);
	}

	public IGfxCommandList BeginCompute()
	{
		return CreateCommandList(CommandListType.Direct);
	}

	public void PumpCompleted()
	{
		lock (_commandListLock)
		{
			CleanupCompletedCommandListsLocked();
		}
	}

	internal void ResetConstantUploadStats()
	{
		_constantUploadStats = default;
	}

	public void Submit(IGfxCommandList commandList)
	{
		if (commandList is not D3D12CommandList nativeCommandList)
		{
			throw new ArgumentException("Command list was not created by the Direct3D12 backend.", nameof(commandList));
		}

		var fenceValue = SubmitCommandList(nativeCommandList);

		lock (_commandListLock)
		{
			_inFlightCommandLists.Add(new(nativeCommandList, fenceValue));
		}
	}

	private ulong SubmitCommandList(D3D12CommandList commandList)
	{
		commandList.Close();
		var nativeHandle = (ID3D12CommandList*)commandList.CommandList.Handle;
		var queue = commandList.Type == CommandListType.Compute ? _computeQueue : _graphicsQueue;
		return ExecuteCommandList(queue, nativeHandle);
	}

	private ulong ExecuteCommandList(
		ComPtr<ID3D12CommandQueue> queue,
		ID3D12CommandList* commandList)
	{
		lock (_submissionLock)
		{
			var fenceValue = checked(_submissionFenceValue + 1UL);
			queue.ExecuteCommandLists(1, &commandList);
			SilkMarshal.ThrowHResult(queue.Signal(_submissionFence, fenceValue));
			_submissionFenceValue = fenceValue;
			return fenceValue;
		}
	}

	public IGfxTexture CreateTexture(in TextureDescriptor descriptor)
	{
		if (descriptor.Width <= 0 || descriptor.Height <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(descriptor), "Textures must have positive dimensions.");
		}

		var initialState = DetermineInitialState(descriptor.Usage);
		if (TryRentPooledTexture(descriptor, initialState, out var pooledTexture))
		{
			return pooledTexture;
		}

		var viewFormat = ToDxgiFormat(descriptor.Format, descriptor.IsSrgb);
		var isDepthTexture = (descriptor.Usage & TextureUsage.DepthStencil) != 0;
		var resourceFormat = isDepthTexture && (descriptor.Usage & TextureUsage.ShaderResource) != 0
			? Format.FormatR32Typeless
			: viewFormat;

		var resourceDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = (ulong)descriptor.Width,
			Height = (uint)descriptor.Height,
			DepthOrArraySize = 1,
			MipLevels = (ushort)descriptor.MipLevels,
			Format = resourceFormat,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutUnknown,
			Flags = DetermineResourceFlags(descriptor.Usage)
		};

		var heapProps = new HeapProperties(HeapType.Default);

		ClearValue clearValue = default;
		ClearValue* clearValuePtr = null;

		if ((descriptor.Usage & TextureUsage.RenderTarget) != 0)
		{
			clearValue.Format = viewFormat;
			clearValue.Anonymous.Color[0] = descriptor.ClearColor.R;
			clearValue.Anonymous.Color[1] = descriptor.ClearColor.G;
			clearValue.Anonymous.Color[2] = descriptor.ClearColor.B;
			clearValue.Anonymous.Color[3] = descriptor.ClearColor.A;
			clearValuePtr = &clearValue;
		}
		else if ((descriptor.Usage & TextureUsage.DepthStencil) != 0)
		{
			clearValue.Format = viewFormat;
			clearValue.Anonymous.DepthStencil = new DepthStencilValue
			{
				Depth = descriptor.DepthClear,
				Stencil = 0
			};
			clearValuePtr = &clearValue;
		}

		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&heapProps,
			HeapFlags.None,
			in resourceDesc,
			initialState,
			clearValuePtr,
			out ComPtr<ID3D12Resource> resource));

		var texture = new D3D12Texture();
		texture.Initialize(null, descriptor, resource);

		if ((descriptor.Usage & TextureUsage.RenderTarget) != 0)
		{
			var rtvHeapDesc = new DescriptorHeapDesc
			{
				Type = DescriptorHeapType.Rtv,
				NumDescriptors = 1,
				Flags = DescriptorHeapFlags.None,
				NodeMask = 0
			};

			SilkMarshal.ThrowHResult(
				_device.CreateDescriptorHeap(in rtvHeapDesc, out ComPtr<ID3D12DescriptorHeap> heap));
			var handle = heap.GetCPUDescriptorHandleForHeapStart();
			_device.CreateRenderTargetView(resource, null, handle);

			texture.SetRenderTargetView(heap, handle);
		}

		if ((descriptor.Usage & TextureUsage.DepthStencil) != 0)
		{
			var dsvHeapDesc = new DescriptorHeapDesc
			{
				Type = DescriptorHeapType.Dsv,
				NumDescriptors = 1,
				Flags = DescriptorHeapFlags.None,
				NodeMask = 0
			};

			SilkMarshal.ThrowHResult(
				_device.CreateDescriptorHeap(in dsvHeapDesc, out ComPtr<ID3D12DescriptorHeap> heap));
			var handle = heap.GetCPUDescriptorHandleForHeapStart();
			var depthStencilViewDesc = new DepthStencilViewDesc
			{
				Format = Format.FormatD32Float,
				ViewDimension = DsvDimension.Texture2D,
				Flags = DsvFlags.None
			};
			depthStencilViewDesc.Anonymous.Texture2D = new Tex2DDsv
			{
				MipSlice = 0
			};
			_device.CreateDepthStencilView(resource, &depthStencilViewDesc, handle);

			texture.SetDepthStencilView(heap, handle);
		}

		var srvHandle = DescriptorHandle.Invalid;
		var depthSrvHandle = DescriptorHandle.Invalid;
		var uavHandle = DescriptorHandle.Invalid;
		if ((descriptor.Usage & TextureUsage.ShaderResource) != 0)
		{
			srvHandle = _globalTable.AllocateShaderResourceView(texture);
			if ((descriptor.Usage & TextureUsage.DepthStencil) != 0)
			{
				depthSrvHandle = _globalTable.AllocateDepthShaderResourceView(texture);
			}
		}

		if ((descriptor.Usage & TextureUsage.UnorderedAccess) != 0)
		{
			uavHandle = _globalTable.AllocateUnorderedAccessView(texture);
		}

		texture.SetHandles(srvHandle, depthSrvHandle, uavHandle, _globalTable);

		return texture;
	}

	public ID3D12BackendTexture ImportExternalTexture(
		in TextureDescriptor descriptor,
		ID3D12Resource* resource,
		CpuDescriptorHandle? rtvHandle,
		CpuDescriptorHandle? dsvHandle)
	{
		if (resource is null)
		{
			throw new ArgumentNullException(nameof(resource));
		}

		var wrapper = RentExternalTextureWrapper();
		wrapper.Initialize(descriptor, resource, rtvHandle, dsvHandle);
		var srvHandle = DescriptorHandle.Invalid;
		var depthSrvHandle = DescriptorHandle.Invalid;
		var uavHandle = DescriptorHandle.Invalid;
		if ((descriptor.Usage & TextureUsage.ShaderResource) != 0)
		{
			srvHandle = _globalTable.AllocateShaderResourceView(wrapper);
			if ((descriptor.Usage & TextureUsage.DepthStencil) != 0)
			{
				depthSrvHandle = _globalTable.AllocateDepthShaderResourceView(wrapper);
			}
		}

		if ((descriptor.Usage & TextureUsage.UnorderedAccess) != 0)
		{
			uavHandle = _globalTable.AllocateUnorderedAccessView(wrapper);
		}

		wrapper.SetHandles(srvHandle, depthSrvHandle, uavHandle);
		return wrapper;
	}

	public IGfxBuffer CreateBuffer(in BufferDescriptor descriptor)
	{
		var bufferAlignment = descriptor.Usage.HasFlag(BufferUsage.Constant)
			? (ulong)Silk.NET.Direct3D12.D3D12.ConstantBufferDataPlacementAlignment
			: 16UL;
		var sizeInBytes = Align(descriptor.SizeInBytes, bufferAlignment);
		var allowsUav = (descriptor.Flags & BufferFlags.AllowUnorderedAccess) != 0;
		var isReadbackBuffer = descriptor.Usage.HasFlag(BufferUsage.Staging);
		var cpuWritableDirect = descriptor.Usage.HasFlag(BufferUsage.Constant);
		var cpuReadableDirect = isReadbackBuffer;
		var resourceFlags = allowsUav ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None;
		// D3D12 creates default-heap buffers in COMMON. Tracking another initial state
		// makes the first use skip its required transition because the debug layer
		// ignores the requested state for buffers.
		var initialState = ResourceStates.Common;

		var resourceDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = sizeInBytes,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = resourceFlags
		};

		var heapType = cpuReadableDirect
			? HeapType.Readback
			: cpuWritableDirect
				? HeapType.Upload
				: HeapType.Default;
		var defaultHeap = new HeapProperties(heapType);
		var defaultState = cpuReadableDirect
			? ResourceStates.CopyDest
			: cpuWritableDirect
				? ResourceStates.GenericRead
				: initialState;
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&defaultHeap,
			HeapFlags.None,
			in resourceDesc,
			defaultState,
			null,
			out ComPtr<ID3D12Resource> resource));

		ComPtr<ID3D12Resource> upload = default;
		if (cpuWritableDirect == false && cpuReadableDirect == false)
		{
			var uploadDesc = resourceDesc;
			// Upload heap buffers cannot use UAV resource flags.
			uploadDesc.Flags = ResourceFlags.None;

			var uploadHeap = new HeapProperties(HeapType.Upload);
			SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
				&uploadHeap,
				HeapFlags.None,
				in uploadDesc,
				ResourceStates.GenericRead,
				null,
				out upload));
		}

		var buffer = new D3D12Buffer(
			name: null,
			descriptor,
			resource,
			sizeInBytes,
			upload,
			cpuWritableDirect: cpuWritableDirect,
			cpuReadableDirect: cpuReadableDirect,
			flushUploadRange: cpuWritableDirect ? null : FlushUploadRange,
			getDeviceRemovedReason: () => _device.GetDeviceRemovedReason(),
			initialState: defaultState);
		return buffer;
	}

	public IGfxIndirectCommandBuffer CreateIndirectCommandBuffer(in IndirectCommandBufferDescriptor descriptor)
	{
		if (descriptor.PassKind != PassKind.Graphics)
		{
			throw new NotSupportedException("D3D12 indirect command buffers currently support graphics pass encoding only.");
		}

		var signature = EnsureGraphicsExecuteIndirectSignature();
		return new D3D12IndirectCommandBuffer(null, descriptor, _device, signature);
	}

	public IGfxBottomLevelAccelerationStructure CreateBottomLevelAccelerationStructure(
		in BottomLevelAccelerationStructureDescriptor descriptor)
	{
		if (SupportsRayTracing == false)
		{
			throw new NotSupportedException("The current Direct3D12 device does not support DXR.");
		}

		if (descriptor.VertexBuffer is not D3D12Buffer || descriptor.IndexBuffer is not D3D12Buffer)
		{
			throw new InvalidOperationException("Acceleration structure geometry buffers were not created by the Direct3D12 backend.");
		}
		if (descriptor.VertexStrideBytes < sizeof(float) * 3 ||
		    descriptor.VertexStrideBytes % (sizeof(float) * 3) != 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(descriptor),
				"DXR ray-tracing geometry requires a float3 position format with a stride that is a multiple of 12 bytes (DXGI_FORMAT_R32G32B32_FLOAT).");
		}
		D3D12RayTracingGeometryValidation.Validate(
			in descriptor,
			(D3D12Buffer)descriptor.VertexBuffer,
			(D3D12Buffer)descriptor.IndexBuffer);

		var geometry = CreateBottomLevelGeometry(descriptor);
		var inputs = new BuildRaytracingAccelerationStructureInputs
		{
			Type = RaytracingAccelerationStructureType.BottomLevel,
			Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
			NumDescs = 1,
			DescsLayout = ElementsLayout.Array
		};
		RaytracingAccelerationStructurePrebuildInfo prebuildInfo = default;
		var geometryPtr = &geometry;
		inputs.Anonymous.PGeometryDescs = geometryPtr;
		_rayTracingDevice.GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &prebuildInfo);
		var result = CreateRayTracingResultResource(prebuildInfo.ResultDataMaxSizeInBytes);
		var scratch = CreateRayTracingScratchResource(prebuildInfo.ScratchDataSizeInBytes);
		return new D3D12BottomLevelAccelerationStructure(descriptor, result, scratch);
	}

	public IGfxTopLevelAccelerationStructure CreateTopLevelAccelerationStructure(
		in TopLevelAccelerationStructureDescriptor descriptor)
	{
		if (SupportsRayTracing == false)
		{
			throw new NotSupportedException("The current Direct3D12 device does not support DXR.");
		}

		var inputs = new BuildRaytracingAccelerationStructureInputs
		{
			Type = RaytracingAccelerationStructureType.TopLevel,
			Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
			NumDescs = descriptor.MaxInstanceCount,
			DescsLayout = ElementsLayout.Array
		};
		RaytracingAccelerationStructurePrebuildInfo prebuildInfo = default;
		_rayTracingDevice.GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &prebuildInfo);
		var result = CreateRayTracingResultResource(prebuildInfo.ResultDataMaxSizeInBytes);
		var scratch = CreateRayTracingScratchResource(prebuildInfo.ScratchDataSizeInBytes);
		var instanceDescriptions = CreateUploadBuffer((ulong)descriptor.MaxInstanceCount * 64UL);
		return new D3D12TopLevelAccelerationStructure(descriptor, result, scratch, instanceDescriptions);
	}

	private RaytracingGeometryDesc CreateBottomLevelGeometry(
		in BottomLevelAccelerationStructureDescriptor descriptor)
	{
		var vertexBuffer = (D3D12Buffer)descriptor.VertexBuffer;
		var indexBuffer = (D3D12Buffer)descriptor.IndexBuffer;
		var geometry = new RaytracingGeometryDesc
		{
			Type = RaytracingGeometryType.Triangles,
			Flags = RaytracingGeometryFlags.Opaque
		};
		geometry.Anonymous.Triangles = new RaytracingGeometryTrianglesDesc
		{
			// DXR requires a stride compatible with the 12-byte float3 position format.
			VertexFormat = Format.FormatR32G32B32Float,
			VertexCount = descriptor.VertexCount,
			VertexBuffer = new GpuVirtualAddressAndStride
			{
				StartAddress = vertexBuffer.Resource.Handle->GetGPUVirtualAddress() + descriptor.VertexBufferOffsetBytes,
				StrideInBytes = descriptor.VertexStrideBytes
			},
			IndexFormat = Format.FormatR32Uint,
			IndexCount = descriptor.IndexCount,
			IndexBuffer = indexBuffer.Resource.Handle->GetGPUVirtualAddress() + descriptor.IndexBufferOffsetBytes
		};

		return geometry;
	}

	private ComPtr<ID3D12Resource> CreateRayTracingResultResource(ulong sizeInBytes)
	{
		return CreateRayTracingResource(sizeInBytes, ResourceStates.RaytracingAccelerationStructure);
	}

	private ComPtr<ID3D12Resource> CreateRayTracingScratchResource(ulong sizeInBytes)
	{
		return CreateRayTracingResource(sizeInBytes, ResourceStates.Common);
	}

	private ComPtr<ID3D12Resource> CreateRayTracingResource(ulong sizeInBytes, ResourceStates initialState)
	{
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = Align(Math.Max(sizeInBytes, 256UL), 256UL),
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.AllowUnorderedAccess
		};
		var heap = new HeapProperties(HeapType.Default);
		// RTAS resources are the sole exception: D3D12 requires their creation
		// state and forbids transitioning into RAYTRACING_ACCELERATION_STRUCTURE.
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(&heap, HeapFlags.None, in desc, initialState, null, out ComPtr<ID3D12Resource> resource));
		return resource;
	}

	private ComPtr<ID3D12Resource> CreateUploadBuffer(ulong sizeInBytes)
	{
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = Align(Math.Max(sizeInBytes, 256UL), 256UL),
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};
		var heap = new HeapProperties(HeapType.Upload);
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(&heap, HeapFlags.None, in desc, ResourceStates.GenericRead, null, out ComPtr<ID3D12Resource> resource));
		return resource;
	}

	private static bool SupportsInlineRayTracing(ComPtr<ID3D12Device> device)
	{
		FeatureDataD3D12Options5 options = default;
		var result = device.CheckFeatureSupport(Silk.NET.Direct3D12.Feature.D3D12Options5, &options, (uint)sizeof(FeatureDataD3D12Options5));
		return result >= 0 && options.RaytracingTier >= RaytracingTier.Tier11;
	}

	public IGfxPipeline GetOrCreatePipeline(PipelineKey key, in ShaderBytecodeSet shaders)
	{
		lock (_pipelineLock)
		{
			if (_pipelineCache.TryGetValue(key, out var cached))
			{
				return cached;
			}

			IGfxPipeline pipeline = key.PassKind switch
			{
				PassKind.Graphics => CreateGraphicsPipeline(key, shaders),
				PassKind.Compute => CreateComputePipeline(key, shaders),
				_ => throw new NotSupportedException($"Unsupported pass kind '{key.PassKind}'.")
			};

			_pipelineCache[key] = pipeline;
			return pipeline;
		}
	}

	public void ClearPipelineCache()
	{
		lock (_pipelineLock)
		{
			foreach (var pipeline in _pipelineCache.Values)
			{
				if (pipeline is D3D12Pipeline d3d12Pipeline)
					d3d12Pipeline.PipelineState.Dispose();
			}
			_pipelineCache.Clear();
		}
	}

	public IGfxDescriptorSetBuilder CreateDescriptorSetBuilder()
	{
		return new D3D12DescriptorSetBuilder(_device);
	}

	public IGfxDescriptorTable GlobalTable => _globalTable;

	internal ComPtr<ID3D12CommandSignature> DrawIndexedIndirectSignature => EnsureDrawIndexedIndirectSignature();

	internal ComPtr<ID3D12CommandSignature> GraphicsExecuteIndirectSignature => EnsureGraphicsExecuteIndirectSignature();

	internal D3D12DescriptorTable BindlessDescriptorTable => _globalTable;

	internal ComPtr<ID3D12Device> NativeDevice => _device;
	
	private static ulong Align(ulong size, ulong alignment)
	{
		return (size + alignment - 1) & ~(alignment - 1);
	}

	internal D3D12ConstantUploadPage RentConstantUploadPage(ulong requestedSize)
	{
		var alignedSize = Align(requestedSize, ConstantUploadAlignment);
		var pageSize = alignedSize > DefaultConstantUploadPageSize
			? alignedSize
			: DefaultConstantUploadPageSize;

		lock (_constantUploadLock)
		{
			_constantUploadStats = _constantUploadStats with
			{
				AllocationCount = _constantUploadStats.AllocationCount + 1,
				RequestedBytes = _constantUploadStats.RequestedBytes + requestedSize,
				CommittedPageBytesInUse = _constantUploadStats.CommittedPageBytesInUse + pageSize,
				OversizeDedicatedPageRents = _constantUploadStats.OversizeDedicatedPageRents + (pageSize > DefaultConstantUploadPageSize ? 1u : 0u)
			};

			if (_constantUploadPagePool.TryGetValue(pageSize, out var pages) && pages.Count > 0)
			{
				_constantUploadStats = _constantUploadStats with
				{
					PageReuses = _constantUploadStats.PageReuses + 1
				};
				return pages.Pop();
			}
		}

		var page = CreateConstantUploadPage(pageSize);
		lock (_constantUploadLock)
		{
			_constantUploadStats = _constantUploadStats with
			{
				NewPageCreations = _constantUploadStats.NewPageCreations + 1
			};
		}

		return page;
	}

	internal void RecycleConstantUploadPages(List<D3D12ConstantUploadPage> pages)
	{
		if (pages.Count == 0)
		{
			return;
		}

		lock (_constantUploadLock)
		{
			for (var i = 0; i < pages.Count; i++)
			{
				var page = pages[i];
				if (_constantUploadPagePool.TryGetValue(page.SizeInBytes, out var pool) == false)
				{
					pool = new Stack<D3D12ConstantUploadPage>();
					_constantUploadPagePool[page.SizeInBytes] = pool;
				}

				pool.Push(page);
			}
		}
	}

	private D3D12ConstantUploadPage CreateConstantUploadPage(ulong sizeInBytes)
	{
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = sizeInBytes,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};
		var heapProps = new HeapProperties(HeapType.Upload);
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&heapProps,
			HeapFlags.None,
			in desc,
			ResourceStates.GenericRead,
			null,
			out ComPtr<ID3D12Resource> resource));

		void* mapped = null;
		SilkMarshal.ThrowHResult(resource.Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped));
		return new D3D12ConstantUploadPage(resource, (byte*)mapped, sizeInBytes);
	}

	private void DisposeConstantUploadPages()
	{
		lock (_constantUploadLock)
		{
			foreach (var (_, pages) in _constantUploadPagePool)
			{
				while (pages.Count > 0)
				{
					pages.Pop().Dispose();
				}
			}

			_constantUploadPagePool.Clear();
		}
	}
	
	

	private IGfxCommandList CreateCommandList(CommandListType type)
	{
		D3D12CommandList? pooled = null;
		lock (_commandListLock)
		{
			if (_commandListPool.TryGetValue(type, out var queue) && queue.Count > 0)
			{
				pooled = queue.Dequeue();
			}
		}

		if (pooled is not null)
		{
			pooled.Reset();
			pooled.SetDebugName($"Unnamed {type}");
			return pooled;
		}

		SilkMarshal.ThrowHResult(_device.CreateCommandAllocator(type, out ComPtr<ID3D12CommandAllocator> allocator));

		SilkMarshal.ThrowHResult(
			_device.CreateCommandList<ID3D12CommandAllocator, ID3D12PipelineState, ID3D12GraphicsCommandList>(
				0,
				type,
				allocator,
				default,
				out ComPtr<ID3D12GraphicsCommandList> commandList));

		var wrapper = new D3D12CommandList(this, type, allocator, commandList);
		wrapper.SetDebugName($"Unnamed {type}");

		return wrapper;
	}

	private bool TryRentPooledTexture(in TextureDescriptor descriptor, ResourceStates desiredState, out IGfxTexture texture)
	{
		PooledTexture? pooled = null;

		lock (_texturePoolLock)
		{
			if (_texturePool.TryGetValue(descriptor, out var queue) && queue.Count > 0)
			{
				pooled = queue.Dequeue();
			}
		}

		if (pooled.HasValue == false)
		{
			texture = null!;
			return false;
		}

		if (pooled.Value.LastKnownState != desiredState)
		{
			TransitionResource(pooled.Value.Texture.Resource.Handle, pooled.Value.LastKnownState, desiredState);
		}

		texture = pooled.Value.Texture;
		return true;
	}

	private ExternalD3D12Texture RentExternalTextureWrapper()
	{
		lock (_texturePoolLock)
		{
			if (_externalTexturePool.Count > 0)
			{
				return _externalTexturePool.Dequeue();
			}
		}

		return new ExternalD3D12Texture();
	}

	private void CleanupCompletedCommandListsLocked()
	{
		if (_submissionFence.Handle is null)
		{
			return;
		}

		var completedFence = _submissionFence.Handle->GetCompletedValue();
		for (var i = _inFlightCommandLists.Count - 1; i >= 0; i--)
		{
			if (_inFlightCommandLists[i].FenceValue <= completedFence)
			{
				var completed = _inFlightCommandLists[i].CommandList;
				completed.CompleteGpuProfiling();
				completed.RecycleConstantUploadPages();
				if (_commandListPool.TryGetValue(completed.Type, out var pool) == false)
				{
					pool = new Queue<D3D12CommandList>();
					_commandListPool[completed.Type] = pool;
				}

				pool.Enqueue(completed);
				_inFlightCommandLists.RemoveAt(i);
			}
		}
	}

	public bool ReturnTexture(IGfxTexture texture, ResourceState lastKnownState)
	{
		switch (texture)
		{
			case D3D12Texture owned:
				var state = ToBackendState(lastKnownState);
				lock (_texturePoolLock)
				{
					if (_texturePool.TryGetValue(owned.Descriptor, out var queue) == false)
					{
						queue = new Queue<PooledTexture>();
						_texturePool[owned.Descriptor] = queue;
					}

					queue.Enqueue(new PooledTexture(owned, state));
				}

				return true;
			case ExternalD3D12Texture external:
				external.Reset();
				lock (_texturePoolLock)
				{
					_externalTexturePool.Enqueue(external);
				}

				return true;
			default:
				return false;
		}
	}

	public void ClearTexturePool()
	{
		lock (_texturePoolLock)
		{
			foreach (var queue in _texturePool.Values)
			{
				while (queue.Count > 0)
				{
					queue.Dequeue().Texture.Dispose();
				}
			}

			_texturePool.Clear();

			_externalTexturePool.Clear();
		}
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;
		WaitForIdle();

		lock (_commandListLock)
		{
			for (var i = _inFlightCommandLists.Count - 1; i >= 0; i--)
			{
				var inFlight = _inFlightCommandLists[i].CommandList;
				inFlight.RecycleConstantUploadPages();
				inFlight.Dispose();
			}

			_inFlightCommandLists.Clear();

			foreach (var (_, pool) in _commandListPool)
			{
				while (pool.Count > 0)
				{
					pool.Dequeue().Dispose();
				}
			}

			_commandListPool.Clear();
		}

		ClearTexturePool();
		DisposeConstantUploadPages();

		foreach (var pipeline in _pipelineCache.Values)
		{
			if (pipeline is D3D12Pipeline d3d12Pipeline)
			{
				d3d12Pipeline.PipelineState.Dispose();
			}
		}

		_pipelineCache.Clear();

		foreach (var rootSignature in _graphicsRootSignatures.Values)
		{
			if (rootSignature.Handle is not null)
			{
				rootSignature.Dispose();
			}
		}

		_graphicsRootSignatures.Clear();

		if (_computeRootSignature.Handle is not null)
		{
			_computeRootSignature.Dispose();
			_computeRootSignature = default;
		}

		if (_drawIndexedIndirectSignature.Handle is not null)
		{
			_drawIndexedIndirectSignature.Dispose();
			_drawIndexedIndirectSignature = default;
		}

		if (_graphicsExecuteIndirectSignature.Handle is not null)
		{
			_graphicsExecuteIndirectSignature.Dispose();
			_graphicsExecuteIndirectSignature = default;
		}

		if (_uploadCommandList.Handle is not null)
		{
			_uploadCommandList.Dispose();
			_uploadCommandList = default;
		}

		if (_uploadAllocator.Handle is not null)
		{
			_uploadAllocator.Dispose();
			_uploadAllocator = default;
		}

		_globalTable.Dispose();
		_gpuProfilerBackend.Dispose();

		if (_submissionFence.Handle is not null)
		{
			_submissionFence.Dispose();
		}

		if (_submissionFenceEvent != nint.Zero)
		{
			CloseHandle(_submissionFenceEvent);
			_submissionFenceEvent = nint.Zero;
		}

		if (_rayTracingDevice.Handle is not null)
		{
			_rayTracingDevice.Dispose();
		}
	}

	private static ResourceStates ToBackendState(ResourceState state)
	{
		var result = ResourceStates.Common;

		if ((state & ResourceState.Common) != 0)
		{
			result |= ResourceStates.Common;
		}

		if ((state & ResourceState.RenderTarget) != 0)
		{
			result |= ResourceStates.RenderTarget;
		}

		if ((state & ResourceState.DepthWrite) != 0)
		{
			result |= ResourceStates.DepthWrite;
		}

		if ((state & ResourceState.ShaderResource) != 0)
		{
			result |= ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
		}

		if ((state & ResourceState.UnorderedAccess) != 0)
		{
			result |= ResourceStates.UnorderedAccess;
		}

		if ((state & ResourceState.CopySource) != 0)
		{
			result |= ResourceStates.CopySource;
		}

		if ((state & ResourceState.CopyDestination) != 0)
		{
			result |= ResourceStates.CopyDest;
		}

		if ((state & ResourceState.IndirectArgument) != 0)
		{
			result |= ResourceStates.IndirectArgument;
		}

		if ((state & ResourceState.Present) != 0)
		{
			result |= ResourceStates.Present;
		}

		return result;
	}

	private void TransitionResource(ID3D12Resource* resource, ResourceStates before, ResourceStates after)
	{
		if (before == after)
		{
			return;
		}

		var commandList = (D3D12CommandList)CreateCommandList(CommandListType.Direct);
		commandList.TransitionResource(resource, before, after);
		var fenceValue = SubmitCommandList(commandList);
		lock (_commandListLock)
		{
			_inFlightCommandLists.Add(new(commandList, fenceValue));
		}

		WaitForFence(fenceValue);
		PumpCompleted();
	}

	private void FlushUploadRange(D3D12Buffer buffer, ulong byteOffset, ulong byteCount)
	{
		if (buffer.Resource.Handle is null || buffer.UploadResource.Handle is null || byteCount == 0)
		{
			return;
		}

		lock (_uploadLock)
		{
			EnsureUploadCommandList();
			SilkMarshal.ThrowHResult(_uploadAllocator.Reset());
			SilkMarshal.ThrowHResult(_uploadCommandList.Reset(_uploadAllocator, (ID3D12PipelineState*)null));

			var previousState = buffer.CurrentState;
			if (previousState != ResourceStates.CopyDest)
			{
				var toCopyDest = new ResourceBarrier
				{
					Type = ResourceBarrierType.Transition,
					Flags = ResourceBarrierFlags.None
				};
				toCopyDest.Anonymous.Transition = new ResourceTransitionBarrier
				{
					PResource = buffer.Resource.Handle,
					Subresource = D3D12Api.ResourceBarrierAllSubresources,
					StateBefore = previousState,
					StateAfter = ResourceStates.CopyDest
				};
				_uploadCommandList.ResourceBarrier(1, &toCopyDest);
			}

			_uploadCommandList.CopyBufferRegion(
				buffer.Resource,
				byteOffset,
				buffer.UploadResource,
				byteOffset,
				byteCount);

			if (previousState != ResourceStates.CopyDest)
			{
				var toOriginal = new ResourceBarrier
				{
					Type = ResourceBarrierType.Transition,
					Flags = ResourceBarrierFlags.None
				};
				toOriginal.Anonymous.Transition = new ResourceTransitionBarrier
				{
					PResource = buffer.Resource.Handle,
					Subresource = D3D12Api.ResourceBarrierAllSubresources,
					StateBefore = ResourceStates.CopyDest,
					StateAfter = previousState
				};
				_uploadCommandList.ResourceBarrier(1, &toOriginal);
			}

			SilkMarshal.ThrowHResult(_uploadCommandList.Close());
			ID3D12CommandList* uploadLists = (ID3D12CommandList*)_uploadCommandList.Handle;
			var fenceValue = ExecuteCommandList(_graphicsQueue, uploadLists);
			WaitForFence(fenceValue);
		}
	}

	private void EnsureUploadCommandList()
	{
		if (_uploadCommandList.Handle is not null)
		{
			return;
		}

		SilkMarshal.ThrowHResult(_device.CreateCommandAllocator(CommandListType.Direct, out _uploadAllocator));
		SilkMarshal.ThrowHResult(
			_device.CreateCommandList<ID3D12CommandAllocator, ID3D12PipelineState, ID3D12GraphicsCommandList>(
				0,
				CommandListType.Direct,
				_uploadAllocator,
				default,
				out _uploadCommandList));
		SilkMarshal.ThrowHResult(_uploadCommandList.Close());
	}

	public void WaitForIdle()
	{
		if (_submissionFence.Handle is null)
		{
			return;
		}

		ulong lastSubmitted;
		lock (_submissionLock)
		{
			lastSubmitted = _submissionFenceValue;
		}

		if (lastSubmitted != 0)
		{
			WaitForFence(lastSubmitted);
		}

		lock (_commandListLock)
		{
			CleanupCompletedCommandListsLocked();
		}
	}

	private ComPtr<ID3D12CommandSignature> EnsureDrawIndexedIndirectSignature()
	{
		if (_drawIndexedIndirectSignature.Handle is not null)
		{
			return _drawIndexedIndirectSignature;
		}

		var argument = new IndirectArgumentDesc
		{
			Type = IndirectArgumentType.DrawIndexed
		};

		var signatureDesc = new CommandSignatureDesc
		{
			ByteStride = (uint)sizeof(DrawIndexedArguments),
			NumArgumentDescs = 1,
			PArgumentDescs = &argument,
			NodeMask = 0
		};

		var nullRootSignature = default(ComPtr<ID3D12RootSignature>);
		SilkMarshal.ThrowHResult(
			_device.Handle->CreateCommandSignature(
				&signatureDesc,
				nullRootSignature,
				out _drawIndexedIndirectSignature));
		return _drawIndexedIndirectSignature;
	}

	private ComPtr<ID3D12CommandSignature> EnsureGraphicsExecuteIndirectSignature()
	{
		if (_graphicsExecuteIndirectSignature.Handle is not null)
		{
			return _graphicsExecuteIndirectSignature;
		}

		var argumentDescs = stackalloc IndirectArgumentDesc[11];

		argumentDescs[0].Type = IndirectArgumentType.VertexBufferView;
		argumentDescs[0].Anonymous.VertexBuffer.Slot = 0;

		argumentDescs[1].Type = IndirectArgumentType.IndexBufferView;

		argumentDescs[2].Type = IndirectArgumentType.ShaderResourceView;
		argumentDescs[2].Anonymous.ShaderResourceView.RootParameterIndex = D3D12RootBindings.Graphics.SrvT10;
		argumentDescs[3].Type = IndirectArgumentType.ShaderResourceView;
		argumentDescs[3].Anonymous.ShaderResourceView.RootParameterIndex = D3D12RootBindings.Graphics.SrvT11;
		argumentDescs[4].Type = IndirectArgumentType.ShaderResourceView;
		argumentDescs[4].Anonymous.ShaderResourceView.RootParameterIndex = D3D12RootBindings.Graphics.SrvT12;
		argumentDescs[5].Type = IndirectArgumentType.ShaderResourceView;
		argumentDescs[5].Anonymous.ShaderResourceView.RootParameterIndex = D3D12RootBindings.Graphics.SrvT13;
		argumentDescs[6].Type = IndirectArgumentType.ShaderResourceView;
		argumentDescs[6].Anonymous.ShaderResourceView.RootParameterIndex = D3D12RootBindings.Graphics.SrvT14;
		argumentDescs[7].Type = IndirectArgumentType.ShaderResourceView;
		argumentDescs[7].Anonymous.ShaderResourceView.RootParameterIndex = D3D12RootBindings.Graphics.SrvT15;
		argumentDescs[8].Type = IndirectArgumentType.ShaderResourceView;
		argumentDescs[8].Anonymous.ShaderResourceView.RootParameterIndex = D3D12RootBindings.Graphics.SrvT16;

		argumentDescs[9].Type = IndirectArgumentType.ConstantBufferView;
		argumentDescs[9].Anonymous.ConstantBufferView.RootParameterIndex = D3D12RootBindings.Graphics.CbvB16;

		argumentDescs[10].Type = IndirectArgumentType.DrawIndexed;

		var signatureDesc = new CommandSignatureDesc
		{
			ByteStride = (uint)sizeof(D3D12IndirectCommandBuffer.CommandRecord),
			NumArgumentDescs = 11,
			PArgumentDescs = argumentDescs,
			NodeMask = 0
		};

		var rootSignature = EnsureGraphicsRootSignature(GraphicsLayoutKind.Material);
		SilkMarshal.ThrowHResult(
			_device.Handle->CreateCommandSignature(
				&signatureDesc,
				rootSignature,
				out _graphicsExecuteIndirectSignature));
		return _graphicsExecuteIndirectSignature;
	}

	private void WaitForFence(ulong fenceValue)
	{
		lock (_submissionFenceWaitLock)
		{
			if (_submissionFence.Handle->GetCompletedValue() >= fenceValue)
			{
				return;
			}

			EnsureSubmissionFenceEvent();
			SilkMarshal.ThrowHResult(_submissionFence.Handle->SetEventOnCompletion(fenceValue, (void*) _submissionFenceEvent));
			WaitForSingleObject(_submissionFenceEvent, 0xFFFFFFFF);
		}
	}

	private void EnsureSubmissionFenceEvent()
	{
		if (_submissionFenceEvent != nint.Zero)
		{
			return;
		}

		_submissionFenceEvent = CreateEventEx(nint.Zero, null, 0, 0x1F0003);
		if (_submissionFenceEvent == nint.Zero)
		{
			throw new InvalidOperationException("Failed to create submission fence event.");
		}
	}

	private D3D12Pipeline CreateGraphicsPipeline(PipelineKey key, in ShaderBytecodeSet shaders)
	{
		if (shaders.Vertex.HasValue == false || shaders.Pixel.HasValue == false)
		{
			throw new InvalidOperationException("Graphics pipelines require both vertex and pixel shaders.");
		}

		var rootSignature = EnsureGraphicsRootSignature(key.Layout);

		var vertexShader = shaders.Vertex.Value.Span;
		var pixelShader = shaders.Pixel.Value.Span;

		Span<byte> positionSemantic = stackalloc byte["POSITION".Length + 1];
		Span<byte> normalSemantic = stackalloc byte["NORMAL".Length + 1];
		Span<byte> texCoordSemantic = stackalloc byte["TEXCOORD".Length + 1];
		Span<byte> tangentSemantic = stackalloc byte["TANGENT".Length + 1];
		CopySemantic("POSITION"u8, positionSemantic);
		CopySemantic("NORMAL"u8, normalSemantic);
		CopySemantic("TEXCOORD"u8, texCoordSemantic);
		CopySemantic("TANGENT"u8, tangentSemantic);

		var inputElements = stackalloc InputElementDesc[4];
		fixed (byte* positionPtr = positionSemantic)
		fixed (byte* normalPtr = normalSemantic)
		fixed (byte* texCoordPtr = texCoordSemantic)
		fixed (byte* tangentPtr = tangentSemantic)
		{
			inputElements[0] = default;
			inputElements[0].SemanticName = positionPtr;
			inputElements[0].SemanticIndex = 0;
			inputElements[0].Format = Format.FormatR32G32B32Float;
			inputElements[0].InputSlot = 0;
			inputElements[0].AlignedByteOffset = 0;
			inputElements[0].InputSlotClass = InputClassification.PerVertexData;
			inputElements[0].InstanceDataStepRate = 0;

			inputElements[1] = default;
			inputElements[1].SemanticName = normalPtr;
			inputElements[1].SemanticIndex = 0;
			inputElements[1].Format = Format.FormatR32G32B32Float;
			inputElements[1].InputSlot = 0;
			inputElements[1].AlignedByteOffset = 12;
			inputElements[1].InputSlotClass = InputClassification.PerVertexData;
			inputElements[1].InstanceDataStepRate = 0;

			inputElements[2] = default;
			inputElements[2].SemanticName = texCoordPtr;
			inputElements[2].SemanticIndex = 0;
			inputElements[2].Format = Format.FormatR32G32Float;
			inputElements[2].InputSlot = 0;
			inputElements[2].AlignedByteOffset = 24;
			inputElements[2].InputSlotClass = InputClassification.PerVertexData;
			inputElements[2].InstanceDataStepRate = 0;

			inputElements[3] = default;
			inputElements[3].SemanticName = tangentPtr;
			inputElements[3].SemanticIndex = 0;
			inputElements[3].Format = Format.FormatR32G32B32A32Float;
			inputElements[3].InputSlot = 0;
			inputElements[3].AlignedByteOffset = 32;
			inputElements[3].InputSlotClass = InputClassification.PerVertexData;
			inputElements[3].InstanceDataStepRate = 0;

			var inputLayout = new InputLayoutDesc
			{
				PInputElementDescs = inputElements,
				NumElements = 4
			};

			var renderState = CreateNormalizedRenderState(key.RenderState);
			var blendState = CreateBlendState(renderState, key.RenderTargets.Formats.Span.Length);
			var rasterizerState = CreateRasterizerState(renderState);
			var depthStencilState = CreateDepthStencilState(renderState);

			var targetFormats = key.RenderTargets.Formats.Span;
			var depthFormat = key.DepthStencil.Format != TextureFormat.Unknown
				? ToDxgiFormat(key.DepthStencil.Format)
				: Format.FormatUnknown;

			fixed (byte* vertexPtr = vertexShader)
			fixed (byte* pixelPtr = pixelShader)
			{
				var shaderBytecodeVS = new ShaderBytecode
				{
					PShaderBytecode = vertexPtr,
					BytecodeLength = (nuint) vertexShader.Length
				};

				var shaderBytecodePS = new ShaderBytecode
				{
					PShaderBytecode = pixelPtr,
					BytecodeLength = (nuint) pixelShader.Length
				};

				var psoDesc = new GraphicsPipelineStateDesc
				{
					PRootSignature = rootSignature.Handle,
					VS = shaderBytecodeVS,
					PS = shaderBytecodePS,
					BlendState = blendState,
					SampleMask = D3D12Api.DefaultSampleMask,
					RasterizerState = rasterizerState,
					DepthStencilState = depthStencilState,
					InputLayout = inputLayout,
					IBStripCutValue = IndexBufferStripCutValue.ValueDisabled,
					PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
					NumRenderTargets = (uint) targetFormats.Length,
					DSVFormat = depthFormat,
					SampleDesc = new SampleDesc(1, 0),
					NodeMask = 0,
					CachedPSO = default,
					Flags = PipelineStateFlags.None
				};

				for (var i = 0; i < targetFormats.Length; i++)
				{
					psoDesc.RTVFormats[i] = ToDxgiFormat(targetFormats[i]);
				}

				SilkMarshal.ThrowHResult(
					_device.CreateGraphicsPipelineState(in psoDesc, out ComPtr<ID3D12PipelineState> pipelineState));

				return new D3D12Pipeline(key, PassKind.Graphics, pipelineState, rootSignature);
			}
		}
	}

	private D3D12Pipeline CreateComputePipeline(PipelineKey key, in ShaderBytecodeSet shaders)
	{
		if (shaders.Compute.HasValue == false)
		{
			throw new InvalidOperationException("Compute pipelines require a compute shader.");
		}

		EnsureComputeRootSignature();

		var computeShader = shaders.Compute.Value.Span;
		fixed (byte* shaderPtr = computeShader)
		{
			var shaderBytecode = new ShaderBytecode
			{
				PShaderBytecode = shaderPtr,
				BytecodeLength = (nuint) computeShader.Length
			};

			var pipelineDesc = new ComputePipelineStateDesc
			{
				PRootSignature = _computeRootSignature.Handle,
				CS = shaderBytecode,
				NodeMask = 0,
				CachedPSO = default,
				Flags = PipelineStateFlags.None
			};

			SilkMarshal.ThrowHResult(
				_device.CreateComputePipelineState(in pipelineDesc, out ComPtr<ID3D12PipelineState> pipelineState));

		return new D3D12Pipeline(key, PassKind.Compute, pipelineState, _computeRootSignature);
	}
	}

	private ComPtr<ID3D12RootSignature> EnsureGraphicsRootSignature(GraphicsLayoutKind layout)
	{
		if (_graphicsRootSignatures.TryGetValue(layout, out var existing) && existing.Handle is not null)
		{
			return existing;
		}

		const uint maxSrvDescriptors = 16384;
		const uint maxUavDescriptors = 16384;
		const uint maxSamplerDescriptors = 2048;

		var ranges = stackalloc DescriptorRange[3];
		ranges[0] = new DescriptorRange
		{
			RangeType = DescriptorRangeType.Srv,
			NumDescriptors = maxSrvDescriptors,
			BaseShaderRegister = 0,
			RegisterSpace = 1,
			OffsetInDescriptorsFromTableStart = 0
		};
		ranges[1] = new DescriptorRange
		{
			RangeType = DescriptorRangeType.Uav,
			NumDescriptors = maxUavDescriptors,
			BaseShaderRegister = 0,
			RegisterSpace = 1,
			OffsetInDescriptorsFromTableStart = 0
		};
		ranges[2] = new DescriptorRange
		{
			RangeType = DescriptorRangeType.Sampler,
			NumDescriptors = maxSamplerDescriptors,
			BaseShaderRegister = 0,
			RegisterSpace = 1,
			OffsetInDescriptorsFromTableStart = 0
		};

		var rootParameters = stackalloc RootParameter[17];

		rootParameters[D3D12RootBindings.Graphics.BindlessSrvTable].ParameterType = RootParameterType.TypeDescriptorTable;
		rootParameters[D3D12RootBindings.Graphics.BindlessSrvTable].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
		rootParameters[D3D12RootBindings.Graphics.BindlessSrvTable].Anonymous.DescriptorTable.PDescriptorRanges = &ranges[0];
		rootParameters[D3D12RootBindings.Graphics.BindlessSrvTable].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.BindlessUavTable].ParameterType = RootParameterType.TypeDescriptorTable;
		rootParameters[D3D12RootBindings.Graphics.BindlessUavTable].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
		rootParameters[D3D12RootBindings.Graphics.BindlessUavTable].Anonymous.DescriptorTable.PDescriptorRanges = &ranges[1];
		rootParameters[D3D12RootBindings.Graphics.BindlessUavTable].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.BindlessSamplerTable].ParameterType = RootParameterType.TypeDescriptorTable;
		rootParameters[D3D12RootBindings.Graphics.BindlessSamplerTable].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
		rootParameters[D3D12RootBindings.Graphics.BindlessSamplerTable].Anonymous.DescriptorTable.PDescriptorRanges = &ranges[2];
		rootParameters[D3D12RootBindings.Graphics.BindlessSamplerTable].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.BindlessCountsCbv].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Graphics.BindlessCountsCbv].Anonymous.Descriptor = new RootDescriptor(27, 0);
		rootParameters[D3D12RootBindings.Graphics.BindlessCountsCbv].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.CbvB0].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Graphics.CbvB0].Anonymous.Descriptor = new RootDescriptor(0, 0);
		rootParameters[D3D12RootBindings.Graphics.CbvB0].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.CbvB2].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Graphics.CbvB2].Anonymous.Descriptor = new RootDescriptor(2, 0);
		rootParameters[D3D12RootBindings.Graphics.CbvB2].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.CbvB3].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Graphics.CbvB3].Anonymous.Descriptor = new RootDescriptor(3, 0);
		rootParameters[D3D12RootBindings.Graphics.CbvB3].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.CbvB4].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Graphics.CbvB4].Anonymous.Descriptor = new RootDescriptor(4, 0);
		rootParameters[D3D12RootBindings.Graphics.CbvB4].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.CbvB14].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Graphics.CbvB14].Anonymous.Descriptor = new RootDescriptor(14, 0);
		rootParameters[D3D12RootBindings.Graphics.CbvB14].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.CbvB16].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Graphics.CbvB16].Anonymous.Descriptor = new RootDescriptor(16, 0);
		rootParameters[D3D12RootBindings.Graphics.CbvB16].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.SrvT10].ParameterType = RootParameterType.TypeSrv;
		rootParameters[D3D12RootBindings.Graphics.SrvT10].Anonymous.Descriptor = new RootDescriptor(10, 0);
		rootParameters[D3D12RootBindings.Graphics.SrvT10].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.SrvT11].ParameterType = RootParameterType.TypeSrv;
		rootParameters[D3D12RootBindings.Graphics.SrvT11].Anonymous.Descriptor = new RootDescriptor(11, 0);
		rootParameters[D3D12RootBindings.Graphics.SrvT11].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.SrvT12].ParameterType = RootParameterType.TypeSrv;
		rootParameters[D3D12RootBindings.Graphics.SrvT12].Anonymous.Descriptor = new RootDescriptor(12, 0);
		rootParameters[D3D12RootBindings.Graphics.SrvT12].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.SrvT13].ParameterType = RootParameterType.TypeSrv;
		rootParameters[D3D12RootBindings.Graphics.SrvT13].Anonymous.Descriptor = new RootDescriptor(13, 0);
		rootParameters[D3D12RootBindings.Graphics.SrvT13].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.SrvT14].ParameterType = RootParameterType.TypeSrv;
		rootParameters[D3D12RootBindings.Graphics.SrvT14].Anonymous.Descriptor = new RootDescriptor(14, 0);
		rootParameters[D3D12RootBindings.Graphics.SrvT14].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.SrvT15].ParameterType = RootParameterType.TypeSrv;
		rootParameters[D3D12RootBindings.Graphics.SrvT15].Anonymous.Descriptor = new RootDescriptor(15, 0);
		rootParameters[D3D12RootBindings.Graphics.SrvT15].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Graphics.SrvT16].ParameterType = RootParameterType.TypeSrv;
		rootParameters[D3D12RootBindings.Graphics.SrvT16].Anonymous.Descriptor = new RootDescriptor(16, 0);
		rootParameters[D3D12RootBindings.Graphics.SrvT16].ShaderVisibility = ShaderVisibility.All;

		var rootSignatureDesc = new RootSignatureDesc
		{
			NumParameters = 17,
			PParameters = rootParameters,
			NumStaticSamplers = 0,
			PStaticSamplers = null,
			Flags = RootSignatureFlags.AllowInputAssemblerInputLayout
		};

		var versionedDesc = new VersionedRootSignatureDesc
		{
			Version = D3DRootSignatureVersion.Version10
		};
		versionedDesc.Anonymous.Desc10 = rootSignatureDesc;

		ID3D10Blob* rootSignatureBlob = null;
		ID3D10Blob* rootSignatureError = null;
		var serializeResult =
			_d3d12.SerializeVersionedRootSignature(&versionedDesc, &rootSignatureBlob, &rootSignatureError);
		try
		{
			HandleRootSignatureErrors(serializeResult, rootSignatureError, "graphics");

			SilkMarshal.ThrowHResult(_device.CreateRootSignature(
				0,
				rootSignatureBlob->GetBufferPointer(),
				rootSignatureBlob->GetBufferSize(),
				out ComPtr<ID3D12RootSignature> rootSignature));

			_graphicsRootSignatures[layout] = rootSignature;
			return rootSignature;
		}
		finally
		{
			if (rootSignatureBlob is not null)
			{
				rootSignatureBlob->Release();
			}
		}
	}

	private void EnsureComputeRootSignature()
	{
		if (_computeRootSignature.Handle is not null)
		{
			return;
		}

		const uint maxSrvDescriptors = 16384;
		const uint maxUavDescriptors = 16384;
		const uint maxSamplerDescriptors = 2048;

		var ranges = stackalloc DescriptorRange[3];
		ranges[0] = new DescriptorRange
		{
			RangeType = DescriptorRangeType.Srv,
			NumDescriptors = maxSrvDescriptors,
			BaseShaderRegister = 0,
			RegisterSpace = 1,
			OffsetInDescriptorsFromTableStart = 0
		};
		ranges[1] = new DescriptorRange
		{
			RangeType = DescriptorRangeType.Uav,
			NumDescriptors = maxUavDescriptors,
			BaseShaderRegister = 0,
			RegisterSpace = 1,
			OffsetInDescriptorsFromTableStart = 0
		};
		ranges[2] = new DescriptorRange
		{
			RangeType = DescriptorRangeType.Sampler,
			NumDescriptors = maxSamplerDescriptors,
			BaseShaderRegister = 0,
			RegisterSpace = 1,
			OffsetInDescriptorsFromTableStart = 0
		};

		var rootParameters = stackalloc RootParameter[32];

		rootParameters[D3D12RootBindings.Compute.BindlessSrvTable].ParameterType = RootParameterType.TypeDescriptorTable;
		rootParameters[D3D12RootBindings.Compute.BindlessSrvTable].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
		rootParameters[D3D12RootBindings.Compute.BindlessSrvTable].Anonymous.DescriptorTable.PDescriptorRanges = &ranges[0];
		rootParameters[D3D12RootBindings.Compute.BindlessSrvTable].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Compute.BindlessUavTable].ParameterType = RootParameterType.TypeDescriptorTable;
		rootParameters[D3D12RootBindings.Compute.BindlessUavTable].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
		rootParameters[D3D12RootBindings.Compute.BindlessUavTable].Anonymous.DescriptorTable.PDescriptorRanges = &ranges[1];
		rootParameters[D3D12RootBindings.Compute.BindlessUavTable].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Compute.BindlessSamplerTable].ParameterType = RootParameterType.TypeDescriptorTable;
		rootParameters[D3D12RootBindings.Compute.BindlessSamplerTable].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
		rootParameters[D3D12RootBindings.Compute.BindlessSamplerTable].Anonymous.DescriptorTable.PDescriptorRanges = &ranges[2];
		rootParameters[D3D12RootBindings.Compute.BindlessSamplerTable].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Compute.BindlessCountsCbv].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Compute.BindlessCountsCbv].Anonymous.Descriptor = new RootDescriptor(27, 0);
		rootParameters[D3D12RootBindings.Compute.BindlessCountsCbv].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Compute.CbvB0].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Compute.CbvB0].Anonymous.Descriptor = new RootDescriptor(0, 0);
		rootParameters[D3D12RootBindings.Compute.CbvB0].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Compute.CbvB1].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Compute.CbvB1].Anonymous.Descriptor = new RootDescriptor(1, 0);
		rootParameters[D3D12RootBindings.Compute.CbvB1].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Compute.CbvB2].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Compute.CbvB2].Anonymous.Descriptor = new RootDescriptor(2, 0);
		rootParameters[D3D12RootBindings.Compute.CbvB2].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Compute.CbvB11].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Compute.CbvB11].Anonymous.Descriptor = new RootDescriptor(11, 0);
		rootParameters[D3D12RootBindings.Compute.CbvB11].ShaderVisibility = ShaderVisibility.All;

		rootParameters[D3D12RootBindings.Compute.CbvB12].ParameterType = RootParameterType.TypeCbv;
		rootParameters[D3D12RootBindings.Compute.CbvB12].Anonymous.Descriptor = new RootDescriptor(12, 0);
		rootParameters[D3D12RootBindings.Compute.CbvB12].ShaderVisibility = ShaderVisibility.All;

		for (var u = 0u; u <= 11u; u++)
		{
			var rootIndex = D3D12RootBindings.Compute.UavU0 + u;
			rootParameters[rootIndex].ParameterType = RootParameterType.TypeUav;
			rootParameters[rootIndex].Anonymous.Descriptor = new RootDescriptor(u, 0);
			rootParameters[rootIndex].ShaderVisibility = ShaderVisibility.All;
		}

		for (var t = 2u; t <= 12u; t++)
		{
			var rootIndex = D3D12RootBindings.Compute.SrvT2 + t - 2;
			rootParameters[rootIndex].ParameterType = RootParameterType.TypeSrv;
			rootParameters[rootIndex].Anonymous.Descriptor = new RootDescriptor(t, 0);
			rootParameters[rootIndex].ShaderVisibility = ShaderVisibility.All;
		}

		var rootSignatureDesc = new RootSignatureDesc
		{
			NumParameters = 32,
			PParameters = rootParameters,
			NumStaticSamplers = 0,
			PStaticSamplers = null,
			Flags = RootSignatureFlags.None
		};

		var versionedDesc = new VersionedRootSignatureDesc
		{
			Version = D3DRootSignatureVersion.Version10
		};
		versionedDesc.Anonymous.Desc10 = rootSignatureDesc;

		ID3D10Blob* rootSignatureBlob = null;
		ID3D10Blob* rootSignatureError = null;
		var serializeResult =
			_d3d12.SerializeVersionedRootSignature(&versionedDesc, &rootSignatureBlob, &rootSignatureError);
		try
		{
			HandleRootSignatureErrors(serializeResult, rootSignatureError, "compute");

			SilkMarshal.ThrowHResult(_device.CreateRootSignature(
				0,
				rootSignatureBlob->GetBufferPointer(),
				rootSignatureBlob->GetBufferSize(),
				out _computeRootSignature));
		}
		finally
		{
			if (rootSignatureBlob is not null)
			{
				rootSignatureBlob->Release();
			}
		}
	}

	private static void HandleRootSignatureErrors(int result, ID3D10Blob* errorBlob, string kind)
	{
		string? errorMessage = null;
		if (errorBlob is not null)
		{
			errorMessage = Marshal.PtrToStringAnsi((nint) errorBlob->GetBufferPointer());
			errorBlob->Release();
		}

		if (result < 0)
		{
			throw new InvalidOperationException($"Failed to serialize {kind} root signature: {errorMessage ?? "Unknown error"}");
		}
	}

	private static void CopySemantic(ReadOnlySpan<byte> source, Span<byte> destination)
	{
		source.CopyTo(destination);
		destination[source.Length] = 0;
	}

	private static RenderStateDescriptor CreateNormalizedRenderState(RenderStateDescriptor state)
	{
		var defaultState = new RenderStateDescriptor(
			AbstractionFillMode.Solid,
			AbstractionCullMode.Back,
			depthTestEnabled: true,
			depthWriteEnabled: true,
			BlendMode.Opaque);

		return state.Equals(default) ? defaultState : state;
	}

	private static BlendDesc CreateBlendState(RenderStateDescriptor state, int targetCount)
	{
		var blendDesc = new BlendDesc
		{
			AlphaToCoverageEnable = 0,
			IndependentBlendEnable = 0
		};

		var count = Math.Max(1, targetCount);
		for (var i = 0; i < count; i++)
		{
			blendDesc.RenderTarget[i] = CreateRenderTargetBlendDesc(state.BlendMode);
		}

		return blendDesc;
	}

	private static RenderTargetBlendDesc CreateRenderTargetBlendDesc(BlendMode blendMode)
	{
		var desc = new RenderTargetBlendDesc
		{
			RenderTargetWriteMask = (byte) ColorWriteEnable.All,
			LogicOp = LogicOp.Noop,
			LogicOpEnable = 0
		};

		switch (blendMode)
		{
			case BlendMode.Additive:
				desc.BlendEnable = 1;
				desc.SrcBlend = Blend.One;
				desc.DestBlend = Blend.One;
				desc.BlendOp = BlendOp.Add;
				desc.SrcBlendAlpha = Blend.One;
				desc.DestBlendAlpha = Blend.One;
				desc.BlendOpAlpha = BlendOp.Add;
				break;
			case BlendMode.AlphaBlend:
				desc.BlendEnable = 1;
				desc.SrcBlend = Blend.SrcAlpha;
				desc.DestBlend = Blend.InvSrcAlpha;
				desc.BlendOp = BlendOp.Add;
				desc.SrcBlendAlpha = Blend.One;
				desc.DestBlendAlpha = Blend.One;
				desc.BlendOpAlpha = BlendOp.Add;
				break;
			default:
				desc.BlendEnable = 0;
				desc.SrcBlend = Blend.One;
				desc.DestBlend = Blend.Zero;
				desc.BlendOp = BlendOp.Add;
				desc.SrcBlendAlpha = Blend.One;
				desc.DestBlendAlpha = Blend.Zero;
				desc.BlendOpAlpha = BlendOp.Add;
				break;
		}

		return desc;
	}

	private static RasterizerDesc CreateRasterizerState(RenderStateDescriptor state)
	{
		return new RasterizerDesc
		{
			FillMode = state.FillMode switch
			{
				AbstractionFillMode.Wireframe => D3DFillMode.Wireframe,
				_ => D3DFillMode.Solid
			},
			CullMode = state.CullMode switch
			{
				AbstractionCullMode.None => D3DCullMode.None,
				AbstractionCullMode.Front => D3DCullMode.Front,
				_ => D3DCullMode.Back
			},
			FrontCounterClockwise = 0,
			DepthBias = D3D12Api.DefaultDepthBias,
			DepthBiasClamp = 0.0f,
			SlopeScaledDepthBias = 0.0f,
			DepthClipEnable = 1,
			MultisampleEnable = 0,
			AntialiasedLineEnable = 0,
			ForcedSampleCount = 0,
			ConservativeRaster = ConservativeRasterizationMode.Off
		};
	}

	private static DepthStencilDesc CreateDepthStencilState(RenderStateDescriptor state)
	{
		return new DepthStencilDesc
		{
			DepthEnable = state.DepthTestEnabled ? (byte) 1 : (byte) 0,
			DepthWriteMask = state.DepthWriteEnabled ? DepthWriteMask.All : DepthWriteMask.Zero,
			DepthFunc = ComparisonFunc.Less,
			StencilEnable = 0,
			StencilReadMask = D3D12Api.DefaultStencilReadMask,
			StencilWriteMask = D3D12Api.DefaultStencilWriteMask,
			FrontFace = new()
			{
				StencilFailOp = StencilOp.Keep,
				StencilDepthFailOp = StencilOp.Keep,
				StencilPassOp = StencilOp.Keep,
				StencilFunc = ComparisonFunc.Always
			},
			BackFace = new()
			{
				StencilFailOp = StencilOp.Keep,
				StencilDepthFailOp = StencilOp.Keep,
				StencilPassOp = StencilOp.Keep,
				StencilFunc = ComparisonFunc.Always
			}
		};
	}

	private sealed class ExternalD3D12Texture : ID3D12BackendTexture
	{
		public string? Name => null;

		public TextureDescriptor Descriptor { get; private set; }

		public DescriptorHandle ShaderResourceView { get; private set; } = DescriptorHandle.Invalid;

		public DescriptorHandle DepthShaderResourceView { get; private set; } = DescriptorHandle.Invalid;

		public DescriptorHandle UnorderedAccessView { get; private set; } = DescriptorHandle.Invalid;

		public ID3D12Resource* Resource { get; private set; }

		public CpuDescriptorHandle? RenderTargetView { get; private set; }

		public CpuDescriptorHandle? DepthStencilView { get; private set; }

		public void Initialize(TextureDescriptor descriptor, ID3D12Resource* resource, CpuDescriptorHandle? rtv,
			CpuDescriptorHandle? dsv)
		{
			Descriptor = descriptor;
			Resource = resource;
			RenderTargetView = rtv;
			DepthStencilView = dsv;
			ShaderResourceView = DescriptorHandle.Invalid;
			DepthShaderResourceView = DescriptorHandle.Invalid;
			UnorderedAccessView = DescriptorHandle.Invalid;
		}

		public void SetHandles(DescriptorHandle srv, DescriptorHandle depthSrv, DescriptorHandle uav)
		{
			ShaderResourceView = srv;
			DepthShaderResourceView = depthSrv;
			UnorderedAccessView = uav;
		}

		public void Reset()
		{
			Descriptor = default;
			Resource = null;
			RenderTargetView = null;
			DepthStencilView = null;
			ShaderResourceView = DescriptorHandle.Invalid;
			DepthShaderResourceView = DescriptorHandle.Invalid;
			UnorderedAccessView = DescriptorHandle.Invalid;
		}
	}
	

	private static Format ToDxgiFormat(TextureFormat format, bool isSrgb = false) => format switch
	{
		TextureFormat.Bgra8Unorm => isSrgb ? Format.FormatB8G8R8A8UnormSrgb : Format.FormatB8G8R8A8Unorm,
		TextureFormat.Rgba8Unorm => isSrgb ? Format.FormatR8G8B8A8UnormSrgb : Format.FormatR8G8B8A8Unorm,
		TextureFormat.Rgba8Uint => Format.FormatR8G8B8A8Uint,
		TextureFormat.R16Unorm => Format.FormatR16Unorm,
		TextureFormat.Rg16Float => Format.FormatR16G16Float,
		TextureFormat.Rgba16Float => Format.FormatR16G16B16A16Float,
		TextureFormat.R32Float => Format.FormatR32Float,
		TextureFormat.D32Float => Format.FormatD32Float,
		TextureFormat.Bc1Unorm => isSrgb ? Format.FormatBC1UnormSrgb : Format.FormatBC1Unorm,
		TextureFormat.Bc3Unorm => isSrgb ? Format.FormatBC3UnormSrgb : Format.FormatBC3Unorm,
		TextureFormat.Bc4Unorm => Format.FormatBC4Unorm,
		TextureFormat.Bc5Unorm => Format.FormatBC5Unorm,
		TextureFormat.Bc7Unorm => isSrgb ? Format.FormatBC7UnormSrgb : Format.FormatBC7Unorm,
		TextureFormat.Unknown => Format.FormatUnknown,
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported texture format.")
	};

	private static ResourceFlags DetermineResourceFlags(TextureUsage usage)
	{
		var flags = ResourceFlags.None;

		if ((usage & TextureUsage.RenderTarget) != 0)
		{
			flags |= ResourceFlags.AllowRenderTarget;
		}

		if ((usage & TextureUsage.DepthStencil) != 0)
		{
			flags |= ResourceFlags.AllowDepthStencil;
		}

		if ((usage & TextureUsage.UnorderedAccess) != 0)
		{
			flags |= ResourceFlags.AllowUnorderedAccess;
		}

		if ((usage & TextureUsage.ShaderResource) == 0)
		{
			flags |= ResourceFlags.DenyShaderResource;
		}

		return flags;
	}

	private static ResourceStates DetermineInitialState(TextureUsage usage)
	{
		if ((usage & TextureUsage.RenderTarget) != 0)
		{
			return ResourceStates.RenderTarget;
		}

		if ((usage & TextureUsage.DepthStencil) != 0)
		{
			return ResourceStates.DepthWrite;
		}

		if ((usage & TextureUsage.UnorderedAccess) != 0)
		{
			return ResourceStates.UnorderedAccess;
		}

		return ResourceStates.Common;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern nint CreateEventEx(nint lpEventAttributes, string? lpName, uint dwFlags, uint dwDesiredAccess);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(nint hObject);
}
