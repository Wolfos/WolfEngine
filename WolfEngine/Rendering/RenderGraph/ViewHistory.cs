using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// The ping-pong history of one temporal effect in one view: a set of persistent textures that a pass reads
/// last frame's result from (<see cref="Previous"/>) and writes this frame's result to (<see cref="Current"/>),
/// swapped when the frame completes.
/// </summary>
/// <remarks>
/// Every temporal effect follows the same rules, and they live here so they are written once:
/// <list type="bullet">
/// <item>The textures are reallocated when the device, backend or shape changes, and history is invalid
/// afterwards, because the previous side holds nothing.</item>
/// <item>The sides only swap in <see cref="Complete"/>, and only when this frame imported them, so a frame
/// that did not run the effect leaves history invalid rather than reading a side nobody wrote.</item>
/// <item>The last state each texture was left in is read back on completion, so next frame's import starts
/// from the right state.</item>
/// <item>Release goes through the device's retirement queue, so textures outlive in-flight submissions.</item>
/// </list>
/// A history holds several slots when an effect writes several textures that must swap together, such as
/// colour and depth for TAA. Slots never swap independently.
/// </remarks>
internal abstract class ViewHistory
{
	private const int SideCount = 2;

	private readonly IGfxTexture?[] _textures;
	private readonly ResourceState[] _states;
	private readonly RenderGraphResourceHandle[] _previousHandles;
	private readonly RenderGraphResourceHandle[] _currentHandles;
	private int _readIndex;
	private bool _valid;
	private bool _imported;

	protected ViewHistory(string name, int slotCount)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);

		Name = name;
		SlotCount = slotCount;
		_textures = new IGfxTexture?[slotCount * SideCount];
		_states = new ResourceState[slotCount * SideCount];
		_previousHandles = new RenderGraphResourceHandle[slotCount];
		_currentHandles = new RenderGraphResourceHandle[slotCount];
	}

	/// <summary>Names the effect in diagnostics.</summary>
	public string Name { get; }

	/// <summary>How many textures swap together.</summary>
	public int SlotCount { get; }

	/// <summary>The device the textures were created on, or null while nothing is allocated.</summary>
	public IGfxDevice? Device { get; private set; }

	public bool IsAllocated => Device is not null;

	/// <summary>
	/// Whether <see cref="Previous"/> holds a completed frame at the current shape. A pass must not blend with
	/// history while this is false.
	/// </summary>
	public bool IsValid => _valid;

	/// <summary>Whether this frame imported the history into the graph, so <see cref="Complete"/> will swap it.</summary>
	public bool IsImported => _imported;

	/// <summary>Which side is <see cref="Previous"/>. Exposed so tests can observe the swap.</summary>
	public int ReadIndex => _readIndex;

	/// <summary>Last frame's result in <paramref name="slot"/>. Only meaningful after <see cref="Import"/> this frame.</summary>
	public RenderGraphResourceHandle Previous(int slot = 0)
	{
		return _imported ? _previousHandles[slot] : default;
	}

	/// <summary>This frame's result in <paramref name="slot"/>. Only meaningful after <see cref="Import"/> this frame.</summary>
	public RenderGraphResourceHandle Current(int slot = 0)
	{
		return _imported ? _currentHandles[slot] : default;
	}

	/// <summary>The texture behind <paramref name="slot"/> on <paramref name="side"/>, for diagnostics and tests.</summary>
	public IGfxTexture? GetTexture(int slot, int side)
	{
		return _textures[TextureIndex(slot, side)];
	}

	/// <summary>
	/// Marks history unusable for this frame, for a change the shape does not capture, such as a resize of the
	/// window a view is sized against. The textures are kept, and the next completed frame makes it valid again.
	/// </summary>
	public void Invalidate()
	{
		_valid = false;
	}

	/// <summary>
	/// Imports both sides into this frame's graph. <see cref="Previous"/> and <see cref="Current"/> return
	/// handles from here until <see cref="Complete"/>.
	/// </summary>
	public void Import(RenderGraphResourceRegistry resources)
	{
		if (IsAllocated == false)
		{
			throw new InvalidOperationException($"View history '{Name}' was imported before it was allocated.");
		}

		var writeIndex = 1 - _readIndex;
		for (var slot = 0; slot < SlotCount; slot++)
		{
			_previousHandles[slot] = ImportSide(resources, slot, _readIndex);
			_currentHandles[slot] = ImportSide(resources, slot, writeIndex);
		}

		_imported = true;
	}

	/// <summary>
	/// Ends the frame. When this frame imported the history, records the state each texture was left in and
	/// swaps the sides, so this frame's result becomes next frame's <see cref="Previous"/>. Otherwise the effect
	/// did not run and history is invalid.
	/// </summary>
	public void Complete(RenderGraphResourceRegistry resources)
	{
		if (_imported == false)
		{
			_valid = false;
			return;
		}

		var writeIndex = 1 - _readIndex;
		for (var slot = 0; slot < SlotCount; slot++)
		{
			_states[TextureIndex(slot, _readIndex)] = resources.GetResourceState(_previousHandles[slot]);
			_states[TextureIndex(slot, writeIndex)] = resources.GetResourceState(_currentHandles[slot]);
		}

		_readIndex = writeIndex;
		_valid = true;
		_imported = false;
	}

	/// <summary>Retires every texture and returns to the unallocated state.</summary>
	public void Release()
	{
		for (var i = 0; i < _textures.Length; i++)
		{
			if (_textures[i] is IGfxTexture texture)
			{
				TemporalResourceRetirement.RetireTexture(Device, texture, _states[i]);
			}

			_textures[i] = null;
			_states[i] = ResourceState.Common;
		}

		Device = null;
		_readIndex = 0;
		_valid = false;
		_imported = false;
		OnReleased();
	}

	protected void Allocate(IGfxDevice device, Func<int, TextureDescriptor> describeSlot)
	{
		for (var slot = 0; slot < SlotCount; slot++)
		{
			var descriptor = describeSlot(slot);
			for (var side = 0; side < SideCount; side++)
			{
				var index = TextureIndex(slot, side);
				_textures[index] = device.CreateTexture(descriptor);
				_states[index] = ResourceState.UnorderedAccess;
			}
		}

		Device = device;
		_readIndex = 0;
		_valid = false;
		_imported = false;
	}

	protected abstract void OnReleased();

	private RenderGraphResourceHandle ImportSide(RenderGraphResourceRegistry resources, int slot, int side)
	{
		var index = TextureIndex(slot, side);

		return resources.ImportTexture(
			_textures[index]!,
			takeOwnership: false,
			initialState: _states[index]);
	}

	private static int TextureIndex(int slot, int side)
	{
		return slot * SideCount + side;
	}
}

/// <summary>
/// A <see cref="ViewHistory"/> whose textures are described by a family-specific shape. Any change to the shape,
/// such as a resize or a format change, reallocates the textures and invalidates history.
/// </summary>
/// <typeparam name="TShape">
/// Everything history is only valid against. Include values that do not affect the textures but do change what
/// they mean, such as the fog's maximum distance, which rescales the froxel slices.
/// </typeparam>
internal sealed class ViewHistory<TShape> : ViewHistory
	where TShape : struct, IEquatable<TShape>
{
	private readonly Func<TShape, int, TextureDescriptor> _describe;
	private GraphicsBackendKind? _backendKind;
	private TShape _shape;

	/// <param name="name">Names the effect in diagnostics.</param>
	/// <param name="slotCount">How many textures swap together.</param>
	/// <param name="describe">The descriptor of a slot at a shape. Both sides of a slot share it.</param>
	public ViewHistory(string name, int slotCount, Func<TShape, int, TextureDescriptor> describe)
		: base(name, slotCount)
	{
		_describe = describe;
	}

	/// <summary>The shape the textures were allocated at. Default while nothing is allocated.</summary>
	public TShape Shape => _shape;

	/// <summary>
	/// Allocates the textures at <paramref name="shape"/>, reallocating if the device, backend or shape changed.
	/// </summary>
	/// <returns>
	/// True when this call allocated, so the history is new and invalid. Families with companion resources that
	/// share the history's lifetime reset them when this returns true.
	/// </returns>
	public bool Ensure(IGfxDevice device, TShape shape)
	{
		if (IsAllocated &&
		    ReferenceEquals(Device, device) &&
		    _backendKind == device.BackendKind &&
		    _shape.Equals(shape))
		{
			return false;
		}

		Release();
		Allocate(device, slot => _describe(shape, slot));
		_backendKind = device.BackendKind;
		_shape = shape;

		return true;
	}

	protected override void OnReleased()
	{
		_backendKind = null;
		_shape = default;
	}
}
