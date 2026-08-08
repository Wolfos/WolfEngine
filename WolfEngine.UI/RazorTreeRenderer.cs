using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable BL0006

namespace WolfEngine.UI;

internal sealed class RazorTreeRenderer : Renderer
{
	private readonly Dictionary<int, ArrayRange<RenderTreeFrame>> _frames = [];
	private readonly Stack<UiNode> _nodePool = [];
	private Exception? _exception;

	public RazorTreeRenderer(IServiceProvider services)
		: base(services, services.GetService(typeof(ILoggerFactory)) as ILoggerFactory ?? NullLoggerFactory.Instance)
	{
	}

	public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

	public int AttachRoot(Type componentType)
	{
		var component = InstantiateComponent(componentType);
		return AssignRootComponentId(component);
	}

	public void Render(int componentId, IReadOnlyDictionary<string, object?> parameters)
	{
		_exception = null;
		var dictionary = parameters as IDictionary<string, object?> ?? new Dictionary<string, object?>(parameters);
		Dispatcher.InvokeAsync(() => RenderRootComponentAsync(componentId, ParameterView.FromDictionary(dictionary)))
			.GetAwaiter().GetResult();
		if (_exception is not null) throw new InvalidOperationException("Gameplay UI component render failed.", _exception);
	}

	public UiNode BuildTree(int rootComponentId)
	{
		var root = RentNode("root");
		AppendComponent(rootComponentId, root);
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
		for (var i = 0; i < renderBatch.UpdatedComponents.Count; i++)
		{
			var componentId = renderBatch.UpdatedComponents.Array[i].ComponentId;
			_frames[componentId] = GetCurrentRenderTreeFrames(componentId);
		}
		return Task.CompletedTask;
	}

	protected override void HandleException(Exception exception) => _exception = exception;

	private void AppendComponent(int componentId, UiNode parent)
	{
		if (!_frames.TryGetValue(componentId, out var range)) range = GetCurrentRenderTreeFrames(componentId);
		AppendRange(range.Array, 0, range.Count, parent);
	}

	private void AppendRange(RenderTreeFrame[] frames, int start, int count, UiNode parent)
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
					var subtreeEnd = i + frame.ElementSubtreeLength;
					var child = i + 1;
					while (child < subtreeEnd && frames[child].FrameType == RenderTreeFrameType.Attribute)
					{
						node.Attributes[frames[child].AttributeName] = frames[child].AttributeValue;
						child++;
					}
					AppendRange(frames, child, subtreeEnd - child, node);
					parent.Children.Add(node);
					i = subtreeEnd;
					break;
				}
				case RenderTreeFrameType.Text:
					if (!string.IsNullOrWhiteSpace(frame.TextContent))
					{
						var text = RentNode("#text");
						text.Text = frame.TextContent;
						parent.Children.Add(text);
					}
					i++;
					break;
				case RenderTreeFrameType.Markup:
					AppendMarkup(frame.MarkupContent, parent);
					i++;
					break;
				case RenderTreeFrameType.Component:
					AppendComponent(frame.ComponentId, parent);
					i += frame.ComponentSubtreeLength;
					break;
				case RenderTreeFrameType.Region:
					AppendRange(frames, i + 1, frame.RegionSubtreeLength - 1, parent);
					i += frame.RegionSubtreeLength;
					break;
				default:
					i++;
					break;
			}
		}
	}

	private void AppendMarkup(string markup, UiNode parent)
	{
		if (string.IsNullOrWhiteSpace(markup)) return;
		var stack = new Stack<UiNode>();
		stack.Push(parent);
		var position = 0;
		while (position < markup.Length)
		{
			var tagStart = markup.IndexOf('<', position);
			if (tagStart < 0)
			{
				AppendText(markup[position..], stack.Peek());
				break;
			}
			AppendText(markup[position..tagStart], stack.Peek());
			var tagEnd = markup.IndexOf('>', tagStart + 1);
			if (tagEnd < 0)
			{
				AppendText(markup[tagStart..], stack.Peek());
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
			if (separator >= 0) ParseAttributes(tag[(separator + 1)..], node.Attributes);
			stack.Peek().Children.Add(node);
			if (!selfClosing && name is not ("br" or "img" or "input" or "meta" or "link")) stack.Push(node);
		}
	}

	private void AppendText(string text, UiNode parent)
	{
		if (!string.IsNullOrWhiteSpace(text))
		{
			var node = RentNode("#text");
			node.Text = System.Net.WebUtility.HtmlDecode(text);
			parent.Children.Add(node);
		}
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
