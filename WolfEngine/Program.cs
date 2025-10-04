namespace WolfEngine;

class Program
{
	private const int ScreenWidth = 640;
	private const int ScreenHeight = 480;

	static void Main(string[] args)
	{
		var renderer = new WolfRendererD3D(ScreenWidth, ScreenHeight);
	}
}