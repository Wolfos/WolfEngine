#nullable enable

using System;
using System.Runtime.InteropServices;

namespace WolfEngine.Rendering.Backend.D3D12;

/// <summary>
/// Programmatic GPU capture through <c>IDXGraphicsAnalysis</c>.
/// </summary>
/// <remarks>
/// PIX's own capturer exports only <c>GetIsAttachedToPix</c>, so the begin/end entry points are not
/// callable directly; <c>IDXGraphicsAnalysis</c> is the supported route and is what PIX listens to in
/// <c>pixtool ... programmatic-capture</c> mode. The interface only resolves while a capture tool is
/// attached to the process, which is why this is queried lazily rather than at device creation.
///
/// The capture covers whatever is submitted between Begin and End; PIX names it and writes it out, so
/// the requested output path is only recorded for reporting.
/// </remarks>
internal static unsafe class D3D12ProgrammaticCapture
{
	private static readonly Guid IID_IDXGraphicsAnalysis = new("9f251514-9d4d-4902-9d60-18988ab7d4b5");

	private static void** _instance;
	private static bool _resolved;

	/// <summary>Whether a capture tool is attached and exposing the capture interface.</summary>
	internal static bool IsAvailable => Resolve() is not null;

	internal static bool TryBeginCapture(out string error)
	{
		var analysis = Resolve();
		if (analysis is null)
		{
			error = "No GPU capture tool is attached to this process. Launch through " +
			        "scripts/windows/capture-gpu-frame.cmd so PIX can inject its capturer.";
			return false;
		}

		// IUnknown occupies slots 0-2; BeginCapture is 3 and EndCapture is 4.
		var vtable = (void**)*analysis;
		var beginCapture = (delegate* unmanaged[Stdcall]<void**, int>)vtable[3];
		var result = beginCapture(analysis);
		if (result < 0)
		{
			error = $"IDXGraphicsAnalysis::BeginCapture failed (0x{result:X8}).";
			return false;
		}

		error = string.Empty;
		return true;
	}

	internal static bool TryEndCapture(out string error)
	{
		var analysis = Resolve();
		if (analysis is null)
		{
			error = "No GPU capture tool is attached to this process.";
			return false;
		}

		var vtable = (void**)*analysis;
		var endCapture = (delegate* unmanaged[Stdcall]<void**, int>)vtable[4];
		var result = endCapture(analysis);
		if (result < 0)
		{
			error = $"IDXGraphicsAnalysis::EndCapture failed (0x{result:X8}).";
			return false;
		}

		error = string.Empty;
		return true;
	}

	private static void** Resolve()
	{
		if (_resolved)
		{
			return _instance;
		}

		_resolved = true;
		try
		{
			var iid = IID_IDXGraphicsAnalysis;
			void* instance = null;
			// A missing interface is the normal case when running without a capture tool, so a failed
			// query is not an error worth surfacing.
			if (DXGIGetDebugInterface1(0, &iid, &instance) >= 0)
			{
				_instance = (void**)instance;
			}
		}
		catch (DllNotFoundException)
		{
			_instance = null;
		}
		catch (EntryPointNotFoundException)
		{
			_instance = null;
		}

		return _instance;
	}

	[DllImport("dxgi", ExactSpelling = true)]
	private static extern int DXGIGetDebugInterface1(uint flags, Guid* iid, void** debug);
}
