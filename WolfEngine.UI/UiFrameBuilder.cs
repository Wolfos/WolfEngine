using System.Buffers;
using System.Numerics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.UI;

internal sealed class UiFrameBuilder
{
	private static readonly UiTextureAtlas WhiteAtlas = new()
	{
		Width = 1,
		Height = 1,
		PixelsRgba = [255, 255, 255, 255]
	};
	private readonly UiGeometryBuilder _geometry = new();

	public UiFrameData Build(UiNode root, int width, int height)
	{
		_geometry.Prepare();
		try
		{
			Append(_geometry, root, 1);
			return _geometry.BuildFrame(width, height, WhiteAtlas);
		}
		catch
		{
			_geometry.Dispose();
			throw;
		}
	}

	private static void Append(UiGeometryBuilder geometry, UiNode node, float inheritedOpacity)
	{
		if (!node.Style.Display) return;
		var opacity = inheritedOpacity * node.Style.Opacity;
		if (!node.IsText && node.Width > 0 && node.Height > 0 && node.Style.Background.A * opacity > 0.001f)
		{
			geometry.AddFilledRect(
				new Vector2(node.Left, node.Top),
				new Vector2(node.Left + node.Width, node.Top + node.Height),
				Pack(WithOpacity(node.Style.Background, opacity)),
				MathF.Max(0, node.Style.BorderRadius));
		}
		if (node.IsText && !string.IsNullOrEmpty(node.Text))
		{
			BitmapFont.Draw(geometry, new Vector2(node.Left, node.Top), node.Style.FontSize,
				Pack(WithOpacity(node.Style.Color, opacity)), node.Text!);
		}
		for (var i = 0; i < node.Children.Count; i++) Append(geometry, node.Children[i], opacity);
	}

	private static uint Pack(ColorRGBA value)
	{
		var r = (uint)Math.Clamp((int)MathF.Round(value.R * 255), 0, 255);
		var g = (uint)Math.Clamp((int)MathF.Round(value.G * 255), 0, 255);
		var b = (uint)Math.Clamp((int)MathF.Round(value.B * 255), 0, 255);
		var a = (uint)Math.Clamp((int)MathF.Round(value.A * 255), 0, 255);
		return r | (g << 8) | (b << 16) | (a << 24);
	}

	private static ColorRGBA WithOpacity(ColorRGBA value, float opacity) =>
		new(value.R, value.G, value.B, value.A * opacity);
}

/// <summary>Builds backend-neutral UI geometry directly into pooled frame buffers.</summary>
internal sealed class UiGeometryBuilder : IDisposable
{
	private const int InitialVertexCapacity = 4096;
	private const int InitialIndexCapacity = 6144;
	private static readonly Vector2 WhiteUv = new(0.5f, 0.5f);
	private UiVertex[] _vertices = ArrayPool<UiVertex>.Shared.Rent(InitialVertexCapacity);
	private uint[] _indices = ArrayPool<uint>.Shared.Rent(InitialIndexCapacity);
	private int _vertexCount;
	private int _indexCount;
	private bool _transferred;

	/// <summary>Starts another build after the previous buffers were transferred to a frame.</summary>
	public void Prepare()
	{
		if (!_transferred)
		{
			if (_vertexCount == 0 && _indexCount == 0) return;
			throw new InvalidOperationException("UI geometry build is already in progress.");
		}

		_vertices = ArrayPool<UiVertex>.Shared.Rent(InitialVertexCapacity);
		_indices = ArrayPool<uint>.Shared.Rent(InitialIndexCapacity);
		_vertexCount = 0;
		_indexCount = 0;
		_transferred = false;
	}

	public void AddFilledRect(Vector2 min, Vector2 max, uint color, float radius = 0)
	{
		if (max.X <= min.X || max.Y <= min.Y) return;
		var clampedRadius = MathF.Min(MathF.Max(radius, 0), MathF.Min(max.X - min.X, max.Y - min.Y) * 0.5f);
		if (clampedRadius < 0.5f)
		{
			AddQuad(min, max, color);
			return;
		}

		var segmentsPerCorner = Math.Clamp((int)MathF.Ceiling(clampedRadius * 0.25f), 2, 12);
		var perimeterCount = segmentsPerCorner * 4 + 4;
		EnsureVertices(perimeterCount + 1);
		EnsureIndices(perimeterCount * 3);

		var centerIndex = (uint)_vertexCount;
		_vertices[_vertexCount++] = new UiVertex((min + max) * 0.5f, WhiteUv, color);
		var firstPerimeterIndex = (uint)_vertexCount;
		AddCorner(new Vector2(max.X - clampedRadius, min.Y + clampedRadius), clampedRadius,
			-MathF.PI * 0.5f, 0, segmentsPerCorner, color);
		AddCorner(new Vector2(max.X - clampedRadius, max.Y - clampedRadius), clampedRadius,
			0, MathF.PI * 0.5f, segmentsPerCorner, color);
		AddCorner(new Vector2(min.X + clampedRadius, max.Y - clampedRadius), clampedRadius,
			MathF.PI * 0.5f, MathF.PI, segmentsPerCorner, color);
		AddCorner(new Vector2(min.X + clampedRadius, min.Y + clampedRadius), clampedRadius,
			MathF.PI, MathF.PI * 1.5f, segmentsPerCorner, color);

		for (var i = 0; i < perimeterCount; i++)
		{
			_indices[_indexCount++] = centerIndex;
			_indices[_indexCount++] = firstPerimeterIndex + (uint)i;
			_indices[_indexCount++] = firstPerimeterIndex + (uint)((i + 1) % perimeterCount);
		}
	}

	private void AddQuad(Vector2 min, Vector2 max, uint color)
	{
		EnsureVertices(4);
		EnsureIndices(6);
		var first = (uint)_vertexCount;
		_vertices[_vertexCount++] = new UiVertex(min, WhiteUv, color);
		_vertices[_vertexCount++] = new UiVertex(new Vector2(max.X, min.Y), WhiteUv, color);
		_vertices[_vertexCount++] = new UiVertex(max, WhiteUv, color);
		_vertices[_vertexCount++] = new UiVertex(new Vector2(min.X, max.Y), WhiteUv, color);
		_indices[_indexCount++] = first;
		_indices[_indexCount++] = first + 1;
		_indices[_indexCount++] = first + 2;
		_indices[_indexCount++] = first;
		_indices[_indexCount++] = first + 2;
		_indices[_indexCount++] = first + 3;
	}

	private void AddCorner(Vector2 center, float radius, float startAngle, float endAngle, int segments, uint color)
	{
		for (var i = 0; i <= segments; i++)
		{
			var angle = startAngle + (endAngle - startAngle) * (i / (float)segments);
			var position = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
			_vertices[_vertexCount++] = new UiVertex(position, WhiteUv, color);
		}
	}

	public UiFrameData BuildFrame(int width, int height, UiTextureAtlas whiteAtlas)
	{
		ObjectDisposedException.ThrowIf(_transferred, this);
		var commands = ArrayPool<UiDrawCommand>.Shared.Rent(1);
		var commandCount = _indexCount > 0 ? 1 : 0;
		if (commandCount > 0)
		{
			commands[0] = new UiDrawCommand(
				_indexCount,
				0,
				0,
				new Vector4(0, 0, width, height),
				UiTextureIds.FontAtlas);
		}

		_transferred = true;
		var frame = new UiFrameData
		{
			VertexCount = _vertexCount,
			IndexCount = _indexCount,
			CommandCount = commandCount,
			DisplayPos = Vector2.Zero,
			DisplaySize = new Vector2(width, height),
			FramebufferSize = new Vector2(width, height),
			DeltaTime = 1f / 60f,
			Vertices = _vertices,
			Indices = _indices,
			Commands = commands,
			HasFontAtlas = true,
			FontAtlas = whiteAtlas
		};
		frame.SetRelease(ReturnPooledFrame);
		return frame;
	}

	private void EnsureVertices(int additional)
	{
		if (_vertexCount <= _vertices.Length - additional) return;
		var replacement = ArrayPool<UiVertex>.Shared.Rent(Math.Max(_vertexCount + additional, _vertices.Length * 2));
		_vertices.AsSpan(0, _vertexCount).CopyTo(replacement);
		ArrayPool<UiVertex>.Shared.Return(_vertices, clearArray: false);
		_vertices = replacement;
	}

	private void EnsureIndices(int additional)
	{
		if (_indexCount <= _indices.Length - additional) return;
		var replacement = ArrayPool<uint>.Shared.Rent(Math.Max(_indexCount + additional, _indices.Length * 2));
		_indices.AsSpan(0, _indexCount).CopyTo(replacement);
		ArrayPool<uint>.Shared.Return(_indices, clearArray: false);
		_indices = replacement;
	}

	private static void ReturnPooledFrame(UiFrameData frame)
	{
		if (frame.Vertices.Length > 0) ArrayPool<UiVertex>.Shared.Return(frame.Vertices, clearArray: false);
		if (frame.Indices.Length > 0) ArrayPool<uint>.Shared.Return(frame.Indices, clearArray: false);
		if (frame.Commands.Length > 0) ArrayPool<UiDrawCommand>.Shared.Return(frame.Commands, clearArray: false);
	}

	public void Dispose()
	{
		if (_transferred) return;
		ArrayPool<UiVertex>.Shared.Return(_vertices, clearArray: false);
		ArrayPool<uint>.Shared.Return(_indices, clearArray: false);
		_transferred = true;
	}
}
