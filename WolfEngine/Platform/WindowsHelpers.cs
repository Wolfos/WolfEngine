using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using WolfEngine.Utility;

namespace WolfEngine.Platform;

[SupportedOSPlatform("windows")]
internal static class WindowsHelpers
{
	private const int OFN_EXPLORER = 0x00080000;
	private const int OFN_FILEMUSTEXIST = 0x00001000;
	private const int OFN_PATHMUSTEXIST = 0x00000800;
	private const int OFN_NOCHANGEDIR = 0x00000008;
	private const uint COINIT_APARTMENTTHREADED = 0x2;
	private const uint COINIT_DISABLE_OLE1DDE = 0x4;
	private const int S_OK = 0;
	private const int S_FALSE = 1;
	private const int ERROR_CANCELLED = unchecked((int)0x800704C7);
	private const int GWL_STYLE = -16;
	private const int GWL_EXSTYLE = -20;
	private const int GWLP_WNDPROC = -4;
	private const uint WM_CLOSE = 0x0010;
	private const uint WM_SYSCOMMAND = 0x0112;
	private const uint WM_NCCALCSIZE = 0x0083;
	private const uint WM_NCHITTEST = 0x0084;
	private const uint WM_GETMINMAXINFO = 0x0024;
	private const uint WM_NCACTIVATE = 0x0086;
	private const nuint SC_MINIMIZE = 0xF020;
	private const nuint SC_MAXIMIZE = 0xF030;
	private const nuint SC_RESTORE = 0xF120;
	private const int HTNOWHERE = 0;
	private const int HTCLIENT = 1;
	private const int HTCAPTION = 2;
	private const int HTMINBUTTON = 8;
	private const int HTMAXBUTTON = 9;
	private const int HTLEFT = 10;
	private const int HTRIGHT = 11;
	private const int HTTOP = 12;
	private const int HTTOPLEFT = 13;
	private const int HTTOPRIGHT = 14;
	private const int HTBOTTOM = 15;
	private const int HTBOTTOMLEFT = 16;
	private const int HTBOTTOMRIGHT = 17;
	private const int SW_HIDE = 0;
	private const int SW_SHOWNORMAL = 1;
	private const int SW_SHOWMINIMIZED = 2;
	private const int SW_SHOWMAXIMIZED = 3;
	private const int SW_SHOWNOACTIVATE = 4;
	private const int SW_SHOW = 5;
	private const int SW_MINIMIZE = 6;
	private const int SW_SHOWMINNOACTIVE = 7;
	private const int SW_SHOWNA = 8;
	private const int SW_RESTORE = 9;
	private const int SW_SHOWDEFAULT = 10;
	private const int SW_FORCEMINIMIZE = 11;
	private const uint SWP_NOMOVE = 0x0002;
	private const uint SWP_NOSIZE = 0x0001;
	private const uint SWP_NOZORDER = 0x0004;
	private const uint SWP_NOOWNERZORDER = 0x0200;
	private const uint SWP_FRAMECHANGED = 0x0020;
	private const int MONITOR_DEFAULTTONEAREST = 2;
	private const int SM_CXSIZEFRAME = 32;
	private const int SM_CYSIZEFRAME = 33;
	private const int SM_CXPADDEDBORDER = 92;
	private const nint WS_CAPTION = 0x00C00000;
	private const nint WS_THICKFRAME = 0x00040000;
	private const nint WS_MINIMIZEBOX = 0x00020000;
	private const nint WS_MAXIMIZEBOX = 0x00010000;
	private const nint WS_SYSMENU = 0x00080000;
	private const nint WS_EX_APPWINDOW = 0x00040000;
	private static readonly Guid FileOpenDialogClsid = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
	private static readonly Guid ShellItemGuid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
	private static readonly ConcurrentDictionary<nint, WindowChromeState> WindowChromeStates = new();
	private static readonly WindowProc CustomWindowProc = WindowProcImpl;

	public static void EnableCustomWindowChrome(nint windowHandle, WindowChromeController controller)
	{
		if (windowHandle == nint.Zero)
		{
			throw new ArgumentException("Window handle cannot be zero.", nameof(windowHandle));
		}

		if (controller is null)
		{
			throw new ArgumentNullException(nameof(controller));
		}

		if (WindowChromeStates.ContainsKey(windowHandle))
		{
			return;
		}

		var style = GetWindowLongPtr(windowHandle, GWL_STYLE);
		style |= WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
		SetWindowLongPtr(windowHandle, GWL_STYLE, style);

		var exStyle = GetWindowLongPtr(windowHandle, GWL_EXSTYLE);
		exStyle |= WS_EX_APPWINDOW;
		SetWindowLongPtr(windowHandle, GWL_EXSTYLE, exStyle);

		var previousProc = SetWindowLongPtr(windowHandle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(CustomWindowProc));
		var state = new WindowChromeState(controller, previousProc);
		WindowChromeStates[windowHandle] = state;

		TryExtendFrameIntoClientArea(windowHandle);
		SetWindowPos(
			windowHandle,
			nint.Zero,
			0,
			0,
			0,
			0,
			SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
	}

	public static void DisableCustomWindowChrome(nint windowHandle)
	{
		if (windowHandle == nint.Zero)
		{
			return;
		}

		if (WindowChromeStates.TryRemove(windowHandle, out var state))
		{
			SetWindowLongPtr(windowHandle, GWLP_WNDPROC, state.PreviousWindowProc);
			SetWindowPos(
				windowHandle,
				nint.Zero,
				0,
				0,
				0,
				0,
				SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
		}
	}

	public static bool GetIsMaximized(nint windowHandle)
	{
		return IsZoomed(windowHandle);
	}

	public static void MinimizeWindow(nint windowHandle)
	{
		PostMessage(windowHandle, WM_SYSCOMMAND, SC_MINIMIZE, nint.Zero);
	}

	public static void ToggleMaximizeWindow(nint windowHandle)
	{
		PostMessage(
			windowHandle,
			WM_SYSCOMMAND,
			IsZoomed(windowHandle) ? SC_RESTORE : SC_MAXIMIZE,
			nint.Zero);
	}

	public static void CloseWindow(nint windowHandle)
	{
		PostMessage(windowHandle, WM_CLOSE, nuint.Zero, nint.Zero);
	}

	public static string? OpenFile(FileDialogOptions options)
	{
		const int maxPathChars = 1024;
		var filter = BuildFilterString(options.AllowedExtensions);
		var fileBuffer = Marshal.AllocHGlobal(maxPathChars * sizeof(char));

		try
		{
			Marshal.WriteInt16(fileBuffer, 0);
			var ofn = new OPENFILENAME
			{
				lStructSize = Marshal.SizeOf<OPENFILENAME>(),
				hwndOwner = IntPtr.Zero,
				lpstrFilter = filter,
				lpstrFile = fileBuffer,
				nMaxFile = maxPathChars,
				lpstrTitle = options.Title,
				lpstrInitialDir = options.InitialDirectory,
				Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
				lpstrDefExt = GetDefaultExtension(options.AllowedExtensions)
			};

			if (GetOpenFileNameW(ref ofn) == false)
			{
				return null;
			}

			var result = Marshal.PtrToStringUni(fileBuffer);
			return string.IsNullOrWhiteSpace(result) ? null : result;
		}
		finally
		{
			Marshal.FreeHGlobal(fileBuffer);
		}
	}

	public static string? SaveFile(FileDialogOptions options)
	{
		const int maxPathChars = 1024;
		var filter = BuildFilterString(options.AllowedExtensions);
		var fileBuffer = Marshal.AllocHGlobal(maxPathChars * sizeof(char));

		try
		{
			Marshal.Copy((options.DefaultFileName ?? string.Empty).ToCharArray(), 0, fileBuffer, options.DefaultFileName?.Length ?? 0);
			Marshal.WriteInt16(fileBuffer, (options.DefaultFileName?.Length ?? 0) * sizeof(char), 0);
			var ofn = new OPENFILENAME
			{
				lStructSize = Marshal.SizeOf<OPENFILENAME>(),
				hwndOwner = IntPtr.Zero,
				lpstrFilter = filter,
				lpstrFile = fileBuffer,
				nMaxFile = maxPathChars,
				lpstrTitle = options.Title,
				lpstrInitialDir = options.InitialDirectory,
				Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
				lpstrDefExt = GetDefaultExtension(options.AllowedExtensions)
			};

			if (GetSaveFileNameW(ref ofn) == false)
			{
				return null;
			}

			var result = Marshal.PtrToStringUni(fileBuffer);
			return string.IsNullOrWhiteSpace(result) ? null : result;
		}
		finally
		{
			Marshal.FreeHGlobal(fileBuffer);
		}
	}

	public static string? OpenFolder(FileDialogOptions options)
	{
		if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
		{
			return OpenFolderCore(options);
		}

		return RunInStaThread(() => OpenFolderCore(options));
	}

	private static string? OpenFolderCore(FileDialogOptions options)
	{
		var coInitResult = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
		var shouldUninitialize = coInitResult is S_OK or S_FALSE;

		try
		{
			return OpenFolderModern(options);
		}
		finally
		{
			if (shouldUninitialize)
			{
				CoUninitialize();
			}
		}
	}

	private static string? OpenFolderModern(FileDialogOptions options)
	{
		IFileOpenDialog? dialog = null;
		IShellItem? initialFolder = null;
		IShellItem? resultItem = null;

		try
		{
			var dialogType = Type.GetTypeFromCLSID(FileOpenDialogClsid, throwOnError: true)
				?? throw new NotSupportedException("FileOpenDialog COM type is unavailable.");
			dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;
			dialog.GetOptions(out var dialogOptions);
			dialog.SetOptions(
				dialogOptions |
				FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS |
				FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM |
				FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST |
				FILEOPENDIALOGOPTIONS.FOS_NOCHANGEDIR);

			if (string.IsNullOrWhiteSpace(options.Title) == false)
			{
				dialog.SetTitle(options.Title);
			}

			initialFolder = CreateShellItemFromPath(options.InitialDirectory);
			if (initialFolder is not null)
			{
				dialog.SetFolder(initialFolder);
				dialog.SetDefaultFolder(initialFolder);
			}

			var showResult = dialog.Show(IntPtr.Zero);
			if (showResult == ERROR_CANCELLED)
			{
				return null;
			}

			if (showResult < 0)
			{
				Marshal.ThrowExceptionForHR(showResult);
			}

			dialog.GetResult(out resultItem);
			resultItem.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
			return string.IsNullOrWhiteSpace(path) ? null : path;
		}
		finally
		{
			if (resultItem is not null)
			{
				Marshal.ReleaseComObject(resultItem);
			}

			if (initialFolder is not null)
			{
				Marshal.ReleaseComObject(initialFolder);
			}

			if (dialog is not null)
			{
				Marshal.ReleaseComObject(dialog);
			}
		}
	}

	private static IShellItem? CreateShellItemFromPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		var shellItemGuid = ShellItemGuid;
		var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemGuid, out IShellItem? shellItem);
		if (hr < 0)
		{
			return null;
		}

		return shellItem;
	}

	private static T RunInStaThread<T>(Func<T> action)
	{
		if (action is null)
		{
			throw new ArgumentNullException(nameof(action));
		}

		T result = default!;
		Exception? exception = null;
		using var completed = new ManualResetEventSlim(false);
		var thread = new Thread(() =>
		{
			try
			{
				result = action();
			}
			catch (Exception ex)
			{
				exception = ex;
			}
			finally
			{
				completed.Set();
			}
		})
		{
			IsBackground = true,
			Name = "WindowsFolderDialogThread"
		};

		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		completed.Wait();
		thread.Join();

		if (exception is not null)
		{
			throw exception;
		}

		return result;
	}

	private static string BuildFilterString(string[]? extensions)
	{
		if (extensions is null || extensions.Length == 0)
		{
			return "All Files\0*.*\0\0";
		}

		var patterns = new List<string>(extensions.Length);
		foreach (var extension in extensions)
		{
			if (string.IsNullOrWhiteSpace(extension))
			{
				continue;
			}

			var trimmed = extension.Trim().TrimStart('.');
			if (trimmed.Length == 0)
			{
				continue;
			}

			patterns.Add("*." + trimmed);
		}

		if (patterns.Count == 0)
		{
			return "All Files\0*.*\0\0";
		}

		var pattern = string.Join(";", patterns);
		return $"Allowed Files ({pattern})\0{pattern}\0All Files\0*.*\0\0";
	}

	private static string? GetDefaultExtension(string[]? extensions)
	{
		if (extensions is null || extensions.Length == 0)
		{
			return null;
		}

		foreach (var extension in extensions)
		{
			if (string.IsNullOrWhiteSpace(extension))
			{
				continue;
			}

			var trimmed = extension.Trim().TrimStart('.');
			if (trimmed.Length > 0)
			{
				return trimmed;
			}
		}

		return null;
	}

	private static nint WindowProcImpl(nint hwnd, uint msg, nuint wParam, nint lParam)
	{
		if (WindowChromeStates.TryGetValue(hwnd, out var state) == false)
		{
			return DefWindowProc(hwnd, msg, wParam, lParam);
		}

		switch (msg)
		{
			case WM_NCCALCSIZE:
				if (wParam != 0)
				{
					return 0;
				}

				break;

			case WM_NCACTIVATE:
				return 1;

			case WM_GETMINMAXINFO:
				AdjustMaximizedWindowBounds(hwnd, lParam);
				break;

			case WM_NCHITTEST:
				var metrics = state.Controller.GetTitleBarMetrics();
				if (IsPointInClientOnlyChromeRegion(hwnd, metrics, lParam))
				{
					return HTCLIENT;
				}

				if (DwmDefWindowProc(hwnd, msg, wParam, lParam, out var dwmResult))
				{
					return dwmResult;
				}

				var hitResult = HitTestWindowChrome(hwnd, metrics, lParam);
				if (hitResult != HTNOWHERE)
				{
					return hitResult;
				}

				break;
		}

		return CallWindowProc(state.PreviousWindowProc, hwnd, msg, wParam, lParam);
	}

	private static bool IsPointInClientOnlyChromeRegion(nint hwnd, WindowTitleBarMetrics metrics, nint lParam)
	{
		GetWindowRect(hwnd, out var windowRect);
		var clientX = GetXLParam(lParam) - windowRect.Left;
		var clientY = GetYLParam(lParam) - windowRect.Top;

		if (metrics.MinimizeButtonRect.Contains(clientX, clientY)
		    || metrics.MaximizeButtonRect.Contains(clientX, clientY)
		    || metrics.CloseButtonRect.Contains(clientX, clientY))
		{
			return true;
		}

		var exclusions = metrics.ExclusionRects;
		for (var i = 0; i < exclusions.Count; i++)
		{
			if (exclusions[i].Contains(clientX, clientY))
			{
				return true;
			}
		}

		return false;
	}

	private static nint HitTestWindowChrome(nint hwnd, WindowTitleBarMetrics metrics, nint lParam)
	{
		var screenX = GetXLParam(lParam);
		var screenY = GetYLParam(lParam);

		GetWindowRect(hwnd, out var windowRect);
		var resizeBorderX = GetResizeBorderThicknessX();
		var resizeBorderY = GetResizeBorderThicknessY();
		var isMaximized = IsZoomed(hwnd);

		if (isMaximized == false)
		{
			var top = screenY < windowRect.Top + resizeBorderY;
			var bottom = screenY >= windowRect.Bottom - resizeBorderY;
			var left = screenX < windowRect.Left + resizeBorderX;
			var right = screenX >= windowRect.Right - resizeBorderX;

			if (top && left)
			{
				return HTTOPLEFT;
			}

			if (top && right)
			{
				return HTTOPRIGHT;
			}

			if (bottom && left)
			{
				return HTBOTTOMLEFT;
			}

			if (bottom && right)
			{
				return HTBOTTOMRIGHT;
			}

			if (left)
			{
				return HTLEFT;
			}

			if (right)
			{
				return HTRIGHT;
			}

			if (top)
			{
				return HTTOP;
			}

			if (bottom)
			{
				return HTBOTTOM;
			}
		}

		var clientX = screenX - windowRect.Left;
		var clientY = screenY - windowRect.Top;

		if (metrics.TitleBarRect.Contains(clientX, clientY))
		{
			return HTCAPTION;
		}

		return HTCLIENT;
	}

	private static void AdjustMaximizedWindowBounds(nint hwnd, nint lParam)
	{
		if (lParam == nint.Zero)
		{
			return;
		}

		var minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
		var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
		if (monitor == nint.Zero)
		{
			return;
		}

		var monitorInfo = new MONITORINFO
		{
			cbSize = Marshal.SizeOf<MONITORINFO>()
		};

		if (GetMonitorInfo(monitor, ref monitorInfo) == false)
		{
			return;
		}

		var workArea = monitorInfo.rcWork;
		var monitorArea = monitorInfo.rcMonitor;
		minMaxInfo.ptMaxPosition.X = workArea.Left - monitorArea.Left;
		minMaxInfo.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
		minMaxInfo.ptMaxSize.X = workArea.Right - workArea.Left;
		minMaxInfo.ptMaxSize.Y = workArea.Bottom - workArea.Top;
		minMaxInfo.ptMaxTrackSize = minMaxInfo.ptMaxSize;

		Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: false);
	}

	private static int GetResizeBorderThicknessX()
	{
		return GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
	}

	private static int GetResizeBorderThicknessY()
	{
		return GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
	}

	private static int GetXLParam(nint lParam)
	{
		return unchecked((short)(long)lParam);
	}

	private static int GetYLParam(nint lParam)
	{
		return unchecked((short)((long)lParam >> 16));
	}

	private static void TryExtendFrameIntoClientArea(nint windowHandle)
	{
		try
		{
			var margins = new MARGINS
			{
				cxLeftWidth = 1,
				cxRightWidth = 1,
				cyTopHeight = 1,
				cyBottomHeight = 1
			};

			DwmExtendFrameIntoClientArea(windowHandle, ref margins);
		}
		catch
		{
			// Border shadow extension is best-effort.
		}
	}

	[DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

	[DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
	private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
	private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

	[DllImport("user32.dll")]
	private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nuint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern nint DefWindowProc(nint hWnd, uint msg, nuint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool IsZoomed(nint hWnd);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int nIndex);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

	[DllImport("dwmapi.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DwmDefWindowProc(nint hwnd, uint msg, nuint wParam, nint lParam, out nint plResult);

	[DllImport("dwmapi.dll")]
	private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHCreateItemFromParsingName(
		[MarshalAs(UnmanagedType.LPWStr)] string path,
		IntPtr pbc,
		ref Guid riid,
		[MarshalAs(UnmanagedType.Interface)] out IShellItem? shellItem);

	[DllImport("ole32.dll")]
	private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

	[DllImport("ole32.dll")]
	private static extern void CoUninitialize();

	private delegate nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam);

	private sealed class WindowChromeState
	{
		public WindowChromeState(WindowChromeController controller, nint previousWindowProc)
		{
			Controller = controller;
			PreviousWindowProc = previousWindowProc;
		}

		public WindowChromeController Controller { get; }
		public nint PreviousWindowProc { get; }
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MINMAXINFO
	{
		public POINT ptReserved;
		public POINT ptMaxSize;
		public POINT ptMaxPosition;
		public POINT ptMinTrackSize;
		public POINT ptMaxTrackSize;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MONITORINFO
	{
		public int cbSize;
		public RECT rcMonitor;
		public RECT rcWork;
		public uint dwFlags;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MARGINS
	{
		public int cxLeftWidth;
		public int cxRightWidth;
		public int cyTopHeight;
		public int cyBottomHeight;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct OPENFILENAME
	{
		public int lStructSize;
		public IntPtr hwndOwner;
		public IntPtr hInstance;
		public string? lpstrFilter;
		public IntPtr lpstrCustomFilter;
		public int nMaxCustFilter;
		public int nFilterIndex;
		public IntPtr lpstrFile;
		public int nMaxFile;
		public IntPtr lpstrFileTitle;
		public int nMaxFileTitle;
		public string? lpstrInitialDir;
		public string? lpstrTitle;
		public int Flags;
		public short nFileOffset;
		public short nFileExtension;
		public string? lpstrDefExt;
		public IntPtr lCustData;
		public IntPtr lpfnHook;
		public string? lpTemplateName;
		public IntPtr pvReserved;
		public int dwReserved;
		public int FlagsEx;
	}

	[Flags]
	private enum FILEOPENDIALOGOPTIONS : uint
	{
		FOS_NOCHANGEDIR = 0x00000008,
		FOS_PICKFOLDERS = 0x00000020,
		FOS_FORCEFILESYSTEM = 0x00000040,
		FOS_PATHMUSTEXIST = 0x00000800
	}

	private enum SIGDN : uint
	{
		SIGDN_FILESYSPATH = 0x80058000
	}

	[ComImport]
	[Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IFileOpenDialog
	{
		[PreserveSig]
		int Show(IntPtr parent);
		void SetFileTypes(uint cFileTypes, IntPtr filterSpec);
		void SetFileTypeIndex(uint iFileType);
		void GetFileTypeIndex(out uint piFileType);
		void Advise(IntPtr pfde, out uint pdwCookie);
		void Unadvise(uint dwCookie);
		void SetOptions(FILEOPENDIALOGOPTIONS fos);
		void GetOptions(out FILEOPENDIALOGOPTIONS pfos);
		void SetDefaultFolder(IShellItem psi);
		void SetFolder(IShellItem psi);
		void GetFolder(out IShellItem ppsi);
		void GetCurrentSelection(out IShellItem ppsi);
		void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
		void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
		void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
		void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
		void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
		void GetResult(out IShellItem ppsi);
		void AddPlace(IShellItem psi, int fdap);
		void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
		void Close(int hr);
		void SetClientGuid(ref Guid guid);
		void ClearClientData();
		void SetFilter(IntPtr pFilter);
		void GetResults(out IntPtr ppenum);
		void GetSelectedItems(out IntPtr ppsai);
	}

	[ComImport]
	[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItem
	{
		void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
		void GetParent(out IShellItem ppsi);
		void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
		void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
		void Compare(IShellItem psi, uint hint, out int piOrder);
	}

}
