#nullable enable

using System;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class SharedDrawIndirectCommandSet : IDisposable
{
	private readonly IGfxIndirectCommandBuffer?[] _commandBuffers =
		new IGfxIndirectCommandBuffer?[GpuDrawResources.IndirectCommandBufferSlotCount * GpuDrawExecutionLanes.ExecutionLaneCount];
	private readonly IGfxIndirectCommandBuffer[] _slotScratch = new IGfxIndirectCommandBuffer[GpuDrawExecutionLanes.ExecutionLaneCount];
	private readonly ulong[] _appliedStructuralVersions = new ulong[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly uint[] _bindlessEpochs = new uint[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly int[] _frameBindings = new int[GpuDrawResources.IndirectCommandBufferSlotCount];

	public SharedDrawIndirectCommandSet()
	{
		Array.Fill(_frameBindings, -1);
	}

	public void EnsureCreated(IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(device);

		for (var slotIndex = 0; slotIndex < GpuDrawResources.IndirectCommandBufferSlotCount; slotIndex++)
		{
			for (var executionIndex = 0; executionIndex < GpuDrawExecutionLanes.ExecutionLaneCount; executionIndex++)
			{
				var index = FlattenSlotLaneIndex(slotIndex, executionIndex);
				_commandBuffers[index] ??= device.CreateIndirectCommandBuffer(new IndirectCommandBufferDescriptor(
					PassKind.Graphics,
					(uint)GpuDrawResources.MaxDrawCount,
					supportsIndexedExecution: true));
			}
		}
	}

	public IGfxIndirectCommandBuffer GetCommandBuffer(int slotIndex, int executionLaneIndex)
	{
		ValidateSlot(slotIndex);
		ValidateLane(executionLaneIndex);
		return _commandBuffers[FlattenSlotLaneIndex(slotIndex, executionLaneIndex)]
		       ?? throw new InvalidOperationException("Shared draw indirect command set has not been created.");
	}

	public IGfxIndirectCommandBuffer GetCommandBuffer(int slotIndex, GpuDrawExecutionLaneDefinition lane) =>
		GetCommandBuffer(slotIndex, lane.ExecutionIndex);

	public ReadOnlySpan<IGfxIndirectCommandBuffer> GetSlotCommands(int slotIndex)
	{
		ValidateSlot(slotIndex);
		for (var executionIndex = 0; executionIndex < GpuDrawExecutionLanes.ExecutionLaneCount; executionIndex++)
		{
			_slotScratch[executionIndex] = GetCommandBuffer(slotIndex, executionIndex);
		}

		return _slotScratch;
	}

	public ulong GetAppliedStructuralVersion(int slotIndex)
	{
		ValidateSlot(slotIndex);
		return _appliedStructuralVersions[slotIndex];
	}

	public void SetAppliedStructuralVersion(int slotIndex, ulong version)
	{
		ValidateSlot(slotIndex);
		_appliedStructuralVersions[slotIndex] = version;
	}

	public bool RequiresFullReencode(int slotIndex, int frameSlot, uint bindlessEpoch)
	{
		ValidateSlot(slotIndex);
		return _frameBindings[slotIndex] != frameSlot || _bindlessEpochs[slotIndex] != bindlessEpoch;
	}

	public void MarkSlotEncoded(int slotIndex, int frameSlot, uint bindlessEpoch, ulong structuralVersion)
	{
		ValidateSlot(slotIndex);
		_frameBindings[slotIndex] = frameSlot;
		_bindlessEpochs[slotIndex] = bindlessEpoch;
		_appliedStructuralVersions[slotIndex] = structuralVersion;
	}

	public void Dispose()
	{
		for (var i = 0; i < _commandBuffers.Length; i++)
		{
			(_commandBuffers[i] as IDisposable)?.Dispose();
			_commandBuffers[i] = null;
		}
	}

	private static int FlattenSlotLaneIndex(int slotIndex, int executionLaneIndex) =>
		(slotIndex * GpuDrawExecutionLanes.ExecutionLaneCount) + executionLaneIndex;

	private static void ValidateSlot(int slotIndex)
	{
		if (slotIndex < 0 || slotIndex >= GpuDrawResources.IndirectCommandBufferSlotCount)
		{
			throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "Indirect command buffer slot is out of range.");
		}
	}

	private static void ValidateLane(int executionLaneIndex)
	{
		if (executionLaneIndex < 0 || executionLaneIndex >= GpuDrawExecutionLanes.ExecutionLaneCount)
		{
			throw new ArgumentOutOfRangeException(nameof(executionLaneIndex), executionLaneIndex, "Shared draw execution lane index is out of range.");
		}
	}
}

