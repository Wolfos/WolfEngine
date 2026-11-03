using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.SDL;

namespace WolfEngine.Platform;

/// <summary>
/// Connects Dear ImGui's copy and paste to the system clipboard through SDL. Without it ImGui keeps an internal
/// buffer on macOS, so text copied in the editor never leaves it and text copied elsewhere cannot be pasted in.
/// Windows needs none of this: ImGui uses the Win32 clipboard itself.
/// </summary>
internal static unsafe class SdlImGuiClipboard
{
	private static Sdl? _sdl;

	// SDL allocates the text it returns; ImGui reads it until the next call, when it is freed.
	private static byte* _lastClipboardText;

	public static void Install(ImGuiPlatformIOPtr platformIo)
	{
		platformIo.Platform_GetClipboardTextFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*>)&GetClipboardText;
		platformIo.Platform_SetClipboardTextFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)&SetClipboardText;
	}

	// Called from native code: an exception escaping it would end the process, so failures leave the clipboard
	// empty or unchanged instead.
	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static byte* GetClipboardText(IntPtr context)
	{
		try
		{
			// SDL is loaded on first use, by when the window has started it, so creating a UI needs no SDL.
			var sdl = _sdl ??= Sdl.GetApi();
			if (_lastClipboardText is not null)
			{
				sdl.Free(_lastClipboardText);
				_lastClipboardText = null;
			}

			_lastClipboardText = sdl.GetClipboardText();

			return _lastClipboardText;
		}
		catch (Exception exception)
		{
			Console.WriteLine($"Reading the clipboard failed: {exception.Message}");

			return null;
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void SetClipboardText(IntPtr context, byte* text)
	{
		try
		{
			if (text is null) return;

			_sdl ??= Sdl.GetApi();
			_sdl.SetClipboardText(text);
		}
		catch (Exception exception)
		{
			Console.WriteLine($"Writing the clipboard failed: {exception.Message}");
		}
	}
}
