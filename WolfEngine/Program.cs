using System.Runtime.InteropServices;

namespace WolfEngine;

class Program
{
	private const int ScreenWidth = 1280;
	private const int ScreenHeight = 720;

	static void Main(string[] args)
	{
		if (OperatingSystem.IsMacOS())
		{
			var renderer = new WolfRendererMetal(ScreenWidth, ScreenHeight);
		}
		else if (OperatingSystem.IsWindows())
		{
			var renderer = new WolfRendererD3D(ScreenWidth, ScreenHeight);
		}
	}
}