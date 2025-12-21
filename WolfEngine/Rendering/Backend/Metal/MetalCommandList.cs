using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.QuartzCore;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("MacOS")]
internal sealed unsafe class MetalCommandList : IGfxCommandList
{
	private readonly MTLCommandQueue _queue;
	private readonly MetalDescriptorTable _descriptorTable;
	private readonly MTLCommandBuffer _commandBuffer;
	private MTLRenderCommandEncoder _renderEncoder;
	private MTLComputeCommandEncoder _computeEncoder;
	private CAMetalDrawable _presentDrawable;
	private PassTargets _currentTargets;
	private bool _hasTargets;
	private Viewport _viewport;
	private readonly MTLClearColor[] _clearColors = new MTLClearColor[8];
	private readonly bool[] _clearColorSet = new bool[8];
	private bool _clearDepthSet;
	private double _clearDepth = 1.0;
	private MTLPrimitiveType _primitiveType = MTLPrimitiveType.Triangle;
	private MTLBuffer _indexBuffer;
	private MTLIndexType _indexType;
	private nuint _indexOffset;

	public MetalCommandList(MTLCommandQueue queue, MetalDescriptorTable descriptorTable)
	{
		_queue = queue;
		_descriptorTable = descriptorTable;
		_commandBuffer = _queue.CommandBuffer();
	}

	public void BeginPass(in PassTargets targets, in Viewport viewport)
	{
		_currentTargets = targets;
		_viewport = new Viewport(viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth);
		_hasTargets = true;
		Array.Clear(_clearColorSet, 0, _clearColorSet.Length);
		_clearDepthSet = false;
	}

	public void EndPass()
	{
		if (_renderEncoder.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.EndEncoding();
			_renderEncoder = default;
		}

		_hasTargets = false;
	}

	public void BindPipeline(IGfxPipeline pipeline)
	{
		if (pipeline is not MetalPipeline metalPipeline)
		{
			throw new InvalidOperationException("Pipeline was not created by the Metal backend.");
		}

		_descriptorTable.SetArgumentEncoders(metalPipeline.TextureEncoder, metalPipeline.RWTextureEncoder, metalPipeline.SamplerEncoder);

		if (metalPipeline.Kind == PassKind.Compute)
		{
			EnsureComputeEncoder();
			ApplyBindlessToComputeEncoder();
			if (metalPipeline.ComputePipelineState.NativePtr == IntPtr.Zero)
			{
				throw new InvalidOperationException("Compute pipeline state was null.");
			}

			_computeEncoder.SetComputePipelineState(metalPipeline.ComputePipelineState);
			return;
		}

		EnsureRenderEncoder();
		ApplyBindlessToRenderEncoder();
		if (metalPipeline.RenderPipelineState.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Render pipeline state was null.");
		}

		_renderEncoder.SetRenderPipelineState(metalPipeline.RenderPipelineState);
		if (metalPipeline.DepthStencilState.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.SetDepthStencilState(metalPipeline.DepthStencilState);
		}

		_renderEncoder.SetCullMode(metalPipeline.RenderState.CullMode switch
		{
			CullMode.None => MTLCullMode.None,
			CullMode.Front => MTLCullMode.Front,
			CullMode.Back => MTLCullMode.Back,
			_ => MTLCullMode.None
		});
		_renderEncoder.SetFrontFacingWinding(MTLWinding.Clockwise);
	}

	public void SetPrimitiveTopology(PrimitiveTopology topology)
	{
		_primitiveType = topology switch
		{
			PrimitiveTopology.TriangleList => MTLPrimitiveType.Triangle,
			PrimitiveTopology.TriangleStrip => MTLPrimitiveType.TriangleStrip,
			PrimitiveTopology.LineList => MTLPrimitiveType.Line,
			PrimitiveTopology.LineStrip => MTLPrimitiveType.LineStrip,
			PrimitiveTopology.PointList => MTLPrimitiveType.Point,
			_ => MTLPrimitiveType.Triangle
		};
	}

	public void SetScissorRect(in RectInt rect)
	{
		EnsureRenderEncoder();
		var scissor = new MTLScissorRect
		{
			x = (nuint)Math.Max(rect.X, 0),
			y = (nuint)Math.Max(rect.Y, 0),
			width = (nuint)Math.Max(rect.Width, 0),
			height = (nuint)Math.Max(rect.Height, 0)
		};
		_renderEncoder.SetScissorRect(scissor);
	}

	public void ClearColorAttachment(uint index, Vector4 color)
	{
		if (index >= _clearColors.Length)
		{
			return;
		}

		_clearColors[index] = new MTLClearColor
		{
			red = color.X,
			green = color.Y,
			blue = color.Z,
			alpha = color.W
		};
		_clearColorSet[index] = true;
	}

	public void ClearDepthStencil(float depth)
	{
		_clearDepth = depth;
		_clearDepthSet = true;
	}

	public void BindGraphicsDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet)
	{
		throw new NotSupportedException("Descriptor sets are not supported in the Metal backend.");
	}

	public void BindComputeDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet)
	{
		throw new NotSupportedException("Descriptor sets are not supported in the Metal backend.");
	}

	public void SetBindlessTable(IGfxDescriptorTable table)
	{
		if (table is not MetalDescriptorTable)
		{
			throw new InvalidOperationException("Bindless table was not created by the Metal backend.");
		}
	}

	public void BindConstantBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0)
	{
		if (buffer is not MetalBuffer metalBuffer)
		{
			throw new InvalidOperationException("Buffer was not created by the Metal backend.");
		}

		EnsureRenderEncoder();
		_renderEncoder.SetVertexBuffer(metalBuffer.Buffer, (nuint)offset, slot);
		_renderEncoder.SetFragmentBuffer(metalBuffer.Buffer, (nuint)offset, slot);
	}

	public void SetGraphicsConstants(uint slot, ReadOnlySpan<byte> data)
	{
		if (data.IsEmpty)
		{
			return;
		}

		EnsureRenderEncoder();
		fixed (byte* dataPtr = data)
		{
			_renderEncoder.SetVertexBytes((IntPtr)dataPtr, (nuint)data.Length, slot);
			_renderEncoder.SetFragmentBytes((IntPtr)dataPtr, (nuint)data.Length, slot);
		}
	}

	public void SetComputeConstants(uint slot, ReadOnlySpan<byte> data)
	{
		if (data.IsEmpty)
		{
			return;
		}

		EnsureComputeEncoder();
		fixed (byte* dataPtr = data)
		{
			_computeEncoder.SetBytes((IntPtr)dataPtr, (nuint)data.Length, slot);
		}
	}

	public void PushConstants<T>(in T data) where T : unmanaged
	{
		Span<T> payload = stackalloc T[1];
		payload[0] = data;
		SetGraphicsConstants(0, MemoryMarshal.AsBytes(payload));
	}

	public void SetVertexBuffers(ReadOnlySpan<VertexBufferView> vertexBuffers)
	{
		EnsureRenderEncoder();
		for (var i = 0; i < vertexBuffers.Length; i++)
		{
			var view = vertexBuffers[i];
			if (view.Buffer is not MetalBuffer metalBuffer)
			{
				throw new InvalidOperationException("Vertex buffer was not created by the Metal backend.");
			}

			_renderEncoder!.SetVertexBuffer(metalBuffer.Buffer, (nuint)view.Offset, (uint)i);
		}
	}

	public void SetIndexBuffer(in IndexBufferView indexBuffer)
	{
		if (indexBuffer.Buffer is not MetalBuffer metalBuffer)
		{
			throw new InvalidOperationException("Index buffer was not created by the Metal backend.");
		}

		_indexBuffer = metalBuffer.Buffer;
		_indexOffset = (nuint)indexBuffer.Offset;
		_indexType = indexBuffer.Format == IndexFormat.UInt16 ? MTLIndexType.UInt16 : MTLIndexType.UInt32;
	}

	public void Draw(in DrawArguments arguments)
	{
		if (_indexBuffer.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Index buffer was not bound.");
		}

		EnsureRenderEncoder();
		_renderEncoder.DrawIndexedPrimitives(
			_primitiveType,
			arguments.IndexCount,
			_indexType,
			_indexBuffer,
			_indexOffset,
			arguments.InstanceCount);
	}

	public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
	{
		EnsureComputeEncoder();
		var threadgroups = new MTLSize
		{
			width = groupCountX,
			height = groupCountY,
			depth = groupCountZ
		};
		var threadsPerGroup = new MTLSize { width = 8, height = 8, depth = 1 };
		_computeEncoder.DispatchThreadgroups(threadgroups, threadsPerGroup);
	}

	public void Barrier(in ResourceBarrierDescription barrier)
	{
		// Metal handles resource hazards implicitly for most use cases.
	}

	public void Commit()
	{
		if (_renderEncoder.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.EndEncoding();
			_renderEncoder = default;
		}

		if (_computeEncoder.NativePtr != IntPtr.Zero)
		{
			_computeEncoder.EndEncoding();
			_computeEncoder = default;
		}

		if (_presentDrawable.NativePtr != IntPtr.Zero)
		{
			_commandBuffer.PresentDrawable(_presentDrawable);
			_presentDrawable = default;
		}

		_commandBuffer.Commit();
	}

	private void EnsureRenderEncoder()
	{
		if (_renderEncoder.NativePtr != IntPtr.Zero)
		{
			return;
		}

		if (_hasTargets == false)
		{
			throw new InvalidOperationException("BeginPass must be called before encoding draw commands.");
		}

		using var descriptor = new MTLRenderPassDescriptor();
		for (var i = 0; i < _currentTargets.ColorAttachments.Count; i++)
		{
			var binding = _currentTargets.ColorAttachments[i];
			if (binding.Texture is MetalBackbufferTexture backbuffer)
			{
				_presentDrawable = backbuffer.Drawable;
				var backAttachment = descriptor.ColorAttachments.Object((nuint)i);
				backAttachment.Texture = backbuffer.Drawable.Texture;
				backAttachment.LoadAction = _clearColorSet[i] ? MTLLoadAction.Clear : MTLLoadAction.Load;
				backAttachment.StoreAction = MTLStoreAction.Store;
				if (_clearColorSet[i])
				{
					backAttachment.ClearColor = _clearColors[i];
				}
				descriptor.ColorAttachments.SetObject(backAttachment, (nuint)i);
				continue;
			}

			if (binding.Texture is not MetalTexture metalTexture)
			{
				throw new InvalidOperationException("Color attachment was not created by the Metal backend.");
			}

			var colorAttachment = descriptor.ColorAttachments.Object((nuint)i);
			colorAttachment.Texture = metalTexture.Texture;
			colorAttachment.LoadAction = _clearColorSet[i] ? MTLLoadAction.Clear : MTLLoadAction.Load;
			colorAttachment.StoreAction = MTLStoreAction.Store;
			if (_clearColorSet[i])
			{
				colorAttachment.ClearColor = _clearColors[i];
			}
			descriptor.ColorAttachments.SetObject(colorAttachment, (nuint)i);
		}

		if (_currentTargets.DepthAttachment is { } depthBinding)
		{
			if (depthBinding.Texture is not MetalTexture depthTexture)
			{
				throw new InvalidOperationException("Depth attachment was not created by the Metal backend.");
			}

			var depthAttachment = descriptor.DepthAttachment;
			depthAttachment.Texture = depthTexture.Texture;
			depthAttachment.LoadAction = _clearDepthSet ? MTLLoadAction.Clear : MTLLoadAction.Load;
			depthAttachment.StoreAction = MTLStoreAction.Store;
			if (_clearDepthSet)
			{
				depthAttachment.ClearDepth = _clearDepth;
			}
		}

		_renderEncoder = _commandBuffer.RenderCommandEncoder(descriptor);
		var viewport = new MTLViewport
		{
			originX = _viewport.X,
			originY = _viewport.Y,
			width = _viewport.Width,
			height = _viewport.Height,
			znear = _viewport.MinDepth,
			zfar = _viewport.MaxDepth
		};
		_renderEncoder.SetViewport(viewport);

		ApplyBindlessToRenderEncoder();
	}

	private void EnsureComputeEncoder()
	{
		if (_computeEncoder.NativePtr != IntPtr.Zero)
		{
			return;
		}

		_computeEncoder = _commandBuffer.ComputeCommandEncoder();
		ApplyBindlessToComputeEncoder();
	}

	private void ApplyBindlessToRenderEncoder()
	{
		if (_descriptorTable.TextureArgumentBuffer.NativePtr == IntPtr.Zero &&
			_descriptorTable.RWTextureArgumentBuffer.NativePtr == IntPtr.Zero &&
			_descriptorTable.SamplerArgumentBuffer.NativePtr == IntPtr.Zero)
		{
			return;
		}

		if (_descriptorTable.TextureArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.SetVertexBuffer(_descriptorTable.TextureArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
			_renderEncoder.SetFragmentBuffer(_descriptorTable.TextureArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
		}

		if (_descriptorTable.RWTextureArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.SetVertexBuffer(_descriptorTable.RWTextureArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexRWTextures);
			_renderEncoder.SetFragmentBuffer(_descriptorTable.RWTextureArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexRWTextures);
		}

		if (_descriptorTable.SamplerArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.SetVertexBuffer(_descriptorTable.SamplerArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);
			_renderEncoder.SetFragmentBuffer(_descriptorTable.SamplerArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);
		}
	}

	private void ApplyBindlessToComputeEncoder()
	{
		if (_descriptorTable.TextureArgumentBuffer.NativePtr == IntPtr.Zero &&
			_descriptorTable.RWTextureArgumentBuffer.NativePtr == IntPtr.Zero &&
			_descriptorTable.SamplerArgumentBuffer.NativePtr == IntPtr.Zero)
		{
			return;
		}

		if (_descriptorTable.TextureArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_computeEncoder.SetBuffer(_descriptorTable.TextureArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
		}

		if (_descriptorTable.RWTextureArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_computeEncoder.SetBuffer(_descriptorTable.RWTextureArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexRWTextures);
		}

		if (_descriptorTable.SamplerArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_computeEncoder.SetBuffer(_descriptorTable.SamplerArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);
		}
	}
}
