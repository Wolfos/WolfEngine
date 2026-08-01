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
	/// Enable the D3D12 debug layer when creating the device (Windows only). The layer costs frame time,
	/// so it is opt-in via WOLF_D3D_DEBUG_LAYER=1; scripts/windows/run-editor-diagnostics.cmd sets it.
	/// Without it the runtime reports every rejected call as a bare E_INVALIDARG with no explanation.
	/// </summary>
	public static bool EnableD3DDebugLayer { get; set; } =
		string.Equals(Environment.GetEnvironmentVariable("WOLF_D3D_DEBUG_LAYER"), "1", StringComparison.Ordinal);

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
	/// Validate, before every ExecuteIndirect, that the buffers whose GPU virtual addresses were baked into the
	/// indirect command records are still alive at those same addresses.
	/// </summary>
	public static bool ValidateIndirectCommandBuffers { get; } =
		string.Equals(Environment.GetEnvironmentVariable("WOLF_VALIDATE_INDIRECT"), "1", StringComparison.Ordinal);

	/// <summary>
	/// Enable the hardening stress path that forces rapid draw churn.
	/// </summary>
	public static bool GpuHardeningStressEnabled { get; } =
		string.Equals(Environment.GetEnvironmentVariable("WOLF_GPU_HARDENING_STRESS"), "1", StringComparison.Ordinal);

	/// <summary>
	/// 1-based frame index to take a programmatic GPU capture of, or 0 for none. Requires the process to
	/// have been launched under a capture tool; scripts/windows/capture-gpu-frame.cmd does that.
	/// </summary>
	public static int GpuCaptureFrameIndex { get; set; } =
		ParsePositiveIntEnvironmentVariable("WOLF_GPU_CAPTURE_FRAME", 0);

	/// <summary>
	/// 1-based terrain brush stamp to capture the following frame for, or 0 for none. A stamp submits its
	/// own compute work immediately and returns; the frame that draws the result is the next one, so that
	/// is what gets captured.
	/// </summary>
	public static int GpuCaptureTerrainStampIndex { get; set; } =
		ParsePositiveIntEnvironmentVariable("WOLF_GPU_CAPTURE_TERRAIN_STAMP", 0);

	/// <summary>
	/// Log GPU draw lifecycle events: shared buffers being replaced by capacity growth, and the full GPU
	/// state refreshes that follow. Both are rare but expensive, and a refresh re-adds every draw and
	/// re-encodes every indirect record, so it is worth being able to see them when chasing a stall or a
	/// device removal.
	/// </summary>
	public static bool LogGpuDrawEvents { get; set; } =
		string.Equals(Environment.GetEnvironmentVariable("WOLF_LOG_GPU_DRAW_EVENTS"), "1", StringComparison.Ordinal);

	/// <summary>
	/// Log the CPU-side per-draw data for terrain draws as it is queued for upload, flagging values that
	/// cannot produce sane geometry. This runs before anything is submitted, so it survives a GPU hang
	/// that a frame capture cannot.
	/// </summary>
	public static bool LogTerrainDrawData { get; set; } =
		string.Equals(Environment.GetEnvironmentVariable("WOLF_LOG_TERRAIN_DRAW_DATA"), "1", StringComparison.Ordinal);

	/// <summary>
	/// Number of frames between hardening metric logs.
	/// </summary>
	public static int GpuHardeningLogIntervalFrames { get; } =
		ParsePositiveIntEnvironmentVariable("WOLF_GPU_HARDENING_LOG_INTERVAL", 0);

	/// <summary>
	/// Replace the reconstructed FSR3 output with its diagnostic mosaic. This is intended for
	/// unattended visual captures of motion vectors, locks, depth, and reactive masks.
	/// </summary>
	public static bool Fsr3DebugViewEnabled { get; set; } =
		string.Equals(Environment.GetEnvironmentVariable("WOLF_FSR3_DEBUG_VIEW"), "1", StringComparison.Ordinal);

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
