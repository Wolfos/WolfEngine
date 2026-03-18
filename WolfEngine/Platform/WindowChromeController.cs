using System.Runtime.Versioning;

namespace WolfEngine.Platform;

public interface IWindowChromeController
{
	bool IsCustomChromeSupported { get; }
	bool IsMaximized { get; }
	void Minimize();
	void ToggleMaximize();
	void Close();
	void SetTitleBarMetrics(WindowTitleBarMetrics metrics);
}

public readonly record struct WindowChromeRect(float Left, float Top, float Right, float Bottom)
{
	public static WindowChromeRect Empty => default;

	public bool IsEmpty => Right <= Left || Bottom <= Top;

	public bool Contains(int x, int y)
	{
		return x >= Left && x < Right && y >= Top && y < Bottom;
	}
}

public sealed class WindowTitleBarMetrics
{
	public static readonly WindowTitleBarMetrics Empty = new(
		WindowChromeRect.Empty,
		WindowChromeRect.Empty,
		WindowChromeRect.Empty,
		WindowChromeRect.Empty,
		Array.Empty<WindowChromeRect>());

	public WindowTitleBarMetrics(
		WindowChromeRect titleBarRect,
		WindowChromeRect minimizeButtonRect,
		WindowChromeRect maximizeButtonRect,
		WindowChromeRect closeButtonRect,
		IReadOnlyList<WindowChromeRect> exclusionRects)
	{
		TitleBarRect = titleBarRect;
		MinimizeButtonRect = minimizeButtonRect;
		MaximizeButtonRect = maximizeButtonRect;
		CloseButtonRect = closeButtonRect;
		ExclusionRects = exclusionRects ?? Array.Empty<WindowChromeRect>();
	}

	public WindowChromeRect TitleBarRect { get; }
	public WindowChromeRect MinimizeButtonRect { get; }
	public WindowChromeRect MaximizeButtonRect { get; }
	public WindowChromeRect CloseButtonRect { get; }
	public IReadOnlyList<WindowChromeRect> ExclusionRects { get; }
}

[SupportedOSPlatform("windows")]
public sealed class WindowChromeController : IWindowChromeController
{
	private nint _windowHandle;
	private WindowTitleBarMetrics _metrics = WindowTitleBarMetrics.Empty;

	public bool IsCustomChromeSupported => OperatingSystem.IsWindows() && _windowHandle != nint.Zero;

	public bool IsMaximized
	{
		get
		{
			if (OperatingSystem.IsWindows() == false || _windowHandle == nint.Zero)
			{
				return false;
			}

			return WindowsHelpers.GetIsMaximized(_windowHandle);
		}
	}

	public void AttachToWindow(nint windowHandle)
	{
		if (OperatingSystem.IsWindows() == false || windowHandle == nint.Zero)
		{
			return;
		}

		_windowHandle = windowHandle;
		WindowsHelpers.EnableCustomWindowChrome(windowHandle, this);
	}

	public void DetachWindow()
	{
		if (OperatingSystem.IsWindows() == false || _windowHandle == nint.Zero)
		{
			return;
		}

		WindowsHelpers.DisableCustomWindowChrome(_windowHandle);
		_windowHandle = nint.Zero;
		_metrics = WindowTitleBarMetrics.Empty;
	}

	public void Minimize()
	{
		if (OperatingSystem.IsWindows() == false || _windowHandle == nint.Zero)
		{
			return;
		}

		WindowsHelpers.MinimizeWindow(_windowHandle);
	}

	public void ToggleMaximize()
	{
		if (OperatingSystem.IsWindows() == false || _windowHandle == nint.Zero)
		{
			return;
		}

		WindowsHelpers.ToggleMaximizeWindow(_windowHandle);
	}

	public void Close()
	{
		if (OperatingSystem.IsWindows() == false || _windowHandle == nint.Zero)
		{
			return;
		}

		WindowsHelpers.CloseWindow(_windowHandle);
	}

	public void SetTitleBarMetrics(WindowTitleBarMetrics metrics)
	{
		_metrics = metrics ?? WindowTitleBarMetrics.Empty;
	}

	internal WindowTitleBarMetrics GetTitleBarMetrics()
	{
		return _metrics;
	}
}
