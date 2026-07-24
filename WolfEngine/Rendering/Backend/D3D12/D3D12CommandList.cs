using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Profiling;

using AbstractionViewport = WolfEngine.Rendering.Abstraction.Viewport;
using AbstractionVertexBufferView = WolfEngine.Rendering.Abstraction.VertexBufferView;
using AbstractionIndexBufferView = WolfEngine.Rendering.Abstraction.IndexBufferView;
using AbstractionDrawArguments = WolfEngine.Rendering.Abstraction.DrawArguments;
using D3D12Api = Silk.NET.Direct3D12.D3D12;

namespace WolfEngine.Rendering.Backend.D3D12;

internal unsafe class D3D12CommandList : IGfxCommandList, IDisposable
{
	private readonly D3D12Device _owner;
	private readonly CpuDescriptorHandle[] _currentRtvHandles = new CpuDescriptorHandle[8];
	private readonly List<D3D12ConstantUploadPage> _constantUploadPages = new();
	private D3D12DescriptorTable? _bindlessTable;
	private D3D12ConstantUploadPage? _currentConstantUploadPage;
	private PassKind? _activePipelineKind;
	private uint _currentRtvCount;
	private CpuDescriptorHandle? _currentDsvHandle;
	private ulong _currentConstantUploadOffset;
	private bool _isClosed;
	private bool _bindlessHeapsDirty = true;
	private D3D12GpuProfilerBackend? _gpuProfiler;
	private GpuProfilePassCapture? _gpuPassCapture;
	private readonly List<D3D12GpuProfilerBackend.TimestampBlock> _gpuTimestampBlocks = new();
	private readonly List<GpuTimestampScope> _gpuTimestampScopes = new();
	private D3D12GpuProfilerBackend.TimestampBlock? _activeGpuBlock;
	private uint _activeGpuStartIndex;
	private string? _activeGpuScopeName;
	private bool _gpuProfilingFailed;

	public D3D12CommandList(
		D3D12Device owner,
		CommandListType type,
		ComPtr<ID3D12CommandAllocator> allocator,
		ComPtr<ID3D12GraphicsCommandList> commandList)
	{
		_owner = owner;
		Type = type;
		Allocator = allocator;
		CommandList = commandList;
	}

	public CommandListType Type { get; }

	public ComPtr<ID3D12CommandAllocator> Allocator { get; }

	public ComPtr<ID3D12GraphicsCommandList> CommandList { get; }

	public ID3D12GraphicsCommandList* NativeCommandList => CommandList.Handle;

	public GraphicsBackendKind BackendKind => GraphicsBackendKind.D3D12;

	public void Close()
	{
		if (_isClosed)
		{
			return;
		}

		CloseGpuScope();
		for (var i = 0; i < _gpuTimestampBlocks.Count; i++)
		{
			var block = _gpuTimestampBlocks[i];
			if (block.UsedSamples > 0)
			{
				CommandList.ResolveQueryData(
					block.Heap,
					QueryType.Timestamp,
					0,
					block.UsedSamples,
					block.Readback.Resource,
					0);
			}
		}
		SilkMarshal.ThrowHResult(CommandList.Close());
		_isClosed = true;
	}

	public void Dispose()
	{
		RecycleConstantUploadPages();
		CommandList.Dispose();
		Allocator.Dispose();
	}

	public void Reset()
	{
		SilkMarshal.ThrowHResult(Allocator.Reset());
		SilkMarshal.ThrowHResult(CommandList.Reset(Allocator, (ID3D12PipelineState*)null));
		_isClosed = false;
		_currentRtvCount = 0;
		_currentDsvHandle = null;
		_activePipelineKind = null;
		_bindlessHeapsDirty = true;
		_currentConstantUploadPage = null;
		_currentConstantUploadOffset = 0;
		_gpuProfiler = null;
		_gpuPassCapture = null;
		_gpuTimestampScopes.Clear();
		_gpuTimestampBlocks.Clear();
		_activeGpuBlock = null;
		_activeGpuScopeName = null;
		_gpuProfilingFailed = false;
	}

	internal void SetDebugName(string name)
	{
		// Debug names are diagnostic metadata and must never turn a renderable frame into a failure.
		_ = CommandList.SetName(name);
	}

	public void BeginPass(in PassTargets targets, in AbstractionViewport viewport)
	{
		var nativeViewport = new Silk.NET.Direct3D12.Viewport
		{
			TopLeftX = viewport.X,
			TopLeftY = viewport.Y,
			Width = viewport.Width,
			Height = viewport.Height,
			MinDepth = viewport.MinDepth,
			MaxDepth = viewport.MaxDepth
		};

		CommandList.RSSetViewports(1, &nativeViewport);

		var left = (int)Math.Floor(viewport.X);
		var top = (int)Math.Floor(viewport.Y);
		var right = (int)Math.Ceiling(viewport.X + viewport.Width);
		var bottom = (int)Math.Ceiling(viewport.Y + viewport.Height);
		var scissor = new Box2D<int>(left, top, right, bottom);
		CommandList.RSSetScissorRects(1, &scissor);

		var colorCount = targets.ColorAttachments.Count;
		_currentRtvCount = (uint)colorCount;
		CpuDescriptorHandle* dsvHandle = null;
		CpuDescriptorHandle depthStorage = default;
		if (targets.DepthAttachment is DepthTargetBinding depthBinding)
		{
			if (depthBinding.Texture is not ID3D12BackendTexture depthTexture ||
			    depthTexture.DepthStencilView is null)
			{
				throw new InvalidOperationException("Depth attachment was not provided by the Direct3D12 backend.");
			}

			depthStorage = depthTexture.DepthStencilView.Value;
			dsvHandle = &depthStorage;
			_currentDsvHandle = depthStorage;
		}
		else
		{
			_currentDsvHandle = null;
		}

		var singleHandle = new Bool32(0);
		if (colorCount > 0)
		{
			Span<CpuDescriptorHandle> rtvSpan = stackalloc CpuDescriptorHandle[colorCount];
			for (var i = 0; i < colorCount; i++)
			{
				if (targets.ColorAttachments[i].Texture is not ID3D12BackendTexture texture ||
				    texture.RenderTargetView is null)
				{
					throw new InvalidOperationException(
						"Render target attachment was not provided by the Direct3D12 backend.");
				}

				rtvSpan[i] = texture.RenderTargetView.Value;
				_currentRtvHandles[i] = rtvSpan[i];
			}

			fixed (CpuDescriptorHandle* rtvPtr = rtvSpan)
			{
				CommandList.OMSetRenderTargets((uint)colorCount, rtvPtr, singleHandle, dsvHandle);
			}

			return;
		}

		CommandList.OMSetRenderTargets(0, (CpuDescriptorHandle*)null, singleHandle, dsvHandle);
		_currentRtvCount = 0;
	}

	public void EndPass()
	{
		_currentRtvCount = 0;
		_currentDsvHandle = null;
	}

	public void BindPipeline(IGfxPipeline pipeline)
	{
		BeginGpuScope(pipeline.Key);
		if (pipeline is not D3D12Pipeline nativePipeline)
		{
			throw new InvalidOperationException("Pipeline was not created by the Direct3D12 backend.");
		}

		CommandList.SetPipelineState(nativePipeline.PipelineState.Handle);
		_activePipelineKind = nativePipeline.Kind;

		if (nativePipeline.Kind == PassKind.Graphics)
		{
			CommandList.SetGraphicsRootSignature(nativePipeline.RootSignature.Handle);
		}
		else
		{
			CommandList.SetComputeRootSignature(nativePipeline.RootSignature.Handle);
		}

		EnsureBindlessDescriptorHeaps();
		ApplyBindlessRootBindings();
	}

	internal void AttachGpuProfiler(D3D12GpuProfilerBackend profiler, GpuProfilePassCapture passCapture)
	{
		_gpuProfiler = profiler;
		_gpuPassCapture = passCapture;
	}

	internal void CompleteGpuProfiling()
	{
		if (_gpuProfiler is null || _gpuPassCapture is null)
		{
			return;
		}

		try
		{
			if (_gpuProfilingFailed)
			{
				_gpuPassCapture.Complete(Array.Empty<GpuProfileScope>());
			}
			else
			{
				var blockResults = new Dictionary<D3D12GpuProfilerBackend.TimestampBlock, ulong[]>(_gpuTimestampBlocks.Count);
				for (var i = 0; i < _gpuTimestampBlocks.Count; i++)
				{
					blockResults[_gpuTimestampBlocks[i]] = _gpuTimestampBlocks[i].ReadResults();
				}
				var scopes = new List<GpuProfileScope>(_gpuTimestampScopes.Count);
				for (var i = 0; i < _gpuTimestampScopes.Count; i++)
				{
					var scope = _gpuTimestampScopes[i];
					var values = blockResults[scope.Block];
					scopes.Add(new GpuProfileScope(
						scope.Name,
						_gpuProfiler.TicksToMilliseconds(values[scope.StartIndex], values[scope.EndIndex])));
				}
				_gpuPassCapture.Complete(scopes);
			}
		}
		catch (Exception exception)
		{
			_gpuProfiler.ReportFailure(exception);
			_gpuPassCapture.Complete(Array.Empty<GpuProfileScope>());
		}
		finally
		{
			_gpuProfiler.ReturnBlocks(_gpuTimestampBlocks);
			_gpuTimestampScopes.Clear();
			_gpuPassCapture = null;
			_gpuProfiler = null;
		}
	}

	private void BeginGpuScope(in PipelineKey key)
	{
		if (_gpuProfiler is null || _gpuProfilingFailed)
		{
			return;
		}
		CloseGpuScope();
		D3D12GpuProfilerBackend.TimestampBlock block;
		try
		{
			block = GetGpuTimestampBlock();
		}
		catch (Exception exception)
		{
			_gpuProfiler.ReportFailure(exception);
			_gpuProfilingFailed = true;
			return;
		}
		_activeGpuStartIndex = block.UsedSamples++;
		CommandList.EndQuery(block.Heap, QueryType.Timestamp, _activeGpuStartIndex);
		_activeGpuBlock = block;
		_activeGpuScopeName = GpuProfileNames.FromPipeline(key);
	}

	private void CloseGpuScope()
	{
		if (_activeGpuBlock is null || _activeGpuScopeName is null)
		{
			return;
		}
		var endIndex = _activeGpuBlock.UsedSamples++;
		CommandList.EndQuery(_activeGpuBlock.Heap, QueryType.Timestamp, endIndex);
		_gpuTimestampScopes.Add(new GpuTimestampScope(
			_activeGpuScopeName,
			_activeGpuBlock,
			_activeGpuStartIndex,
			endIndex));
		_activeGpuBlock = null;
		_activeGpuScopeName = null;
	}

	private D3D12GpuProfilerBackend.TimestampBlock GetGpuTimestampBlock()
	{
		if (_gpuTimestampBlocks.Count == 0 ||
		    _gpuTimestampBlocks[^1].UsedSamples + 2 > D3D12GpuProfilerBackend.SamplesPerBlock)
		{
			_gpuTimestampBlocks.Add(_gpuProfiler!.RentBlock());
		}
		return _gpuTimestampBlocks[^1];
	}

	private readonly record struct GpuTimestampScope(
		string Name,
		D3D12GpuProfilerBackend.TimestampBlock Block,
		uint StartIndex,
		uint EndIndex);

	public void SetBindlessTable(IGfxDescriptorTable table)
	{
		if (table is not D3D12DescriptorTable d3d12Table)
		{
			throw new InvalidOperationException("Bindless table was not created by the Direct3D12 backend.");
		}

		_bindlessTable = d3d12Table;
		_bindlessHeapsDirty = true;
		EnsureBindlessDescriptorHeaps();
		ApplyBindlessRootBindings();
	}

	public void BindGraphicsDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet)
	{
		if (descriptorSet is not D3D12DescriptorSet d3dDescriptorSet)
		{
			throw new InvalidOperationException("Descriptor set was not created by the Direct3D12 backend.");
		}

		var heapPtr = stackalloc ID3D12DescriptorHeap*[1];
		heapPtr[0] = d3dDescriptorSet.DescriptorHeap.Handle;
		CommandList.SetDescriptorHeaps(1, heapPtr);
		_bindlessHeapsDirty = true;

		var handle = d3dDescriptorSet.GetGpuHandle(0);
		CommandList.SetGraphicsRootDescriptorTable(slot, handle);
	}

	public void BindComputeDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet)
	{
		if (descriptorSet is not D3D12DescriptorSet d3dDescriptorSet)
		{
			throw new InvalidOperationException("Descriptor set was not created by the Direct3D12 backend.");
		}

		var heapPtr = stackalloc ID3D12DescriptorHeap*[1];
		heapPtr[0] = d3dDescriptorSet.DescriptorHeap.Handle;
		CommandList.SetDescriptorHeaps(1, heapPtr);
		_bindlessHeapsDirty = true;

		var handle = d3dDescriptorSet.GetGpuHandle(0);
		CommandList.SetComputeRootDescriptorTable(slot, handle);
	}

	public void BindConstantBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0)
	{
		if (_activePipelineKind != PassKind.Graphics)
		{
			throw new InvalidOperationException("BindConstantBuffer requires a bound graphics pipeline on the D3D12 backend.");
		}

		if (buffer is not D3D12Buffer d3d12Buffer)
		{
			throw new InvalidOperationException("Buffer was not created by the Direct3D12 backend.");
		}

		var resource = d3d12Buffer.Resource.Handle;
		if (resource is null)
		{
			throw new InvalidOperationException("Buffer resource was null.");
		}

		var gpuAddress = resource->GetGPUVirtualAddress() + offset;
		if (D3D12RootBindings.TryGetGraphicsSrvIndex(slot, out var srvRootIndex))
		{
			if (d3d12Buffer.IsCpuWritableDirect == false)
			{
				TransitionBufferIfNeeded(
					d3d12Buffer,
					ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
			}

			CommandList.SetGraphicsRootShaderResourceView(srvRootIndex, gpuAddress);
			return;
		}

		if (D3D12RootBindings.TryGetGraphicsCbvIndex(slot, out var cbvRootIndex))
		{
			if (d3d12Buffer.IsCpuWritableDirect == false)
			{
				TransitionBufferIfNeeded(d3d12Buffer, ResourceStates.VertexAndConstantBuffer);
			}

			CommandList.SetGraphicsRootConstantBufferView(cbvRootIndex, gpuAddress);
			return;
		}

		CommandList.SetGraphicsRootConstantBufferView(slot, gpuAddress);
	}

	public void SetGraphicsConstants(uint slot, ReadOnlySpan<byte> data)
	{
		if (data.IsEmpty)
		{
			return;
		}

		if (data.Length % 4 != 0)
		{
			throw new ArgumentException("Data size must be a multiple of 4 bytes.", nameof(data));
		}

		if (D3D12RootBindings.TryGetGraphicsCbvIndex(slot, out var rootIndex))
		{
			var gpuAddress = UploadConstants(data);
			CommandList.SetGraphicsRootConstantBufferView(rootIndex, gpuAddress);
			return;
		}

		var num32BitValues = (uint)data.Length / 4;
		fixed (byte* dataPtr = data)
		{
			CommandList.SetGraphicsRoot32BitConstants(slot, num32BitValues, dataPtr, 0);
		}
	}

	public void SetComputeConstants(uint slot, ReadOnlySpan<byte> data)
	{
		if (data.IsEmpty)
		{
			return;
		}

		if (data.Length % 4 != 0)
		{
			throw new ArgumentException("Data size must be a multiple of 4 bytes.", nameof(data));
		}

		if (D3D12RootBindings.TryGetComputeCbvIndex(slot, out var rootIndex))
		{
			var gpuAddress = UploadConstants(data);
			CommandList.SetComputeRootConstantBufferView(rootIndex, gpuAddress);
			return;
		}

		var num32BitValues = (uint)data.Length / 4;
		fixed (byte* dataPtr = data)
		{
			CommandList.SetComputeRoot32BitConstants(slot, num32BitValues, dataPtr, 0);
		}
	}

	public void SetComputeBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0)
	{
		if (buffer is not D3D12Buffer d3d12Buffer || d3d12Buffer.Resource.Handle is null)
		{
			throw new InvalidOperationException("Buffer was not created by the Direct3D12 backend.");
		}

		if (D3D12RootBindings.TryGetComputeUavIndex(slot, out var rootIndex) == false)
		{
			throw new NotSupportedException($"Compute buffer slot {slot} is not supported by the D3D12 root signature.");
		}

		if (d3d12Buffer.IsCpuWritableDirect == false)
		{
			TransitionBufferIfNeeded(d3d12Buffer, ResourceStates.UnorderedAccess);
		}

		var gpuAddress = d3d12Buffer.Resource.Handle->GetGPUVirtualAddress() + offset;
		CommandList.SetComputeRootUnorderedAccessView(rootIndex, gpuAddress);
	}

	public void SetComputeReadOnlyBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0)
	{
		if (buffer is not D3D12Buffer d3d12Buffer || d3d12Buffer.Resource.Handle is null)
		{
			throw new InvalidOperationException("Buffer was not created by the Direct3D12 backend.");
		}

		if (D3D12RootBindings.TryGetComputeSrvIndex(slot, out var rootIndex) == false)
		{
			throw new NotSupportedException($"Compute read-only buffer slot {slot} is not supported by the D3D12 root signature.");
		}

		if (d3d12Buffer.IsCpuWritableDirect == false)
		{
			TransitionBufferIfNeeded(d3d12Buffer, ResourceStates.NonPixelShaderResource);
		}

		var gpuAddress = d3d12Buffer.Resource.Handle->GetGPUVirtualAddress() + offset;
		CommandList.SetComputeRootShaderResourceView(rootIndex, gpuAddress);
	}

	public void SetPrimitiveTopology(PrimitiveTopology topology)
	{
		var d3d12Topology = topology switch
		{
			PrimitiveTopology.TriangleList => D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist,
			PrimitiveTopology.TriangleStrip => D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglestrip,
			PrimitiveTopology.LineList => D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist,
			PrimitiveTopology.LineStrip => D3DPrimitiveTopology.D3DPrimitiveTopologyLinestrip,
			PrimitiveTopology.PointList => D3DPrimitiveTopology.D3DPrimitiveTopologyPointlist,
			_ => throw new ArgumentOutOfRangeException(nameof(topology), topology, "Unsupported primitive topology.")
		};

		CommandList.IASetPrimitiveTopology(d3d12Topology);
	}

	public void SetScissorRect(in RectInt rect)
	{
		var scissor = new Box2D<int>(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
		CommandList.RSSetScissorRects(1, &scissor);
	}

	public void ClearColorAttachment(uint index, ColorRGBA color)
	{
		if (index >= _currentRtvCount)
		{
			throw new ArgumentOutOfRangeException(nameof(index));
		}

		var colorValues = stackalloc float[4] { color.R, color.G, color.B, color.A };
		CommandList.ClearRenderTargetView(_currentRtvHandles[index], colorValues, 0, (Box2D<int>*)null);
	}

	public void ClearDepthStencil(float depth)
	{
		if (_currentDsvHandle.HasValue == false)
		{
			return;
		}

		CommandList.ClearDepthStencilView(_currentDsvHandle.Value, ClearFlags.Depth, depth, 0, 0, (Box2D<int>*)null);
	}

	public void PushConstants<T>(in T data) where T : unmanaged
	{
		ReadOnlySpan<byte> bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
			System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
				ref System.Runtime.CompilerServices.Unsafe.AsRef(in data),
				1));
		SetGraphicsConstants(0, bytes);
	}

	public void SetVertexBuffer(in AbstractionVertexBufferView vertexBuffer)
	{
		if (vertexBuffer.Buffer is not D3D12Buffer buffer)
		{
			throw new InvalidOperationException("Vertex buffer was not created by the Direct3D12 backend.");
		}

		var resource = buffer.Resource.Handle;
		if (resource is null)
		{
			throw new InvalidOperationException("Vertex buffer resource was null.");
		}

		if (buffer.IsCpuWritableDirect == false)
		{
			TransitionBufferIfNeeded(buffer, ResourceStates.VertexAndConstantBuffer);
		}

		var gpuAddress = resource->GetGPUVirtualAddress();
		var size = buffer.SizeInBytes;
		var start = vertexBuffer.Offset;
		if (start >= size)
		{
			throw new ArgumentOutOfRangeException(nameof(vertexBuffer), "Vertex buffer offset exceeds buffer size.");
		}

		var remaining = size - start;
		var spanSize = Math.Min(remaining, uint.MaxValue);
		var view = new Silk.NET.Direct3D12.VertexBufferView
		{
			BufferLocation = gpuAddress + start,
			StrideInBytes = vertexBuffer.Stride,
			SizeInBytes = (uint)spanSize
		};

		CommandList.IASetVertexBuffers(0, 1, &view);
	}

	public void SetVertexBuffers(ReadOnlySpan<AbstractionVertexBufferView> vertexBuffers)
	{
		if (vertexBuffers.IsEmpty)
		{
			return;
		}

		var views = stackalloc Silk.NET.Direct3D12.VertexBufferView[vertexBuffers.Length];

		for (var i = 0; i < vertexBuffers.Length; i++)
		{
			var view = vertexBuffers[i];

			if (view.Buffer is not D3D12Buffer buffer)
			{
				throw new InvalidOperationException("Vertex buffer was not created by the Direct3D12 backend.");
			}

			var resource = buffer.Resource.Handle;
			if (resource is null)
			{
				throw new InvalidOperationException("Vertex buffer resource was null.");
			}

			if (buffer.IsCpuWritableDirect == false)
			{
				TransitionBufferIfNeeded(buffer, ResourceStates.VertexAndConstantBuffer);
			}

			var gpuAddress = resource->GetGPUVirtualAddress();
			var size = buffer.SizeInBytes;
			var start = view.Offset;
			if (start >= size)
			{
				throw new ArgumentOutOfRangeException(nameof(vertexBuffers), "Vertex buffer offset exceeds buffer size.");
			}

			var remaining = size - start;
			var spanSize = Math.Min(remaining, uint.MaxValue);

			views[i] = new Silk.NET.Direct3D12.VertexBufferView
			{
				BufferLocation = gpuAddress + start,
				StrideInBytes = view.Stride,
				SizeInBytes = (uint)spanSize
			};
		}

		CommandList.IASetVertexBuffers(0, (uint)vertexBuffers.Length, views);
	}

	public void SetIndexBuffer(in AbstractionIndexBufferView indexBuffer)
	{
		if (indexBuffer.Buffer is not D3D12Buffer buffer)
		{
			throw new InvalidOperationException("Index buffer was not created by the Direct3D12 backend.");
		}

		var resource = buffer.Resource.Handle;
		if (resource is null)
		{
			throw new InvalidOperationException("Index buffer resource was null.");
		}

		if (buffer.IsCpuWritableDirect == false)
		{
			TransitionBufferIfNeeded(buffer, ResourceStates.IndexBuffer);
		}

		var gpuAddress = resource->GetGPUVirtualAddress();
		var size = buffer.SizeInBytes;
		var offset = indexBuffer.Offset;
		if (offset >= size)
		{
			throw new ArgumentOutOfRangeException(nameof(indexBuffer), "Index buffer offset exceeds buffer size.");
		}

		var remaining = size - offset;
		var spanSize = Math.Min(remaining, uint.MaxValue);

		var format = indexBuffer.Format switch
		{
			IndexFormat.UInt16 => Format.FormatR16Uint,
			IndexFormat.UInt32 => Format.FormatR32Uint,
			_ => throw new ArgumentOutOfRangeException(nameof(indexBuffer), indexBuffer.Format, "Unsupported index format.")
		};

		var view = new Silk.NET.Direct3D12.IndexBufferView
		{
			BufferLocation = gpuAddress + offset,
			SizeInBytes = (uint)spanSize,
			Format = format
		};

		CommandList.IASetIndexBuffer(&view);
	}

	public void Draw(in AbstractionDrawArguments arguments)
	{
		EnsureBindlessDescriptorHeaps();
		ApplyBindlessRootBindings();
		CommandList.DrawIndexedInstanced(
			arguments.IndexCount,
			arguments.InstanceCount,
			arguments.StartIndex,
			arguments.BaseVertex,
			arguments.StartInstance);
	}

	public void DrawIndexedIndirect(in AbstractionIndexBufferView indexBuffer, IGfxBuffer indirectArgsBuffer, ulong indirectArgsOffset)
	{
		if (indirectArgsBuffer is not D3D12Buffer argsBuffer || argsBuffer.Resource.Handle is null)
		{
			throw new InvalidOperationException("Indirect args buffer was not created by the Direct3D12 backend.");
		}

		SetIndexBuffer(indexBuffer);
		EnsureBindlessDescriptorHeaps();
		ApplyBindlessRootBindings();

		var previousState = argsBuffer.CurrentState;
		TransitionBufferIfNeeded(argsBuffer, ResourceStates.IndirectArgument);
		CommandList.ExecuteIndirect(
			_owner.DrawIndexedIndirectSignature,
			1,
			argsBuffer.Resource,
			indirectArgsOffset,
			(ID3D12Resource*)null,
			0);
		TransitionBufferIfNeeded(argsBuffer, previousState);
	}

	public void ExecuteIndirectCommandBuffer(IGfxIndirectCommandBuffer commandBuffer, uint maxCommandCount)
	{
		if (commandBuffer is not D3D12IndirectCommandBuffer d3d12CommandBuffer)
		{
			throw new InvalidOperationException("Indirect command buffer was not created by the Direct3D12 backend.");
		}

		var maxAvailable = Math.Min(maxCommandCount, d3d12CommandBuffer.Descriptor.MaxCommandCount);
		if (maxAvailable == 0)
		{
			return;
		}

		EnsureBindlessDescriptorHeaps();
		ApplyBindlessRootBindings();
		TransitionIndirectReferencedBuffers(d3d12CommandBuffer);
		CommandList.ExecuteIndirect(
			d3d12CommandBuffer.CommandSignature,
			maxAvailable,
			d3d12CommandBuffer.ArgumentBuffer,
			0,
			(ID3D12Resource*)null,
			0);
	}

	public void ExecuteIndirectCommandBufferRange(
		IGfxIndirectCommandBuffer commandBuffer,
		IGfxBuffer commandRangeBuffer,
		ulong commandRangeOffsetBytes)
	{
		if (commandBuffer is not D3D12IndirectCommandBuffer d3d12CommandBuffer)
		{
			throw new InvalidOperationException("Indirect command buffer was not created by the Direct3D12 backend.");
		}

		if (commandRangeBuffer is not D3D12Buffer countBuffer || countBuffer.Resource.Handle is null)
		{
			throw new InvalidOperationException("Command count/range buffer was not created by the Direct3D12 backend.");
		}

		EnsureBindlessDescriptorHeaps();
		ApplyBindlessRootBindings();
		TransitionIndirectReferencedBuffers(d3d12CommandBuffer);

		// Range mode: consume {start,count} and execute from command 0 using the count value only.
		var countOffset = commandRangeOffsetBytes + sizeof(uint);
		var previousState = countBuffer.CurrentState;
		TransitionBufferIfNeeded(countBuffer, ResourceStates.IndirectArgument);
		CommandList.ExecuteIndirect(
			d3d12CommandBuffer.CommandSignature,
			d3d12CommandBuffer.Descriptor.MaxCommandCount,
			d3d12CommandBuffer.ArgumentBuffer,
			0,
			countBuffer.Resource,
			countOffset);
		TransitionBufferIfNeeded(countBuffer, previousState);
	}

	public void BuildBottomLevelAccelerationStructure(IGfxBottomLevelAccelerationStructure accelerationStructure)
	{
		if (accelerationStructure is not D3D12BottomLevelAccelerationStructure blas)
		{
			throw new InvalidOperationException("Bottom-level acceleration structure was not created by the Direct3D12 backend.");
		}

		var descriptor = blas.Descriptor;
		var vertexBuffer = descriptor.VertexBuffer as D3D12Buffer
			?? throw new InvalidOperationException("BLAS vertex buffer was not created by the Direct3D12 backend.");
		var indexBuffer = descriptor.IndexBuffer as D3D12Buffer
			?? throw new InvalidOperationException("BLAS index buffer was not created by the Direct3D12 backend.");
		D3D12RayTracingGeometryValidation.Validate(in descriptor, vertexBuffer, indexBuffer);
		TransitionBufferIfNeeded(vertexBuffer, ResourceStates.NonPixelShaderResource);
		TransitionBufferIfNeeded(indexBuffer, ResourceStates.NonPixelShaderResource);
		TransitionResource(
			blas.Result.Handle,
			blas.ResultState,
			ResourceStates.RaytracingAccelerationStructure);
		blas.ResultState = ResourceStates.RaytracingAccelerationStructure;
		TransitionResource(blas.Scratch.Handle, blas.ScratchState, ResourceStates.UnorderedAccess);
		blas.ScratchState = ResourceStates.UnorderedAccess;

		var geometry = CreateBottomLevelGeometry(descriptor, vertexBuffer, indexBuffer);
		var geometryPtr = &geometry;
		var inputs = new BuildRaytracingAccelerationStructureInputs
		{
			Type = RaytracingAccelerationStructureType.BottomLevel,
			Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
			NumDescs = 1,
			DescsLayout = ElementsLayout.Array
		};
		inputs.Anonymous.PGeometryDescs = geometryPtr;
		BuildAccelerationStructure(inputs, blas.Result.Handle, blas.Scratch.Handle);
	}

	public void BuildTopLevelAccelerationStructure(
		IGfxTopLevelAccelerationStructure accelerationStructure,
		ReadOnlySpan<RayTracingInstanceDescription> instances)
	{
		if (accelerationStructure is not D3D12TopLevelAccelerationStructure tlas)
		{
			throw new InvalidOperationException("Top-level acceleration structure was not created by the Direct3D12 backend.");
		}

		var count = Math.Min((uint)instances.Length, tlas.Descriptor.MaxInstanceCount);
		WriteInstanceDescriptions(tlas, instances, count);
		for (var i = 0; i < count; i++)
		{
			if (instances[i].AccelerationStructure is D3D12BottomLevelAccelerationStructure blas)
			{
				InsertUavBarrier(blas.Result.Handle);
			}
		}
		TransitionResource(
			tlas.Result.Handle,
			tlas.ResultState,
			ResourceStates.RaytracingAccelerationStructure);
		tlas.ResultState = ResourceStates.RaytracingAccelerationStructure;
		TransitionResource(tlas.Scratch.Handle, tlas.ScratchState, ResourceStates.UnorderedAccess);
		tlas.ScratchState = ResourceStates.UnorderedAccess;

		var inputs = new BuildRaytracingAccelerationStructureInputs
		{
			Type = RaytracingAccelerationStructureType.TopLevel,
			Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
			NumDescs = count,
			DescsLayout = ElementsLayout.Array
		};
		inputs.Anonymous.InstanceDescs = tlas.InstanceDescriptions.Handle->GetGPUVirtualAddress();
		BuildAccelerationStructure(inputs, tlas.Result.Handle, tlas.Scratch.Handle);
	}

	public void SynchronizeAccelerationStructureBuildForComputeRead(IGfxTopLevelAccelerationStructure accelerationStructure)
	{
		if (accelerationStructure is not D3D12TopLevelAccelerationStructure tlas)
		{
			throw new InvalidOperationException("Top-level acceleration structure was not created by the Direct3D12 backend.");
		}

		InsertUavBarrier(tlas.Result.Handle);
	}

	public void SetComputeAccelerationStructure(uint slot, IGfxTopLevelAccelerationStructure accelerationStructure)
	{
		if (accelerationStructure is not D3D12TopLevelAccelerationStructure tlas)
		{
			throw new InvalidOperationException("Top-level acceleration structure was not created by the Direct3D12 backend.");
		}
		if (D3D12RootBindings.TryGetComputeSrvIndex(slot, out var rootIndex) == false)
		{
			throw new NotSupportedException($"Compute acceleration structure slot {slot} is not supported by the D3D12 root signature.");
		}

		CommandList.SetComputeRootShaderResourceView(rootIndex, tlas.Result.Handle->GetGPUVirtualAddress());
	}

	private void BuildAccelerationStructure(
		in BuildRaytracingAccelerationStructureInputs inputs,
		ID3D12Resource* result,
		ID3D12Resource* scratch)
	{
		var commandList4 = CommandList.QueryInterface<ID3D12GraphicsCommandList4>();
		try
		{
			var build = new BuildRaytracingAccelerationStructureDesc
			{
				DestAccelerationStructureData = result->GetGPUVirtualAddress(),
				ScratchAccelerationStructureData = scratch->GetGPUVirtualAddress(),
				Inputs = inputs
			};
			commandList4.BuildRaytracingAccelerationStructure(in build, 0, null);
			InsertUavBarrier(result);
		}
		finally
		{
			commandList4.Dispose();
		}
	}

	private static RaytracingGeometryDesc CreateBottomLevelGeometry(
		in BottomLevelAccelerationStructureDescriptor descriptor,
		D3D12Buffer vertexBuffer,
		D3D12Buffer indexBuffer)
	{
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

	private static void WriteInstanceDescriptions(
		D3D12TopLevelAccelerationStructure tlas,
		ReadOnlySpan<RayTracingInstanceDescription> instances,
		uint count)
	{
		void* mapped = null;
		SilkMarshal.ThrowHResult(tlas.InstanceDescriptions.Handle->Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped));
		try
		{
			var destination = new Span<D3D12RayTracingInstanceData>(mapped, checked((int)tlas.Descriptor.MaxInstanceCount));
			destination.Clear();
			for (var i = 0; i < count; i++)
			{
				if (instances[i].AccelerationStructure is not D3D12BottomLevelAccelerationStructure blas)
				{
					throw new InvalidOperationException("TLAS instance references a BLAS not created by the Direct3D12 backend.");
				}
				destination[i] = D3D12RayTracingInstanceData.Create(instances[i], blas.Result.Handle->GetGPUVirtualAddress());
			}
		}
		finally
		{
			tlas.InstanceDescriptions.Handle->Unmap(0, (Silk.NET.Direct3D12.Range*)null);
		}
	}

	private void InsertUavBarrier(ID3D12Resource* resource)
	{
		var barrier = new ResourceBarrier { Type = ResourceBarrierType.Uav };
		barrier.Anonymous.UAV = new ResourceUavBarrier { PResource = resource };
		CommandList.ResourceBarrier(1, &barrier);
	}

	internal void TransitionResource(
		ID3D12Resource* resource,
		ResourceStates before,
		ResourceStates after)
	{
		if (resource is null || before == after)
		{
			return;
		}

		var barrier = new ResourceBarrier { Type = ResourceBarrierType.Transition };
		barrier.Anonymous.Transition = new ResourceTransitionBarrier
		{
			PResource = resource,
			Subresource = D3D12Api.ResourceBarrierAllSubresources,
			StateBefore = before,
			StateAfter = after
		};
		CommandList.ResourceBarrier(1, &barrier);
	}

	public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
	{
		EnsureBindlessDescriptorHeaps();
		ApplyBindlessRootBindings();
		CommandList.Dispatch(groupCountX, groupCountY, groupCountZ);
	}

	public void CopyBuffer(IGfxBuffer source, ulong sourceOffset, IGfxBuffer destination, ulong destinationOffset, ulong sizeInBytes)
	{
		if (sizeInBytes == 0)
		{
			return;
		}

		if (source is not D3D12Buffer sourceBuffer || sourceBuffer.Resource.Handle is null)
		{
			throw new InvalidOperationException("Source buffer was not created by the Direct3D12 backend.");
		}

		if (destination is not D3D12Buffer destinationBuffer || destinationBuffer.Resource.Handle is null)
		{
			throw new InvalidOperationException("Destination buffer was not created by the Direct3D12 backend.");
		}

		var previousSourceState = sourceBuffer.CurrentState;
		TransitionBufferIfNeeded(sourceBuffer, ResourceStates.CopySource);
		if (destinationBuffer.IsCpuReadableDirect == false)
		{
			TransitionBufferIfNeeded(destinationBuffer, ResourceStates.CopyDest);
		}

		CommandList.CopyBufferRegion(
			destinationBuffer.Resource,
			destinationOffset,
			sourceBuffer.Resource,
			sourceOffset,
			sizeInBytes);

		TransitionBufferIfNeeded(sourceBuffer, previousSourceState);
	}

	public void SetDescriptorHeaps(ComPtr<ID3D12DescriptorHeap>[] heaps)
	{
		var heapPtrs = stackalloc ID3D12DescriptorHeap*[heaps.Length];
		for (var i = 0; i < heaps.Length; i++)
		{
			heapPtrs[i] = heaps[i].Handle;
		}

		CommandList.SetDescriptorHeaps((uint)heaps.Length, heapPtrs);
	}

	public void SetComputeRootDescriptorTable(uint rootParameterIndex, GpuDescriptorHandle baseDescriptor)
	{
		CommandList.SetComputeRootDescriptorTable(rootParameterIndex, baseDescriptor);
	}

	private void EnsureBindlessDescriptorHeaps()
	{
		if (_bindlessTable is null || _bindlessHeapsDirty == false)
		{
			return;
		}

		var heaps = stackalloc ID3D12DescriptorHeap*[2];
		heaps[0] = _bindlessTable.DescriptorHeap.Handle;
		heaps[1] = _bindlessTable.SamplerHeap.Handle;
		CommandList.SetDescriptorHeaps(2, heaps);
		_bindlessHeapsDirty = false;
	}

	private void ApplyBindlessRootBindings()
	{
		if (_bindlessTable is null || _activePipelineKind.HasValue == false)
		{
			return;
		}

		if (_activePipelineKind.Value == PassKind.Graphics)
		{
			CommandList.SetGraphicsRootDescriptorTable(
				D3D12RootBindings.Graphics.BindlessSrvTable,
				_bindlessTable.SrvTableStart);
			CommandList.SetGraphicsRootDescriptorTable(
				D3D12RootBindings.Graphics.BindlessUavTable,
				_bindlessTable.UavTableStart);
			CommandList.SetGraphicsRootDescriptorTable(
				D3D12RootBindings.Graphics.BindlessSamplerTable,
				_bindlessTable.SamplerTableStart);
			if (_bindlessTable.CountsBufferGpuAddress != 0)
			{
				CommandList.SetGraphicsRootConstantBufferView(
					D3D12RootBindings.Graphics.BindlessCountsCbv,
					_bindlessTable.CountsBufferGpuAddress);
			}

			return;
		}

		CommandList.SetComputeRootDescriptorTable(
			D3D12RootBindings.Compute.BindlessSrvTable,
			_bindlessTable.SrvTableStart);
		CommandList.SetComputeRootDescriptorTable(
			D3D12RootBindings.Compute.BindlessUavTable,
			_bindlessTable.UavTableStart);
		CommandList.SetComputeRootDescriptorTable(
			D3D12RootBindings.Compute.BindlessSamplerTable,
			_bindlessTable.SamplerTableStart);
		if (_bindlessTable.CountsBufferGpuAddress != 0)
		{
			CommandList.SetComputeRootConstantBufferView(
				D3D12RootBindings.Compute.BindlessCountsCbv,
				_bindlessTable.CountsBufferGpuAddress);
		}
	}

	private ulong UploadConstants(ReadOnlySpan<byte> data)
	{
		var alignedSize = Align((ulong)data.Length, 256);
		if (_currentConstantUploadPage is null ||
		    _currentConstantUploadOffset + alignedSize > _currentConstantUploadPage.SizeInBytes)
		{
			_currentConstantUploadPage = _owner.RentConstantUploadPage(alignedSize);
			_constantUploadPages.Add(_currentConstantUploadPage);
			_currentConstantUploadOffset = 0;
		}

		var page = _currentConstantUploadPage;
		var destination = page.MappedData + (nint)_currentConstantUploadOffset;
		fixed (byte* source = data)
		{
			Buffer.MemoryCopy(source, destination, alignedSize, (ulong)data.Length);
		}

		var gpuAddress = page.GpuAddress + _currentConstantUploadOffset;
		_currentConstantUploadOffset += alignedSize;
		return gpuAddress;
	}

	internal void RecycleConstantUploadPages()
	{
		if (_constantUploadPages.Count > 0)
		{
			_owner.RecycleConstantUploadPages(_constantUploadPages);
			_constantUploadPages.Clear();
		}

		_currentConstantUploadPage = null;
		_currentConstantUploadOffset = 0;
	}

	private static ulong Align(ulong size, ulong alignment)
	{
		return (size + alignment - 1) & ~(alignment - 1);
	}

	private void TransitionBufferIfNeeded(D3D12Buffer buffer, ResourceStates targetState)
	{
		if (buffer.Resource.Handle is null || (buffer.CurrentState & targetState) == targetState)
		{
			return;
		}

		var transition = new ResourceTransitionBarrier
		{
			PResource = buffer.Resource.Handle,
			Subresource = D3D12Api.ResourceBarrierAllSubresources,
			StateBefore = buffer.CurrentState,
			StateAfter = targetState
		};

		var native = new ResourceBarrier
		{
			Type = ResourceBarrierType.Transition,
			Flags = ResourceBarrierFlags.None
		};
		native.Anonymous.Transition = transition;
		CommandList.ResourceBarrier(1, &native);
		buffer.CurrentState = targetState;
	}

	private void TransitionIndirectReferencedBuffers(D3D12IndirectCommandBuffer commandBuffer)
	{
		foreach (var (buffer, requiredState) in commandBuffer.ReferencedBufferStates)
		{
			if (buffer.IsCpuWritableDirect == false)
			{
				TransitionBufferIfNeeded(buffer, requiredState);
			}
		}
	}

	private static ResourceStates ConvertResourceState(ResourceState state)
	{
		if (state == ResourceState.None)
		{
			return ResourceStates.Common;
		}

		ResourceStates result = 0;

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

		return result == 0 ? ResourceStates.Common : result;
	}

	public void Barrier(in ResourceBarrierDescription barrier)
	{
		ID3D12Resource* resource = null;
		D3D12Buffer? trackedBuffer = null;
		if (barrier.Resource is ID3D12BackendTexture texture)
		{
			resource = texture.Resource;
		}
		else if (barrier.Resource is D3D12Buffer buffer)
		{
			resource = buffer.Resource.Handle;
			trackedBuffer = buffer;
		}
		else
		{
			throw new InvalidOperationException("Resource barrier targeted an unsupported resource type.");
		}

		if (resource is null)
		{
			throw new InvalidOperationException("Resource pointer was null while issuing a barrier.");
		}

		var before = ConvertResourceState(barrier.Before);
		var after = ConvertResourceState(barrier.After);
		if (before == after)
		{
			return;
		}

		var transition = new ResourceTransitionBarrier
		{
			PResource = resource,
			Subresource = D3D12Api.ResourceBarrierAllSubresources,
			StateBefore = before,
			StateAfter = after
		};

		var native = new ResourceBarrier
		{
			Type = ResourceBarrierType.Transition,
			Flags = ResourceBarrierFlags.None
		};
		native.Anonymous.Transition = transition;

		CommandList.ResourceBarrier(1, &native);
		if (trackedBuffer is not null)
		{
			trackedBuffer.CurrentState = after;
		}
	}
}
