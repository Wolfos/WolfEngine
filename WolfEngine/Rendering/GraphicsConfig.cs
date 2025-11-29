#nullable enable

namespace WolfEngine.Rendering;

/// <summary>
/// Global render configuration toggles.
/// </summary>
public static class GraphicsConfig
{
	/// <summary>
	/// Enable the D3D12 debug layer when creating the device (Windows only).
	/// </summary>
	public static bool EnableD3DDebugLayer { get; set; } = false;
}
