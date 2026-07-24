using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Profiling;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("MacOS")]
internal sealed unsafe class MetalCommandList : IGfxCommandList, IDisposable
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
	private MetalPipeline _currentGraphicsPipeline;
	private MetalPipeline _currentComputePipeline;
	private bool _bindlessBuffersSetRender;
	private bool _bindlessBuffersSetCompute;
	private uint _lastBindlessVersionRender = uint.MaxValue;
	private uint _lastBindlessVersionCompute = uint.MaxValue;
	private readonly List<MTLBuffer> _indirectReferencedBuffers = new();
	private readonly HashSet<nint> _indirectReferencedPointers = new();
	private bool _disposed;
	private bool _committed;
	private MetalGpuProfilerBackend? _gpuProfiler;
	private GpuProfilePassCapture? _gpuPassCapture;
	private readonly List<MetalGpuProfilerBackend.TimestampBlock> _gpuTimestampBlocks = new();
	private readonly List<GpuTimestampScope> _gpuTimestampScopes = new();
	private bool _gpuProfilingFailed;
	private ulong _gpuFrameIndex;

	public MetalCommandList(MTLCommandQueue queue, MetalDescriptorTable descriptorTable)
	{
		_queue = queue;
		_descriptorTable = descriptorTable;
		_commandBuffer = _queue.CommandBuffer();
	}

	public GraphicsBackendKind BackendKind => GraphicsBackendKind.Metal;

	public void BeginPass(in PassTargets targets, in Viewport viewport)
	{
		_currentTargets = targets;
		_viewport = new Viewport(viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth);
		_hasTargets = true;
		Array.Clear(_clearColorSet, 0, _clearColorSet.Length);
		_clearDepthSet = false;
		_currentGraphicsPipeline = null;
		_bindlessBuffersSetRender = false;
		_lastBindlessVersionRender = uint.MaxValue;
	}

	public void EndPass()
	{
		ThrowIfDisposed();
		if (_renderEncoder.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.EndEncoding();
			_renderEncoder.Dispose();
			_renderEncoder = default;
		}

		_currentGraphicsPipeline = null;
		_bindlessBuffersSetRender = false;
		_lastBindlessVersionRender = uint.MaxValue;
		_hasTargets = false;
	}

	public void BindPipeline(IGfxPipeline pipeline)
	{
		ThrowIfDisposed();
		if (pipeline is not MetalPipeline metalPipeline)
		{
			throw new InvalidOperationException("Pipeline was not created by the Metal backend.");
		}

		if (metalPipeline.Kind == PassKind.Compute)
		{
			EnsureComputeEncoder();
			if (ReferenceEquals(_currentComputePipeline, metalPipeline) == false)
			{
				_descriptorTable.SetArgumentEncoders(metalPipeline.TextureEncoder, metalPipeline.RWTextureEncoder, metalPipeline.SamplerEncoder);
				_bindlessBuffersSetCompute = false;
				_currentComputePipeline = metalPipeline;
				if (metalPipeline.ComputePipelineState.NativePtr == IntPtr.Zero)
				{
					throw new InvalidOperationException("Compute pipeline state was null.");
				}

				_computeEncoder.SetComputePipelineState(metalPipeline.ComputePipelineState);
			}

			ApplyBindlessToComputeEncoder();
			return;
		}

		EnsureRenderEncoder();
		if (ReferenceEquals(_currentGraphicsPipeline, metalPipeline) == false)
		{
			_descriptorTable.SetArgumentEncoders(metalPipeline.TextureEncoder, metalPipeline.RWTextureEncoder, metalPipeline.SamplerEncoder);
			_bindlessBuffersSetRender = false;
			_currentGraphicsPipeline = metalPipeline;
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
			var winding = metalPipeline.Key.Layout == GraphicsLayoutKind.Skybox
				? MTLWinding.CounterClockwise
				: MTLWinding.Clockwise;
			_renderEncoder.SetFrontFacingWinding(winding);
		}

		ApplyBindlessToRenderEncoder();
	}

	internal void AttachGpuProfiler(MetalGpuProfilerBackend profiler, GpuProfilePassCapture passCapture)
	{
		_gpuProfiler = profiler;
		_gpuPassCapture = passCapture;
		_gpuFrameIndex = passCapture.FrameIndex;
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
				var scopes = new List<GpuProfileScope>(_gpuTimestampScopes.Count);
				for (var i = 0; i < _gpuTimestampScopes.Count; i++)
				{
					var scope = _gpuTimestampScopes[i];
					var values = scope.Block.ReadPair(scope.StartIndex, scope.EndIndex);
					scopes.Add(new GpuProfileScope(
						scope.Name,
						MetalGpuProfilerBackend.TicksToMilliseconds(values.Start, values.End)));
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
			_gpuProfiler.ReleaseBlocks(_gpuTimestampBlocks);
			_gpuTimestampScopes.Clear();
			_gpuPassCapture = null;
			_gpuProfiler = null;
		}
	}

	private void ConfigureRenderStageProfiling(MTLRenderPassDescriptor descriptor)
	{
		if (_gpuProfiler is null || _gpuProfilingFailed)
		{
			return;
		}

		try
		{
			var reservation = ReserveGpuTimestampSamples(4);
			var block = reservation.Block;
			var vertexStart = reservation.StartIndex;
			var vertexEnd = vertexStart + 1;
			var fragmentStart = vertexStart + 2;
			var fragmentEnd = vertexStart + 3;
			var attachment = descriptor.SampleBufferAttachments.Object(0);
			attachment.SampleBuffer = block.SampleBuffer;
			attachment.StartOfVertexSampleIndex = vertexStart;
			attachment.EndOfVertexSampleIndex = vertexEnd;
			attachment.StartOfFragmentSampleIndex = fragmentStart;
			attachment.EndOfFragmentSampleIndex = fragmentEnd;
			_gpuTimestampScopes.Add(new GpuTimestampScope("Vertex stage", block, vertexStart, vertexEnd));
			_gpuTimestampScopes.Add(new GpuTimestampScope("Fragment stage", block, fragmentStart, fragmentEnd));
		}
		catch (Exception exception)
		{
			_gpuProfiler.ReportFailure(exception);
			_gpuProfilingFailed = true;
		}
	}

	private void ConfigureComputeStageProfiling(MTLComputePassDescriptor descriptor)
	{
		if (_gpuProfiler is null || _gpuProfilingFailed)
		{
			return;
		}

		try
		{
			var reservation = ReserveGpuTimestampSamples(2);
			var block = reservation.Block;
			var start = reservation.StartIndex;
			var end = start + 1;
			var attachment = descriptor.SampleBufferAttachments.Object(0);
			attachment.SampleBuffer = block.SampleBuffer;
			attachment.StartOfEncoderSampleIndex = start;
			attachment.EndOfEncoderSampleIndex = end;
			_gpuTimestampScopes.Add(new GpuTimestampScope("Compute stage", block, start, end));
		}
		catch (Exception exception)
		{
			_gpuProfiler.ReportFailure(exception);
			_gpuProfilingFailed = true;
		}
	}

	private MetalGpuProfilerBackend.SampleReservation ReserveGpuTimestampSamples(ulong requiredSamples)
	{
		var reservation = _gpuProfiler!.ReserveSamples(_gpuFrameIndex, requiredSamples);
		if (!_gpuTimestampBlocks.Contains(reservation.Block))
		{
			_gpuProfiler.RetainBlock(reservation.Block);
			_gpuTimestampBlocks.Add(reservation.Block);
		}
		return reservation;
	}

	private readonly record struct GpuTimestampScope(
		string Name,
		MetalGpuProfilerBackend.TimestampBlock Block,
		ulong StartIndex,
		ulong EndIndex);

	public void SetPrimitiveTopology(PrimitiveTopology topology)
	{
		ThrowIfDisposed();
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
		ThrowIfDisposed();
		EnsureRenderEncoder();
		var maxWidth = Math.Max(0, (int)_viewport.Width);
		var maxHeight = Math.Max(0, (int)_viewport.Height);
		var x = Math.Clamp(rect.X, 0, maxWidth);
		var y = Math.Clamp(rect.Y, 0, maxHeight);
		var width = Math.Max(0, Math.Min(rect.Width, maxWidth - x));
		var height = Math.Max(0, Math.Min(rect.Height, maxHeight - y));
		var scissor = new MTLScissorRect
		{
			x = (nuint)x,
			y = (nuint)y,
			width = (nuint)width,
			height = (nuint)height
		};
		_renderEncoder.SetScissorRect(scissor);
	}

	public void ClearColorAttachment(uint index, ColorRGBA color)
	{
		ThrowIfDisposed();
		if (index >= _clearColors.Length)
		{
			return;
		}

		_clearColors[index] = new MTLClearColor
		{
			red = color.R,
			green = color.G,
			blue = color.B,
			alpha = color.A
		};
		_clearColorSet[index] = true;
	}

	public void ClearDepthStencil(float depth)
	{
		ThrowIfDisposed();
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
		ThrowIfDisposed();
		if (table is not MetalDescriptorTable)
		{
			throw new InvalidOperationException("Bindless table was not created by the Metal backend.");
		}
	}

	public void BindConstantBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0)
	{
		ThrowIfDisposed();
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
		ThrowIfDisposed();
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
		ThrowIfDisposed();
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

	public void SetComputeBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0)
	{
		ThrowIfDisposed();
		if (buffer is not MetalBuffer metalBuffer)
		{
			throw new InvalidOperationException("Buffer was not created by the Metal backend.");
		}

		EnsureComputeEncoder();
		_computeEncoder.SetBuffer(metalBuffer.Buffer, (nuint)offset, slot);
	}

	public void SetComputeReadOnlyBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0)
	{
		SetComputeBuffer(slot, buffer, offset);
	}

	public void PushConstants<T>(in T data) where T : unmanaged
	{
		Span<T> payload = stackalloc T[1];
		payload[0] = data;
		SetGraphicsConstants(0, MemoryMarshal.AsBytes(payload));
	}

	public void SetVertexBuffer(in VertexBufferView vertexBuffer)
	{
		ThrowIfDisposed();
		EnsureRenderEncoder();
		if (vertexBuffer.Buffer is not MetalBuffer metalBuffer)
		{
			throw new InvalidOperationException("Vertex buffer was not created by the Metal backend.");
		}

		_renderEncoder!.SetVertexBuffer(metalBuffer.Buffer, (nuint)vertexBuffer.Offset, 0);
	}

	public void SetVertexBuffers(ReadOnlySpan<VertexBufferView> vertexBuffers)
	{
		ThrowIfDisposed();
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
		ThrowIfDisposed();
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
		ThrowIfDisposed();
		if (_indexBuffer.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Index buffer was not bound.");
		}

		EnsureRenderEncoder();
		var indexSize = _indexType == MTLIndexType.UInt16 ? 2u : 4u;
		var startOffset = (nuint)(arguments.StartIndex * indexSize);
		_renderEncoder.DrawIndexedPrimitives(
			_primitiveType,
			arguments.IndexCount,
			_indexType,
			_indexBuffer,
			_indexOffset + startOffset,
			arguments.InstanceCount,
			arguments.BaseVertex,
			arguments.StartInstance);
	}

	public void DrawIndexedIndirect(in IndexBufferView indexBuffer, IGfxBuffer indirectArgsBuffer, ulong indirectArgsOffset)
	{
		ThrowIfDisposed();
		if (indexBuffer.Buffer is not MetalBuffer metalIndexBuffer)
		{
			throw new InvalidOperationException("Index buffer was not created by the Metal backend.");
		}

		if (indirectArgsBuffer is not MetalBuffer metalArgsBuffer)
		{
			throw new InvalidOperationException("Indirect args buffer was not created by the Metal backend.");
		}

		EnsureRenderEncoder();
		var indexType = indexBuffer.Format == IndexFormat.UInt16 ? MTLIndexType.UInt16 : MTLIndexType.UInt32;
		_renderEncoder.DrawIndexedPrimitives(
			_primitiveType,
			indexType,
			metalIndexBuffer.Buffer,
			(nuint)indexBuffer.Offset,
			metalArgsBuffer.Buffer,
			(nuint)indirectArgsOffset);
	}

	public void ExecuteIndirectCommandBuffer(IGfxIndirectCommandBuffer commandBuffer, uint maxCommandCount)
	{
		ThrowIfDisposed();
		if (commandBuffer is not MetalIndirectCommandBuffer metalCommandBuffer)
		{
			throw new InvalidOperationException("Indirect command buffer was not created by the Metal backend.");
		}

		var maxAvailable = Math.Min(maxCommandCount, metalCommandBuffer.Descriptor.MaxCommandCount);
		if (maxAvailable == 0)
		{
			return;
		}
		if (metalCommandBuffer.UsesCurrentBindlessBuffers(_descriptorTable, maxAvailable) == false)
		{
			// An argument buffer grew after this ICB slot was encoded. The backend bridge
			// observes the pointer change at the next frame boundary and re-encodes the
			// active slot. Skipping one stale execution is preferable to submitting an ICB
			// that contains a dangling Metal buffer binding.
			return;
		}

		EnsureRenderEncoder();
		if (maxAvailable >= metalCommandBuffer.Descriptor.MaxCommandCount)
		{
			var referenced = metalCommandBuffer.GetReferencedBuffers();
			for (var i = 0; i < referenced.Count; i++)
			{
				_renderEncoder.UseResource(referenced[i], MTLResourceUsage.Read);
			}
		}
		else
		{
			metalCommandBuffer.CollectReferencedBuffers(maxAvailable, _indirectReferencedBuffers,
				_indirectReferencedPointers);
			for (var i = 0; i < _indirectReferencedBuffers.Count; i++)
			{
				_renderEncoder.UseResource(_indirectReferencedBuffers[i], MTLResourceUsage.Read);
			}
		}
		_renderEncoder.ExecuteCommandsInBuffer(
			metalCommandBuffer.Buffer,
			new NSRange { location = 0, length = maxAvailable });
	}

	public void ExecuteIndirectCommandBufferRange(
		IGfxIndirectCommandBuffer commandBuffer,
		IGfxBuffer commandRangeBuffer,
		ulong commandRangeOffsetBytes)
	{
		ThrowIfDisposed();
		if (commandBuffer is not MetalIndirectCommandBuffer metalCommandBuffer)
		{
			throw new InvalidOperationException("Indirect command buffer was not created by the Metal backend.");
		}

		if (commandRangeBuffer is not MetalBuffer metalRangeBuffer)
		{
			throw new InvalidOperationException("Command count/range buffer was not created by the Metal backend.");
		}
		if (metalRangeBuffer.IsDisposed)
		{
			return;
		}
		if (metalCommandBuffer.UsesCurrentBindlessBuffers(
			    _descriptorTable,
			    metalCommandBuffer.Descriptor.MaxCommandCount) == false)
		{
			return;
		}

		EnsureRenderEncoder();
		var referenced = metalCommandBuffer.GetReferencedBuffers();
		for (var i = 0; i < referenced.Count; i++)
		{
			_renderEncoder.UseResource(referenced[i], MTLResourceUsage.Read);
		}

		// The caller provides a two-uint execution range buffer: { start, length }.
		_renderEncoder.UseResource(metalRangeBuffer.Buffer, MTLResourceUsage.Read);
		_renderEncoder.ExecuteCommandsInBuffer(
			metalCommandBuffer.Buffer,
			metalRangeBuffer.Buffer,
			(nuint)commandRangeOffsetBytes);
	}

	public void BuildBottomLevelAccelerationStructure(IGfxBottomLevelAccelerationStructure accelerationStructure)
	{
		ThrowIfDisposed();
		if (accelerationStructure is not MetalBottomLevelAccelerationStructure blas)
		{
			throw new InvalidOperationException("Bottom-level acceleration structure was not created by the Metal backend.");
		}

		EndActiveEncoders();
		var encoder = _commandBuffer.AccelerationStructureCommandEncoder();
		encoder.BuildAccelerationStructure(
			blas.AccelerationStructure,
			blas.MetalDescriptor,
			blas.ScratchBuffer,
			0);
		encoder.UpdateFence(blas.BuildFence);
		encoder.EndEncoding();
		encoder.Dispose();
	}

	public unsafe void BuildTopLevelAccelerationStructure(
		IGfxTopLevelAccelerationStructure accelerationStructure,
		ReadOnlySpan<RayTracingInstanceDescription> instances)
	{
		ThrowIfDisposed();
		if (accelerationStructure is not MetalTopLevelAccelerationStructure tlas)
		{
			throw new InvalidOperationException("Top-level acceleration structure was not created by the Metal backend.");
		}

		var instanceCount = Math.Min((uint)instances.Length, tlas.Descriptor.MaxInstanceCount);
		var descriptorSpan = new Span<MetalAccelerationStructureInstanceDescriptorData>(
			tlas.InstanceDescriptorBuffer.Contents.ToPointer(),
			(int)tlas.Descriptor.MaxInstanceCount);
		descriptorSpan.Clear();

		var nativeAccelerationStructures = new IntPtr[instanceCount];
		tlas.ReferencedBottomLevelAccelerationStructures.Clear();
		for (var i = 0; i < instanceCount; i++)
		{
			var instance = instances[i];
			if (instance.AccelerationStructure is not MetalBottomLevelAccelerationStructure blas)
			{
				throw new InvalidOperationException("TLAS instance references a BLAS not created by the Metal backend.");
			}

			nativeAccelerationStructures[i] = (IntPtr)blas.AccelerationStructure;
			tlas.ReferencedBottomLevelAccelerationStructures.Add(blas.AccelerationStructure);
			descriptorSpan[i] = CreateInstanceDescriptor(instance, (uint)i);
		}

		tlas.MetalDescriptor.InstanceCount = instanceCount;
		if (tlas.InstancedAccelerationStructures.NativePtr != IntPtr.Zero)
		{
			tlas.InstancedAccelerationStructures.Dispose();
		}
		tlas.InstancedAccelerationStructures = CreateNativeArray(nativeAccelerationStructures);
		tlas.MetalDescriptor.InstancedAccelerationStructures = tlas.InstancedAccelerationStructures;

		EndActiveEncoders();
		var encoder = _commandBuffer.AccelerationStructureCommandEncoder();
		for (var i = 0; i < instances.Length; i++)
		{
			if (instances[i].AccelerationStructure is MetalBottomLevelAccelerationStructure blas)
			{
				encoder.WaitForFence(blas.BuildFence);
			}
		}
		encoder.BuildAccelerationStructure(
			tlas.AccelerationStructure,
			tlas.MetalDescriptor,
			tlas.ScratchBuffer,
			0);
		encoder.UpdateFence(tlas.BuildFence);
		encoder.EndEncoding();
		encoder.Dispose();
	}

	public void SynchronizeAccelerationStructureBuildForComputeRead(IGfxTopLevelAccelerationStructure accelerationStructure)
	{
		ThrowIfDisposed();
		if (accelerationStructure is not MetalTopLevelAccelerationStructure tlas)
		{
			throw new InvalidOperationException("Top-level acceleration structure was not created by the Metal backend.");
		}

		EnsureComputeEncoder();
		_computeEncoder.WaitForFence(tlas.BuildFence);
		_computeEncoder.MemoryBarrier(MTLBarrierScope.Buffers);
	}

	public void SetComputeAccelerationStructure(uint slot, IGfxTopLevelAccelerationStructure accelerationStructure)
	{
		ThrowIfDisposed();
		if (accelerationStructure is not MetalTopLevelAccelerationStructure tlas)
		{
			throw new InvalidOperationException("Top-level acceleration structure was not created by the Metal backend.");
		}

		EnsureComputeEncoder();
		_computeEncoder.SetAccelerationStructure(tlas.AccelerationStructure, slot);
		_computeEncoder.UseResource(tlas.AccelerationStructure, MTLResourceUsage.Read);
		for (var i = 0; i < tlas.ReferencedBottomLevelAccelerationStructures.Count; i++)
		{
			_computeEncoder.UseResource(tlas.ReferencedBottomLevelAccelerationStructures[i], MTLResourceUsage.Read);
		}
	}

	public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
	{
		ThrowIfDisposed();
		EnsureComputeEncoder();
		var reflectedThreadGroupSize = _currentComputePipeline?.ComputeThreadGroupSize
			?? throw new InvalidOperationException("No compute pipeline with reflected threadgroup size is currently bound.");
		var threadgroups = new MTLSize
		{
			width = groupCountX,
			height = groupCountY,
			depth = groupCountZ
		};
		var threadsPerGroup = new MTLSize
		{
			width = reflectedThreadGroupSize.X,
			height = reflectedThreadGroupSize.Y,
			depth = reflectedThreadGroupSize.Z
		};
		_computeEncoder.DispatchThreadgroups(threadgroups, threadsPerGroup);
	}

	private static MetalAccelerationStructureInstanceDescriptorData CreateInstanceDescriptor(
		in RayTracingInstanceDescription instance,
		uint accelerationStructureIndex)
	{
		var transform = instance.Transform;
		return new MetalAccelerationStructureInstanceDescriptorData
		{
			Column0X = transform.M11,
			Column0Y = transform.M12,
			Column0Z = transform.M13,
			Column1X = transform.M21,
			Column1Y = transform.M22,
			Column1Z = transform.M23,
			Column2X = transform.M31,
			Column2Y = transform.M32,
			Column2Z = transform.M33,
			Column3X = transform.M41,
			Column3Y = transform.M42,
			Column3Z = transform.M43,
			Options = (uint)(instance.Active
				? MTLAccelerationStructureInstanceOptions.Opaque
				: MTLAccelerationStructureInstanceOptions.NonOpaque),
			Mask = instance.Active ? instance.Mask : 0u,
			IntersectionFunctionTableOffset = 0,
			AccelerationStructureIndex = accelerationStructureIndex
		};
	}

	private static NSArray CreateNativeArray(params IntPtr[] objects)
	{
		var arrayClass = new ObjectiveCClass("NSMutableArray");
		var array = ObjectiveC.IntPtr_objc_msgSend(arrayClass.Alloc(), new Selector("init"));
		for (var i = 0; i < objects.Length; i++)
		{
			if (objects[i] != IntPtr.Zero)
			{
				ObjectiveC.objc_msgSend(array, new Selector("addObject:"), objects[i]);
			}
		}

		return new NSArray(array);
	}

	public void CopyBuffer(IGfxBuffer source, ulong sourceOffset, IGfxBuffer destination, ulong destinationOffset, ulong sizeInBytes)
	{
		ThrowIfDisposed();
		if (sizeInBytes == 0)
		{
			return;
		}

		if (source is not MetalBuffer sourceBuffer)
		{
			throw new InvalidOperationException("Source buffer was not created by the Metal backend.");
		}

		if (destination is not MetalBuffer destinationBuffer)
		{
			throw new InvalidOperationException("Destination buffer was not created by the Metal backend.");
		}

		EndActiveEncoders();

		var blit = _commandBuffer.BlitCommandEncoder();
		blit.CopyFromBuffer(
			sourceBuffer.Buffer,
			(nuint)sourceOffset,
			destinationBuffer.Buffer,
			(nuint)destinationOffset,
			(nuint)sizeInBytes);
		blit.EndEncoding();
		if (blit.NativePtr != IntPtr.Zero)
		{
			blit.Dispose();
		}
	}

	public void Barrier(in ResourceBarrierDescription barrier)
	{
		ThrowIfDisposed();
		// Metal handles resource hazards implicitly for most use cases.
	}

	public void CopyTexture(MTLTexture source, MTLTexture destination, uint width, uint height)
	{
		ThrowIfDisposed();
		if (source.NativePtr == IntPtr.Zero || destination.NativePtr == IntPtr.Zero)
		{
			return;
		}

		EndActiveEncoders();

		var blit = _commandBuffer.BlitCommandEncoder();
		var origin = new MTLOrigin { x = 0, y = 0, z = 0 };
		var size = new MTLSize { width = width, height = height, depth = 1 };
		blit.CopyFromTexture(source, 0, 0, origin, size, destination, 0, 0, origin);
		blit.EndEncoding();
		if (blit.NativePtr != IntPtr.Zero)
		{
			blit.Dispose();
		}
	}

	public void Commit()
	{
		ThrowIfDisposed();
		if (_committed)
		{
			return;
		}
		EndActiveEncoders();

		if (_presentDrawable.NativePtr != IntPtr.Zero)
		{
			_commandBuffer.PresentDrawable(_presentDrawable);
			_presentDrawable = default;
		}

		_commandBuffer.Commit();
		_committed = true;
	}

	internal void WaitUntilCompleted()
	{
		ThrowIfDisposed();
		if (_committed == false)
		{
			return;
		}

		_commandBuffer.WaitUntilCompleted();
	}

	public void SetPresentDrawable(CAMetalDrawable drawable)
	{
		ThrowIfDisposed();
		if (drawable.NativePtr == IntPtr.Zero)
		{
			return;
		}

		_presentDrawable = drawable;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		if (_renderEncoder.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.EndEncoding();
			_renderEncoder.Dispose();
			_renderEncoder = default;
		}

		if (_computeEncoder.NativePtr != IntPtr.Zero)
		{
			_computeEncoder.EndEncoding();
			_computeEncoder.Dispose();
			_computeEncoder = default;
		}

		_presentDrawable = default;

		if (_commandBuffer.NativePtr != IntPtr.Zero)
		{
			_commandBuffer.Dispose();
		}

		_disposed = true;
	}

	private void EnsureRenderEncoder()
	{
		ThrowIfDisposed();
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
				SetPresentDrawable(backbuffer.Drawable);
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

		ConfigureRenderStageProfiling(descriptor);
		_renderEncoder = _commandBuffer.RenderCommandEncoder(descriptor);
		_currentGraphicsPipeline = null;
		_bindlessBuffersSetRender = false;
		_lastBindlessVersionRender = uint.MaxValue;
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
		ThrowIfDisposed();
		if (_computeEncoder.NativePtr != IntPtr.Zero)
		{
			return;
		}

		using var descriptor = new MTLComputePassDescriptor();
		ConfigureComputeStageProfiling(descriptor);
		_computeEncoder = _commandBuffer.ComputeCommandEncoder(descriptor);
		_currentComputePipeline = null;
		_bindlessBuffersSetCompute = false;
		_lastBindlessVersionCompute = uint.MaxValue;
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

		if (_bindlessBuffersSetRender == false && _descriptorTable.TextureArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.SetVertexBuffer(_descriptorTable.TextureArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
			_renderEncoder.SetFragmentBuffer(_descriptorTable.TextureArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
		}

		if (_bindlessBuffersSetRender == false && _descriptorTable.RWTextureArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.SetFragmentBuffer(_descriptorTable.RWTextureArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexRWTextures);
		}

		if (_bindlessBuffersSetRender == false && _descriptorTable.SamplerArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.SetVertexBuffer(_descriptorTable.SamplerArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);
			_renderEncoder.SetFragmentBuffer(_descriptorTable.SamplerArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);
		}

		if (_bindlessBuffersSetRender == false && _descriptorTable.CountBuffer.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.SetVertexBuffer(_descriptorTable.CountBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexCounts);
			_renderEncoder.SetFragmentBuffer(_descriptorTable.CountBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexCounts);
		}

		_bindlessBuffersSetRender = true;

		var bindlessVersion = _descriptorTable.BindlessVersion;
		if (_lastBindlessVersionRender == bindlessVersion)
		{
			return;
		}

		_lastBindlessVersionRender = bindlessVersion;
		UseBindlessResourcesForRender();
	}

	private void ApplyBindlessToComputeEncoder()
	{
		if (_descriptorTable.TextureArgumentBuffer.NativePtr == IntPtr.Zero &&
			_descriptorTable.RWTextureArgumentBuffer.NativePtr == IntPtr.Zero &&
			_descriptorTable.SamplerArgumentBuffer.NativePtr == IntPtr.Zero)
		{
			return;
		}

		if (_bindlessBuffersSetCompute == false && _descriptorTable.TextureArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_computeEncoder.SetBuffer(_descriptorTable.TextureArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexTextures);
		}

		if (_bindlessBuffersSetCompute == false && _descriptorTable.RWTextureArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_computeEncoder.SetBuffer(_descriptorTable.RWTextureArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexRWTextures);
		}

		if (_bindlessBuffersSetCompute == false && _descriptorTable.SamplerArgumentBuffer.NativePtr != IntPtr.Zero)
		{
			_computeEncoder.SetBuffer(_descriptorTable.SamplerArgumentBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexSamplers);
		}

		if (_bindlessBuffersSetCompute == false && _descriptorTable.CountBuffer.NativePtr != IntPtr.Zero)
		{
			_computeEncoder.SetBuffer(_descriptorTable.CountBuffer, 0, MetalDescriptorTable.BindlessArgumentBufferIndexCounts);
		}

		_bindlessBuffersSetCompute = true;

		var bindlessVersion = _descriptorTable.BindlessVersion;
		if (_lastBindlessVersionCompute == bindlessVersion)
		{
			return;
		}

		_lastBindlessVersionCompute = bindlessVersion;
		UseBindlessResourcesForCompute();
	}

	private void UseBindlessResourcesForRender()
	{
		var srvCount = _descriptorTable.SrvCount;
		for (var i = 0; i < srvCount; i++)
		{
			var texture = _descriptorTable.GetSrvTexture(i);
			if (texture.NativePtr != IntPtr.Zero)
			{
				_renderEncoder.UseResource(texture, MTLResourceUsage.Read);
			}
		}

		var uavCount = _descriptorTable.UavCount;
		for (var i = 0; i < uavCount; i++)
		{
			var texture = _descriptorTable.GetUavTexture(i);
			if (texture.NativePtr != IntPtr.Zero)
			{
				_renderEncoder.UseResource(texture, MTLResourceUsage.Read | MTLResourceUsage.Write);
			}
		}
	}

	private void UseBindlessResourcesForCompute()
	{
		var srvCount = _descriptorTable.SrvCount;
		for (var i = 0; i < srvCount; i++)
		{
			var texture = _descriptorTable.GetSrvTexture(i);
			if (texture.NativePtr != IntPtr.Zero)
			{
				_computeEncoder.UseResource(texture, MTLResourceUsage.Read);
			}
		}

		var uavCount = _descriptorTable.UavCount;
		for (var i = 0; i < uavCount; i++)
		{
			var texture = _descriptorTable.GetUavTexture(i);
			if (texture.NativePtr != IntPtr.Zero)
			{
				_computeEncoder.UseResource(texture, MTLResourceUsage.Read | MTLResourceUsage.Write);
			}
		}
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(MetalCommandList));
		}
	}

	private void EndActiveEncoders()
	{
		if (_renderEncoder.NativePtr != IntPtr.Zero)
		{
			_renderEncoder.EndEncoding();
			_renderEncoder.Dispose();
			_renderEncoder = default;
			_currentGraphicsPipeline = null;
			_bindlessBuffersSetRender = false;
			_lastBindlessVersionRender = uint.MaxValue;
		}

		if (_computeEncoder.NativePtr != IntPtr.Zero)
		{
			_computeEncoder.EndEncoding();
			_computeEncoder.Dispose();
			_computeEncoder = default;
			_currentComputePipeline = null;
			_bindlessBuffersSetCompute = false;
			_lastBindlessVersionCompute = uint.MaxValue;
		}
	}
}
