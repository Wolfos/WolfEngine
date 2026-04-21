#nullable enable

using System;

namespace WolfEngine.Rendering;

/// <summary>
/// Global render configuration toggles.
/// </summary>
public static class GraphicsConfig
{
	/// <summary>
	/// Enable the D3D12 debug layer when creating the device (Windows only).
	/// </summary>
	public static bool EnableD3DDebugLayer { get; set; } = true;

	/// <summary>
	/// Enable the hardening stress path that forces rapid draw churn.
	/// </summary>
	public static bool GpuHardeningStressEnabled { get; } =
		string.Equals(Environment.GetEnvironmentVariable("WOLF_GPU_HARDENING_STRESS"), "1", StringComparison.Ordinal);

	/// <summary>
	/// Number of frames between hardening metric logs.
	/// </summary>
	public static int GpuHardeningLogIntervalFrames { get; } =
		ParsePositiveIntEnvironmentVariable("WOLF_GPU_HARDENING_LOG_INTERVAL", 0);

	private static int ParsePositiveIntEnvironmentVariable(string name, int fallback)
	{
		var raw = Environment.GetEnvironmentVariable(name);
		if (int.TryParse(raw, out var parsed) && parsed > 0)
		{
			return parsed;
		}

		return fallback;
	}
}
