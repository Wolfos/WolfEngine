using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

public static class Screen
{
	public static Int2 CurrentResolution { get; internal set; }

	public static bool VSyncEnabled { get; set; } = true;
}
