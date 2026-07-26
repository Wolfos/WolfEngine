#nullable enable

using System;
using System.Collections.Generic;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct SharedDrawIndirectCommandPage
{
	public SharedDrawIndirectCommandPage(
		uint pageIndex,
		uint pageStartCommandIndex,
		uint pageCommandCapacity,
		IGfxIndirectCommandBuffer commandBuffer)
	{
		PageIndex = pageIndex;
		PageStartCommandIndex = pageStartCommandIndex;
		PageCommandCapacity = pageCommandCapacity;
		CommandBuffer = commandBuffer ?? throw new ArgumentNullException(nameof(commandBuffer));
	}

	public uint PageIndex { get; }
	public uint PageStartCommandIndex { get; }
	public uint PageCommandCapacity { get; }
	public IGfxIndirectCommandBuffer CommandBuffer { get; }
}

public sealed class SharedDrawIndirectCommandSet : IDisposable
{
	public const uint IndirectCommandPageCapacity = 2048;

	private readonly SortedDictionary<uint, IGfxIndirectCommandBuffer>[] _commandPages =
		new SortedDictionary<uint, IGfxIndirectCommandBuffer>[GpuDrawResources.IndirectCommandBufferSlotCount * GpuDrawExecutionLanes.ExecutionLaneCount];
	private readonly ulong[] _appliedStructuralVersions = new ulong[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly uint[] _bindlessEpochs = new uint[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly ulong[] _bindingVersions = new ulong[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly int[] _frameBindings = new int[GpuDrawResources.IndirectCommandBufferSlotCount];

	public SharedDrawIndirectCommandSet()
	{
		Array.Fill(_frameBindings, -1);
		for (var i = 0; i < _commandPages.Length; i++)
		{
			_commandPages[i] = new SortedDictionary<uint, IGfxIndirectCommandBuffer>();
		}
	}

	public void EnsureCreated(IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(device);
	}

	public IGfxIndirectCommandBuffer EnsurePage(
		IGfxDevice device,
		int slotIndex,
		int executionLaneIndex,
		uint pageIndex)
	{
		ArgumentNullException.ThrowIfNull(device);
		ValidateSlot(slotIndex);
		ValidateLane(executionLaneIndex);
		ValidatePage(pageIndex);

		var pages = _commandPages[FlattenSlotLaneIndex(slotIndex, executionLaneIndex)];
		if (pages.TryGetValue(pageIndex, out var commandBuffer))
		{
			return commandBuffer;
		}

		var laneName = ResolveLaneDebugName(executionLaneIndex);
		commandBuffer = device.CreateIndirectCommandBuffer(new IndirectCommandBufferDescriptor(
			PassKind.Graphics,
			IndirectCommandPageCapacity,
			supportsIndexedExecution: true,
			name: $"{laneName} slot{slotIndex} page{pageIndex}"));
		pages.Add(pageIndex, commandBuffer);
		return commandBuffer;
	}

	public IGfxIndirectCommandBuffer EnsurePageForCommand(
		IGfxDevice device,
		int slotIndex,
		int executionLaneIndex,
		uint commandIndex,
		out uint pageCommandIndex)
	{
		pageCommandIndex = GetPageCommandIndex(commandIndex);
		return EnsurePage(device, slotIndex, executionLaneIndex, GetPageIndex(commandIndex));
	}

	public bool TryGetPage(
		int slotIndex,
		int executionLaneIndex,
		uint pageIndex,
		out IGfxIndirectCommandBuffer commandBuffer)
	{
		ValidateSlot(slotIndex);
		ValidateLane(executionLaneIndex);
		ValidatePage(pageIndex);
		return _commandPages[FlattenSlotLaneIndex(slotIndex, executionLaneIndex)].TryGetValue(pageIndex, out commandBuffer!);
	}

	public bool TryGetPageForCommand(
		int slotIndex,
		int executionLaneIndex,
		uint commandIndex,
		out IGfxIndirectCommandBuffer commandBuffer,
		out uint pageCommandIndex)
	{
		pageCommandIndex = GetPageCommandIndex(commandIndex);
		return TryGetPage(slotIndex, executionLaneIndex, GetPageIndex(commandIndex), out commandBuffer);
	}

	public SharedDrawIndirectCommandPage[] GetAllocatedPages(int slotIndex, int executionLaneIndex)
	{
		ValidateSlot(slotIndex);
		ValidateLane(executionLaneIndex);

		var pages = _commandPages[FlattenSlotLaneIndex(slotIndex, executionLaneIndex)];
		if (pages.Count == 0)
		{
			return Array.Empty<SharedDrawIndirectCommandPage>();
		}

		var result = new SharedDrawIndirectCommandPage[pages.Count];
		var i = 0;
		foreach (var page in pages)
		{
			result[i++] = new SharedDrawIndirectCommandPage(
				page.Key,
				page.Key * IndirectCommandPageCapacity,
				IndirectCommandPageCapacity,
				page.Value);
		}

		return result;
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

	/// <param name="bindingVersion">
	/// <see cref="GpuDrawResources.IndirectBindingVersion"/> as it stands right now. Records bake the GPU
	/// virtual addresses of the shared buffers, so a slot encoded against an older version points at
	/// buffers that capacity growth has since replaced, and must be re-encoded before it is executed.
	/// </param>
	public bool RequiresFullReencode(int slotIndex, int frameSlot, uint bindlessEpoch, ulong bindingVersion)
	{
		ValidateSlot(slotIndex);
		return _frameBindings[slotIndex] != frameSlot ||
		       _bindlessEpochs[slotIndex] != bindlessEpoch ||
		       _bindingVersions[slotIndex] != bindingVersion;
	}

	public void MarkSlotEncoded(int slotIndex, int frameSlot, uint bindlessEpoch, ulong bindingVersion, ulong structuralVersion)
	{
		ValidateSlot(slotIndex);
		_frameBindings[slotIndex] = frameSlot;
		_bindlessEpochs[slotIndex] = bindlessEpoch;
		_bindingVersions[slotIndex] = bindingVersion;
		_appliedStructuralVersions[slotIndex] = structuralVersion;
	}

	public void Dispose()
	{
		for (var i = 0; i < _commandPages.Length; i++)
		{
			foreach (var commandBuffer in _commandPages[i].Values)
			{
				(commandBuffer as IDisposable)?.Dispose();
			}

			_commandPages[i].Clear();
		}
	}

	/// <summary>
	/// Lanes are stored in declaration order, which is not required to match their execution index, so
	/// resolve by the index rather than by position.
	/// </summary>
	private static string ResolveLaneDebugName(int executionLaneIndex)
	{
		var definitions = GpuDrawExecutionLanes.Definitions;
		for (var i = 0; i < definitions.Length; i++)
		{
			if (definitions[i].ExecutionIndex == executionLaneIndex)
			{
				return definitions[i].DebugName;
			}
		}

		return $"lane{executionLaneIndex}";
	}

	public static uint GetPageIndex(uint commandIndex) => commandIndex / IndirectCommandPageCapacity;

	public static uint GetPageCommandIndex(uint commandIndex) => commandIndex % IndirectCommandPageCapacity;

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

	private static void ValidatePage(uint pageIndex)
	{
		var maxPageIndex = (uint)((GpuDrawResources.MaxDrawCount - 1) / IndirectCommandPageCapacity);
		if (pageIndex > maxPageIndex)
		{
			throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, "Indirect command page index is out of range.");
		}
	}
}
