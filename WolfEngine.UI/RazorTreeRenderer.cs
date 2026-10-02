using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

#pragma warning disable BL0006

namespace WolfEngine.UI;

internal sealed class RazorTreeRenderer : Renderer
{
	private readonly Dictionary<int, ArrayRange<RenderTreeFrame>> _frames = [];
	private readonly Stack<UiNode> _nodePool = [];
	private readonly StringBuilder _textRun = new();
	private Exception? _exception;
	private readonly record struct IdentityKey(long Parent, int Component, object? Key, int Sequence, int Occurrence, int Markup, string Name);
	private readonly Dictionary<IdentityKey, long> _identities = [];
	private readonly List<IdentityKey> _removedIdentities = [];
	private readonly Dictionary<(long Parent, int Component, int Sequence), int> _occurrences = [];
	private readonly HashSet<long> _liveIdentities = [];
	private readonly HashSet<string> _unboundEventWarnings = new(StringComparer.Ordinal);
	private long _nextIdentity;
	public bool DisplayChanged { get; set; }

	public RazorTreeRenderer(IServiceProvider services, UiDispatcher? dispatcher = null)
		: base(services, services.GetService(typeof(ILoggerFactory)) as ILoggerFactory ?? NullLoggerFactory.Instance)
	{
		var owner = dispatcher ?? new UiDispatcher();
		owner.Bind(); Dispatcher = owner;
	}

	public override Dispatcher Dispatcher { get; }

	public void DispatchMouse(ulong handler, MouseEventArgs args)
	{
		_ = Dispatcher.InvokeAsync(async () =>
		{
			try { await DispatchEventAsync(handler, null, args); }
			catch (Exception exception) { Console.Error.WriteLine($"[Gameplay UI] Mouse callback failed: {exception}"); }
		});
	}

	public int AttachRoot(Type componentType)
	{
		var component = InstantiateComponent(componentType);
		return AssignRootComponentId(component);
	}

	public void Render(int componentId, IReadOnlyDictionary<string, object?> parameters)
	{
		_exception = null;
		var dictionary = parameters as IDictionary<string, object?> ?? new Dictionary<string, object?>(parameters);
		var render = Dispatcher.InvokeAsync(() => RenderRootComponentAsync(componentId, ParameterView.FromDictionary(dictionary)));
		if (render.IsCompleted) render.GetAwaiter().GetResult();
		if (_exception is not null) throw new InvalidOperationException("Gameplay UI component render failed.", _exception);
	}

	public UiNode BuildTree(int rootComponentId)
	{
		var root = RentNode("root");
		_liveIdentities.Clear();
		_occurrences.Clear();
		AppendComponent(rootComponentId, root);
		CoalesceTextRuns(root);
		// Removed identities must never revive captures when a keyed element is recreated later.
		_removedIdentities.Clear();
		foreach (var pair in _identities) if (!_liveIdentities.Contains(pair.Value)) _removedIdentities.Add(pair.Key);
		foreach (var key in _removedIdentities) _identities.Remove(key);
		DisplayChanged = false;
		return root;
	}

	public void RecycleTree(UiNode? root)
	{
		if (root is null) return;
		for (var i = 0; i < root.Children.Count; i++) RecycleTree(root.Children[i]);
		root.Reset(string.Empty);
		_nodePool.Push(root);
	}

	protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
	{
		DisplayChanged = true;
		for (var i = 0; i < renderBatch.DisposedComponentIDs.Count; i++) _frames.Remove(renderBatch.DisposedComponentIDs.Array[i]);
		for (var i = 0; i < renderBatch.UpdatedComponents.Count; i++)
		{
			var componentId = renderBatch.UpdatedComponents.Array[i].ComponentId;
			_frames[componentId] = GetCurrentRenderTreeFrames(componentId);
		}
		return Task.CompletedTask;
	}

	protected override void HandleException(Exception exception)
	{
		_exception = exception;
		Console.Error.WriteLine($"[Gameplay UI] Component failed: {exception}");
	}

	private void AppendComponent(int componentId, UiNode parent)
	{
		if (!_frames.TryGetValue(componentId, out var range)) range = GetCurrentRenderTreeFrames(componentId);
		AppendRange(range.Array, 0, range.Count, parent, componentId);
	}

	private void AppendRange(RenderTreeFrame[] frames, int start, int count, UiNode parent, int componentId)
	{
		var end = start + count;
		for (var i = start; i < end;)
		{
			ref var frame = ref frames[i];
			switch (frame.FrameType)
			{
				case RenderTreeFrameType.Element:
				{
					var node = RentNode(frame.ElementName);
					node.Key = frame.ElementKey;
					var identityKey = new IdentityKey(parent.Identity, componentId, frame.ElementKey, frame.ElementKey is null ? frame.Sequence : 0,
						frame.ElementKey is null ? NextOccurrence(parent.Identity, componentId, frame.Sequence) : 0, -1, frame.ElementName);
					if (!_identities.TryGetValue(identityKey, out var identity)) _identities[identityKey] = identity = ++_nextIdentity;
					node.Identity = identity; _liveIdentities.Add(identity);
					var subtreeEnd = i + frame.ElementSubtreeLength;
					var child = i + 1;
					while (child < subtreeEnd && frames[child].FrameType == RenderTreeFrameType.Attribute)
					{
						node.Attributes[frames[child].AttributeName] = frames[child].AttributeValue;
						WarnUnboundEvent(frames[child].AttributeName);
						if (frames[child].AttributeEventHandlerId != 0)
							node.Events[frames[child].AttributeName] = frames[child].AttributeEventHandlerId;
						child++;
					}
					AppendRange(frames, child, subtreeEnd - child, node, componentId);
					parent.Children.Add(node);
					i = subtreeEnd;
					break;
				}
				case RenderTreeFrameType.Text:
					AppendText(frame.TextContent, parent);
					i++;
					break;
				case RenderTreeFrameType.Markup:
					AppendMarkup(frame.MarkupContent, parent, componentId, frame.Sequence, NextOccurrence(parent.Identity, componentId, frame.Sequence));
					i++;
					break;
				case RenderTreeFrameType.Component:
					AppendComponent(frame.ComponentId, parent);
					i += frame.ComponentSubtreeLength;
					break;
				case RenderTreeFrameType.Region:
					AppendRange(frames, i + 1, frame.RegionSubtreeLength - 1, parent, componentId);
					i += frame.RegionSubtreeLength;
					break;
				default:
					i++;
					break;
			}
		}
	}

	private int NextOccurrence(long parent, int component, int sequence)
	{
		var key = (parent, component, sequence);
		_occurrences.TryGetValue(key, out var count); _occurrences[key] = count + 1; return count;
	}

	private void AppendMarkup(string markup, UiNode parent, int componentId, int sequence, int occurrence)
	{
		if (string.IsNullOrEmpty(markup)) return;
		var stack = new Stack<UiNode>();
		stack.Push(parent);
		var position = 0;
		while (position < markup.Length)
		{
			var tagStart = markup.IndexOf('<', position);
			if (tagStart < 0)
			{
				AppendText(System.Net.WebUtility.HtmlDecode(markup[position..]), stack.Peek());
				break;
			}
			AppendText(System.Net.WebUtility.HtmlDecode(markup[position..tagStart]), stack.Peek());
			var tagEnd = markup.IndexOf('>', tagStart + 1);
			if (tagEnd < 0)
			{
				AppendText(System.Net.WebUtility.HtmlDecode(markup[tagStart..]), stack.Peek());
				break;
			}
			var tag = markup[(tagStart + 1)..tagEnd].Trim();
			position = tagEnd + 1;
			if (tag.StartsWith("!--", StringComparison.Ordinal)) continue;
			if (tag.StartsWith('/'))
			{
				if (stack.Count > 1) stack.Pop();
				continue;
			}

			var selfClosing = tag.EndsWith('/');
			if (selfClosing) tag = tag[..^1].TrimEnd();
			var separator = tag.IndexOfAny([' ', '\t', '\r', '\n']);
			var name = separator < 0 ? tag : tag[..separator];
			if (name.Length == 0) continue;
			var node = RentNode(name);
			var identityKey = new IdentityKey(parent.Identity, componentId, null, sequence, occurrence, tagStart, name);
			if (!_identities.TryGetValue(identityKey, out var identity)) _identities[identityKey] = identity = ++_nextIdentity;
			node.Identity = identity; _liveIdentities.Add(identity);
			if (separator >= 0) ParseAttributes(tag[(separator + 1)..], node.Attributes);
			foreach (var attribute in node.Attributes.Keys) WarnUnboundEvent(attribute);
			stack.Peek().Children.Add(node);
			if (!selfClosing && name is not ("br" or "img" or "input" or "meta" or "link")) stack.Push(node);
		}
	}

	private void AppendText(string text, UiNode parent)
	{
		if (!string.IsNullOrEmpty(text))
		{
			var node = RentNode("#text");
			node.Text = text;
			parent.Children.Add(node);
		}
	}

	// Razor splits literals, expressions and regions into separate frames. Those
	// boundaries are not layout boundaries: adjacent text under one element must
	// be measured, wrapped and painted together. Never merge across child elements.
	private void CoalesceTextRuns(UiNode parent)
	{
		var children = parent.Children;
		var write = 0;
		for (var read = 0; read < children.Count;)
		{
			var first = children[read];
			if (!first.IsText)
			{
				CoalesceTextRuns(first);
				children[write++] = first;
				read++;
				continue;
			}

			var end = read + 1;
			while (end < children.Count && children[end].IsText) end++;
			if (end > read + 1)
			{
				// Reuse one builder to avoid quadratic string concatenation for long runs.
				_textRun.Clear();
				for (var i = read; i < end; i++) _textRun.Append(children[i].Text);
				first.Text = _textRun.ToString();
				for (var i = read + 1; i < end; i++) RecycleTree(children[i]);
			}

			// Keep the existing treatment of indentation between elements, but retain
			// whitespace fragments within a meaningful run ("A", " ", "B" => "A B").
			if (string.IsNullOrWhiteSpace(first.Text)) RecycleTree(first);
			else children[write++] = first;
			read = end;
		}
		if (write < children.Count) children.RemoveRange(write, children.Count - write);
	}

	private UiNode RentNode(string name)
	{
		if (_nodePool.TryPop(out var node))
		{
			node.Reset(name);
			return node;
		}
		return new UiNode { Name = name };
	}

	private void WarnUnboundEvent(string attribute)
	{
		if (attribute.StartsWith("@on", StringComparison.Ordinal) && _unboundEventWarnings.Add(attribute))
			Console.Error.WriteLine($"[Gameplay UI] '{attribute}' is an unbound literal attribute. Add '@using Microsoft.AspNetCore.Components.Web' to the gameplay project's _Imports.razor to enable Blazor event directives.");
	}

	private static void ParseAttributes(string source, Dictionary<string, object?> attributes)
	{
		var position = 0;
		while (position < source.Length)
		{
			while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
			var nameStart = position;
			while (position < source.Length && !char.IsWhiteSpace(source[position]) && source[position] != '=') position++;
			if (position == nameStart) break;
			var name = source[nameStart..position];
			while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
			if (position >= source.Length || source[position] != '=')
			{
				attributes[name] = true;
				continue;
			}
			position++;
			while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
			if (position >= source.Length) { attributes[name] = string.Empty; break; }
			var quote = source[position] is '\'' or '"' ? source[position++] : '\0';
			var valueStart = position;
			if (quote == '\0') while (position < source.Length && !char.IsWhiteSpace(source[position])) position++;
			else while (position < source.Length && source[position] != quote) position++;
			attributes[name] = System.Net.WebUtility.HtmlDecode(source[valueStart..position]);
			if (quote != '\0' && position < source.Length) position++;
		}
	}
}

#pragma warning restore BL0006
