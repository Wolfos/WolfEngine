using System.Numerics;
using Microsoft.AspNetCore.Components.Web;
using WolfEngine.Input;

namespace WolfEngine.UI;

internal sealed class UiPointerController(GameplayUiSurface surface)
{
	private readonly List<long> _hover = [];
	private readonly List<long> _hit = [];
	private readonly HashSet<long> _active = [];
	private readonly List<long> _statePath = [];
	private readonly long?[] _captures = new long?[5];
	private PointerInputEvent? _lastInput;
	private long _hitRevision = -1;
	private PointerInputContext _hitContext;
	private Vector2 _hitPosition;
	private Vector2 _position;
	private PointerInputContext _context;
	private bool _enabled;
	public void BeginFrame(PointerInputContext context)
	{
		var contextChanged = _context != context;
		_context = context;
		_enabled = context.Enabled && context.Focused && context.Size.X > 0 && context.Size.Y > 0;
		if (!_enabled) Cancel();
		ValidateCaptures();
		if (_enabled && _lastInput is { } last && (contextChanged || _hitRevision != surface.Performance.Revision)) UpdateHover(last);
	}
	public void ApplyState(UiNode node)
	{
		_active.Clear();
		foreach (var capture in _captures)
			if (capture is { } id && FindPath(node, id, _statePath))
				foreach (var ancestor in _statePath) _active.Add(ancestor);
		ApplyStateRecursive(node);
	}
	private void ApplyStateRecursive(UiNode node)
	{
		node.Hovered = !node.IsText && _hover.Contains(node.Identity);
		node.Active = !node.IsText && _active.Contains(node.Identity);
		foreach (var child in node.Children) ApplyStateRecursive(child);
	}
	public void Cancel()
	{
		var changed = _hover.Count > 0 || _captures.Any(c => c.HasValue);
		_hover.Clear(); Array.Clear(_captures);
		if (changed) surface.RefreshInteraction();
	}
	private void ValidateCaptures()
	{
		var changed = false;
		for (var i = 0; i < _captures.Length; i++)
			if (_captures[i] is { } id)
			{
				var invalid = !FindPath(surface.Root, id, _statePath);
				foreach (var ancestor in _statePath)
					if (Find(surface.Root, ancestor) is not { } node || node.Disabled || !node.Style.Display) { invalid = true; break; }
				if (invalid) { _captures[i] = null; changed = true; }
			}
		if (changed) surface.RefreshInteraction();
	}
	public bool Route(PointerInputEvent input)
	{
		if (!_enabled) return false;
		_lastInput = input;
		UpdateHover(input);
		long? target = null;
		for (var i = _hover.Count - 1; i >= 0; i--)
			if (Find(surface.Root, _hover[i]) is { } node && (node.Name == "button" || node.Events.Count > 0)) { target = node.Identity; break; }
		var interactive = target.HasValue;
		var button = PointerInputQueue.Button(input.Binding);
		if (button >= 0)
		{
			if (input.Pressed)
			{
				if (!interactive) return false;
				if (IsDisabled(target!.Value)) return true;
				_captures[button] = target;
				surface.RefreshInteraction();
				Dispatch(target.Value, "onmousedown", input, true);
				ValidateCaptures(); return true;
			}
			if (_captures[button] is { } capture)
			{
				_captures[button] = null;
				if (!IsDisabled(capture))
				{
					Dispatch(capture, "onmouseup", input, true);
					if (button == 0 && target == capture && Find(surface.Root, capture) is not null && !IsDisabled(capture)) Dispatch(capture, "onclick", input, true);
				}
				surface.RefreshInteraction(); return true;
			}
			return false;
		}
		if (input.Binding == InputActionBinding.MousePosition)
		{
			var capture = _captures.FirstOrDefault(c => c.HasValue);
			if (capture.HasValue || _hover.Count > 0) Dispatch(capture ?? _hover[^1], "onmousemove", input, true);
		}
		return interactive || _captures.Any(c => c.HasValue);
	}
	private bool IsDisabled(long id)
	{
		var path = new List<long>();
		return !FindPath(surface.Root, id, path) || path.Any(item => Find(surface.Root, item)?.Disabled == true);
	}
	private void UpdateHover(PointerInputEvent input)
	{
		if (_hitRevision == surface.Performance.Revision && _hitContext == _context && _hitPosition == input.Position) return;
		_hitContext = _context; _hitPosition = input.Position;
		_position = (input.Position - _context.Origin) * new Vector2(surface.LogicalWidth / _context.Size.X, surface.LogicalHeight / _context.Size.Y);
		_hit.Clear();
		if (_context.Available && _position.X >= 0 && _position.Y >= 0 && _position.X < surface.LogicalWidth && _position.Y < surface.LogicalHeight)
			Hit(surface.Root, _position, new Vector4(0, 0, surface.LogicalWidth, surface.LogicalHeight), _hit);
		_hitRevision = surface.Performance.Revision;
		if (_hover.SequenceEqual(_hit)) return;
		var old = _hover.ToArray(); var next = _hit.ToArray();
		_hover.Clear(); _hover.AddRange(next);
		foreach (var id in old.Reverse()) if (!next.Contains(id)) Dispatch(id, "onmouseleave", input, false);
		foreach (var id in next) if (!old.Contains(id)) Dispatch(id, "onmouseenter", input, false);
		surface.RefreshInteraction();
		_hitRevision = surface.Performance.Revision;
	}
	private void Dispatch(long identity, string name, PointerInputEvent input, bool bubble)
	{
		var path = new List<long>(); if (!FindPath(surface.Root, identity, path)) return;
		for (var i = path.Count - 1; i >= 0; i--)
		{
			var node = Find(surface.Root, path[i]); if (node is null) continue;
			if (node.Disabled || IsDisabled(node.Identity)) return;
			var stop = node.Attributes.TryGetValue("__internal_stopPropagation_" + name, out var value) && value is true;
			if (node.Events.TryGetValue(name, out var handler))
			{
				surface.DispatchMouse(handler, new MouseEventArgs { ClientX = _position.X, ClientY = _position.Y,
					OffsetX = _position.X - node.Left, OffsetY = _position.Y - node.Top,
					Button = Math.Max(0, PointerInputQueue.Button(input.Binding)), Buttons = input.Buttons, Type = name[2..], Detail = name == "onclick" ? 1 : 0,
					ShiftKey = input.ShiftKey, CtrlKey = input.CtrlKey, AltKey = input.AltKey, MetaKey = input.MetaKey });
			}
			if (!bubble || stop) return;
		}
	}
	internal static UiNode? Find(UiNode? node, long identity)
	{
		if (node is null) return null; if (node.Identity == identity) return node;
		foreach (var child in node.Children) if (Find(child, identity) is { } found) return found;
		return null;
	}
	private static bool FindPath(UiNode? node, long identity, List<long> path)
	{
		path.Clear(); return AppendPath(node, identity, path);
	}
	private static bool AppendPath(UiNode? current, long identity, List<long> path)
	{
		if (current is null) return false;
		path.Add(current.Identity); if (current.Identity == identity) return true;
		foreach (var child in current.Children) if (AppendPath(child, identity, path)) return true;
		path.RemoveAt(path.Count - 1); return false;
	}
	internal static bool Hit(UiNode? node, Vector2 position, Vector4 clip, List<long> path)
	{
		if (node is null || !node.Style.Display || position.X < clip.X || position.Y < clip.Y || position.X >= clip.Z || position.Y >= clip.W) return false;
		var count = path.Count; path.Add(node.Identity);
		var childClip = node.Style.ClipOverflow ? new Vector4(Math.Max(clip.X, node.Left), Math.Max(clip.Y, node.Top),
			Math.Min(clip.Z, node.Left + node.Width), Math.Min(clip.W, node.Top + node.Height)) : clip;
		for (var i = node.Children.Count - 1; i >= 0; i--) if (Hit(node.Children[i], position, childClip, path)) return true;
		if (!node.IsText && node.Style.PointerEvents && position.X >= node.Left && position.Y >= node.Top && position.X < node.Left + node.Width && position.Y < node.Top + node.Height) return true;
		path.RemoveRange(count, path.Count - count); return false;
	}
}
