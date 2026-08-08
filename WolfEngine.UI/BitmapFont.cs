using System.Numerics;
using ImGuiNET;

namespace WolfEngine.UI;

/// <summary>A compact deterministic 5x7 HUD font rendered from the atlas white pixel.</summary>
internal static class BitmapFont
{
	public static void Draw(ImDrawListPtr draw, Vector2 position, float fontSize, uint color, string text)
	{
		var pixel = MathF.Max(1, fontSize / 7f);
		var originX = position.X;
		var x = originX;
		var y = position.Y;
		foreach (var source in text)
		{
			if (source == '\n')
			{
				x = originX;
				y += 8 * pixel;
				continue;
			}
			var rows = Glyph(char.ToUpperInvariant(source));
			for (var row = 0; row < rows.Length; row++)
			{
				for (var column = 0; column < 5; column++)
				{
					if ((rows[row] & (1 << (4 - column))) == 0) continue;
					var min = new Vector2(x + column * pixel, y + row * pixel);
					draw.AddRectFilled(min, min + new Vector2(pixel, pixel), color);
				}
			}
			x += 6 * pixel;
		}
	}

	private static ReadOnlySpan<byte> Glyph(char value) => value switch
	{
		'A' => [14,17,17,31,17,17,17], 'B' => [30,17,17,30,17,17,30],
		'C' => [14,17,16,16,16,17,14], 'D' => [30,17,17,17,17,17,30],
		'E' => [31,16,16,30,16,16,31], 'F' => [31,16,16,30,16,16,16],
		'G' => [14,17,16,23,17,17,15], 'H' => [17,17,17,31,17,17,17],
		'I' => [31,4,4,4,4,4,31], 'J' => [7,2,2,2,18,18,12],
		'K' => [17,18,20,24,20,18,17], 'L' => [16,16,16,16,16,16,31],
		'M' => [17,27,21,21,17,17,17], 'N' => [17,25,21,19,17,17,17],
		'O' => [14,17,17,17,17,17,14], 'P' => [30,17,17,30,16,16,16],
		'Q' => [14,17,17,17,21,18,13], 'R' => [30,17,17,30,20,18,17],
		'S' => [15,16,16,14,1,1,30], 'T' => [31,4,4,4,4,4,4],
		'U' => [17,17,17,17,17,17,14], 'V' => [17,17,17,17,17,10,4],
		'W' => [17,17,17,21,21,21,10], 'X' => [17,17,10,4,10,17,17],
		'Y' => [17,17,10,4,4,4,4], 'Z' => [31,1,2,4,8,16,31],
		'0' => [14,17,19,21,25,17,14], '1' => [4,12,4,4,4,4,14],
		'2' => [14,17,1,2,4,8,31], '3' => [30,1,1,14,1,1,30],
		'4' => [2,6,10,18,31,2,2], '5' => [31,16,16,30,1,1,30],
		'6' => [14,16,16,30,17,17,14], '7' => [31,1,2,4,8,8,8],
		'8' => [14,17,17,14,17,17,14], '9' => [14,17,17,15,1,1,14],
		':' => [0,4,4,0,4,4,0], '.' => [0,0,0,0,0,12,12],
		',' => [0,0,0,0,4,4,8], '-' => [0,0,0,31,0,0,0],
		'/' => [1,2,2,4,8,8,16], '+' => [0,4,4,31,4,4,0],
		'%' => [17,2,4,4,8,16,17], '_' => [0,0,0,0,0,0,31],
		' ' => [0,0,0,0,0,0,0], _ => [14,17,1,2,4,0,4]
	};
}
