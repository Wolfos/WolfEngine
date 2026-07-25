#nullable enable

using System;
using System.Runtime.InteropServices;

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

	/// <summary>
	/// Force per-pass GPU debug markers on.
	/// Prefer <see cref="ShouldEmitGpuMarkers"/>, which also picks up an attached capture tool.
	/// </summary>
	public static bool EnableGpuMarkers { get; set; } =
		string.Equals(Environment.GetEnvironmentVariable("WOLF_GPU_MARKERS"), "1", StringComparison.Ordinal);

	/// <summary>
	/// Whether GPU markers should be recorded right now.
	/// </summary>
	public static bool ShouldEmitGpuMarkers() => EnableGpuMarkers || IsCaptureToolAttached();

	/// <summary>
	/// Break into the debugger on D3D/DXGI debug layer errors. With no debugger attached a break is a
	/// fail-fast, which kills the editor over messages it could have simply logged, so this is opt-in via
	/// WOLF_D3D_DEBUG_BREAK=1. Messages are always dumped to the console regardless.
	/// </summary>
	public static bool BreakOnD3DDebugError { get; } =
		string.Equals(Environment.GetEnvironmentVariable("WOLF_D3D_DEBUG_BREAK"), "1", StringComparison.Ordinal);

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

	/// <summary>
	/// A capture tool injects its own module into the process, so a loaded module handle is enough to know
	/// markers will actually be read by something.
	/// </summary>
	private static bool IsCaptureToolAttached()
	{
		if (OperatingSystem.IsWindows() == false)
		{
			return false;
		}

		try
		{
			return GetModuleHandleW("WinPixGpuCapturer.dll") != IntPtr.Zero ||
			       GetModuleHandleW("renderdoc.dll") != IntPtr.Zero;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	[DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
	private static extern IntPtr GetModuleHandleW(string moduleName);

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
