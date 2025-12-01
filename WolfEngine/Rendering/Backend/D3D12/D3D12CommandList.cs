using System.Numerics;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using WolfEngine.Rendering.Abstraction;

using AbstractionViewport = WolfEngine.Rendering.Abstraction.Viewport;
using AbstractionVertexBufferView = WolfEngine.Rendering.Abstraction.VertexBufferView;
using AbstractionIndexBufferView = WolfEngine.Rendering.Abstraction.IndexBufferView;
using AbstractionDrawArguments = WolfEngine.Rendering.Abstraction.DrawArguments;
using D3D12Api = Silk.NET.Direct3D12.D3D12;

namespace WolfEngine.Rendering.Backend.D3D12;

internal unsafe class D3D12CommandList : IGfxCommandList, IDisposable
{
	private readonly CpuDescriptorHandle[] _currentRtvHandles = new CpuDescriptorHandle[8];
	private uint _currentRtvCount;
	private CpuDescriptorHandle? _currentDsvHandle;
	private bool _isClosed;

	public D3D12CommandList(CommandListType type, ComPtr<ID3D12CommandAllocator> allocator,
		ComPtr<ID3D12GraphicsCommandList> commandList)
	{
		Type = type;
		Allocator = allocator;
		CommandList = commandList;
	}

	public CommandListType Type { get; }

	public ComPtr<ID3D12CommandAllocator> Allocator { get; }

	public ComPtr<ID3D12GraphicsCommandList> CommandList { get; }

	public ID3D12GraphicsCommandList* NativeCommandList => CommandList.Handle;

	public void Close()
	{
		if (_isClosed)
		{
			return;
		}

		SilkMarshal.ThrowHResult(CommandList.Close());
		_isClosed = true;
	}

	public void Dispose()
	{
		CommandList.Dispose();
		Allocator.Dispose();
	}

	public void Reset()
	{
		SilkMarshal.ThrowHResult(Allocator.Reset());
		SilkMarshal.ThrowHResult(CommandList.Reset(Allocator, (ID3D12PipelineState*) null));
		_isClosed = false;
		_currentRtvCount = 0;
		_currentDsvHandle = null;
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

		var left = (int) Math.Floor(viewport.X);
		var top = (int) Math.Floor(viewport.Y);
		var right = (int) Math.Ceiling(viewport.X + viewport.Width);
		var bottom = (int) Math.Ceiling(viewport.Y + viewport.Height);
		var scissor = new Box2D<int>(left, top, right, bottom);
		CommandList.RSSetScissorRects(1, &scissor);

		var colorCount = targets.ColorAttachments.Count;
		_currentRtvCount = (uint) colorCount;
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
				CommandList.OMSetRenderTargets((uint) colorCount, rtvPtr, singleHandle, dsvHandle);
			}

			return;
		}

		CommandList.OMSetRenderTargets(0, (CpuDescriptorHandle*) null, singleHandle, dsvHandle);
		_currentRtvCount = 0;
	}

	public void EndPass()
	{
		// No-op for now. The application is responsible for inserting any necessary barriers.
		_currentRtvCount = 0;
		_currentDsvHandle = null;
	}

	public void BindPipeline(IGfxPipeline pipeline)
	{
		if (pipeline is not D3D12Pipeline nativePipeline)
		{
			throw new InvalidOperationException("Pipeline was not created by the Direct3D12 backend.");
		}

		CommandList.SetPipelineState(nativePipeline.PipelineState.Handle);

		if (nativePipeline.Kind == PassKind.Graphics)
		{
			CommandList.SetGraphicsRootSignature(nativePipeline.RootSignature.Handle);
		}
		else
		{
			CommandList.SetComputeRootSignature(nativePipeline.RootSignature.Handle);
		}
	}

	public void SetBindlessTable(IGfxDescriptorTable table)
	{
		throw new NotSupportedException(
			"Bindless descriptor tables are not yet implemented for the Direct3D12 backend.");
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

		var handle = d3dDescriptorSet.GetGpuHandle(0);
		CommandList.SetComputeRootDescriptorTable(slot, handle);
	}

	public void BindConstantBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0)
	{
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
		CommandList.SetGraphicsRootConstantBufferView(slot, gpuAddress);
	}

	public void SetGraphicsConstants(uint slot, ReadOnlySpan<byte> data)
	{
		if (data.IsEmpty)
		{
			return;
		}

		var num32BitValues = (uint)data.Length / 4;
		if (data.Length % 4 != 0)
		{
			throw new ArgumentException("Data size must be a multiple of 4 bytes.", nameof(data));
		}

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

		var num32BitValues = (uint)data.Length / 4;
		if (data.Length % 4 != 0)
		{
			throw new ArgumentException("Data size must be a multiple of 4 bytes.", nameof(data));
		}

		fixed (byte* dataPtr = data)
		{
			CommandList.SetComputeRoot32BitConstants(slot, num32BitValues, dataPtr, 0);
		}
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

	public void ClearColorAttachment(uint index, Vector4 color)
	{
		if (index >= _currentRtvCount)
		{
			throw new ArgumentOutOfRangeException(nameof(index));
		}

		var colorValues = stackalloc float[4] { color.X, color.Y, color.Z, color.W };
		CommandList.ClearRenderTargetView(_currentRtvHandles[index], colorValues, 0, (Box2D<int>*) null);
	}

	public void ClearDepthStencil(float depth)
	{
		if (_currentDsvHandle.HasValue == false)
		{
			return;
		}

		CommandList.ClearDepthStencilView(_currentDsvHandle.Value, ClearFlags.Depth, depth, 0, 0, (Box2D<int>*) null);
	}

	public void PushConstants<T>(in T data) where T : unmanaged
	{
		ReadOnlySpan<byte> bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref System.Runtime.CompilerServices.Unsafe.AsRef(in data), 1));
		SetGraphicsConstants(0, bytes);
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

			var gpuAddress = resource->GetGPUVirtualAddress();
			var size = buffer.SizeInBytes;
			var start = view.Offset;
			if (start >= size)
			{
				throw new ArgumentOutOfRangeException(nameof(vertexBuffers),
					"Vertex buffer offset exceeds buffer size.");
			}

			var remaining = size - start;
			var spanSize = Math.Min(remaining, uint.MaxValue);

			views[i] = new()
			{
				BufferLocation = gpuAddress + start,
				StrideInBytes = view.Stride,
				SizeInBytes = (uint) spanSize
			};
		}

		CommandList.IASetVertexBuffers(0, (uint) vertexBuffers.Length, views);
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
			_ => throw new ArgumentOutOfRangeException(nameof(indexBuffer), indexBuffer.Format,
				"Unsupported index format.")
		};

		var view = new Silk.NET.Direct3D12.IndexBufferView
		{
			BufferLocation = gpuAddress + offset,
			SizeInBytes = (uint) spanSize,
			Format = format
		};

		CommandList.IASetIndexBuffer(&view);
	}

	public void Draw(in AbstractionDrawArguments arguments)
	{
		CommandList.DrawIndexedInstanced(
			arguments.IndexCount,
			arguments.InstanceCount,
			arguments.StartIndex,
			arguments.BaseVertex,
			arguments.StartInstance);
	}

	public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
	{
		CommandList.Dispatch(groupCountX, groupCountY, groupCountZ);
	}

	/// <summary>
	/// Sets the descriptor heaps to be used by the command list.
	/// This is a D3D12-specific operation required before any descriptor tables can be bound.
	/// </summary>
	public void SetDescriptorHeaps(ComPtr<ID3D12DescriptorHeap>[] heaps)
	{
		var heapPtrs = stackalloc ID3D12DescriptorHeap*[heaps.Length];
		for (int i = 0; i < heaps.Length; i++)
		{
			heapPtrs[i] = heaps[i].Handle;
		}
		CommandList.SetDescriptorHeaps((uint)heaps.Length, heapPtrs);
	}

	/// <summary>
	/// Sets a compute root descriptor table.
	/// </summary>
	public void SetComputeRootDescriptorTable(uint rootParameterIndex, GpuDescriptorHandle baseDescriptor)
	{
		CommandList.SetComputeRootDescriptorTable(rootParameterIndex, baseDescriptor);
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
		if (barrier.Resource is ID3D12BackendTexture texture)
		{
			resource = texture.Resource;
		}
		else if (barrier.Resource is D3D12Buffer buffer)
		{
			resource = buffer.Resource.Handle;
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
	}
}

