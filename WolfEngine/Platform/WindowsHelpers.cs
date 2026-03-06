using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WolfEngine.Utility;

namespace WolfEngine.Platform;

[SupportedOSPlatform("windows")]
internal static class WindowsHelpers
{
	private const int OFN_EXPLORER = 0x00080000;
	private const int OFN_FILEMUSTEXIST = 0x00001000;
	private const int OFN_PATHMUSTEXIST = 0x00000800;
	private const int OFN_NOCHANGEDIR = 0x00000008;
	private const uint BIF_RETURNONLYFSDIRS = 0x00000001;
	private const uint BIF_NEWDIALOGSTYLE = 0x00000040;
	private const uint BIF_EDITBOX = 0x00000010;
	private const int BFFM_INITIALIZED = 1;
	private const int BFFM_SETSELECTIONW = 0x0467;

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

	public static string? OpenFolder(FileDialogOptions options)
	{
		const int maxPathChars = 1024;
		var displayNameBuffer = Marshal.AllocHGlobal(maxPathChars * sizeof(char));
		var pathBuffer = Marshal.AllocHGlobal(maxPathChars * sizeof(char));
		IntPtr initialDirectoryPtr = IntPtr.Zero;
		BrowseCallbackProc? callback = null;

		try
		{
			Marshal.WriteInt16(displayNameBuffer, 0);
			Marshal.WriteInt16(pathBuffer, 0);

			if (string.IsNullOrWhiteSpace(options.InitialDirectory) == false)
			{
				initialDirectoryPtr = Marshal.StringToHGlobalUni(options.InitialDirectory);
				callback = static (hwnd, message, _, lpData) =>
				{
					if (message == BFFM_INITIALIZED && lpData != IntPtr.Zero)
					{
						SendMessageW(hwnd, BFFM_SETSELECTIONW, new IntPtr(1), lpData);
					}

					return 0;
				};
			}

			var browseInfo = new BROWSEINFOW
			{
				hwndOwner = IntPtr.Zero,
				pszDisplayName = displayNameBuffer,
				lpszTitle = options.Title,
				ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX,
				lpfn = callback,
				lParam = initialDirectoryPtr
			};

			var itemIdList = SHBrowseForFolderW(ref browseInfo);
			if (itemIdList == IntPtr.Zero)
			{
				return null;
			}

			try
			{
				if (SHGetPathFromIDListW(itemIdList, pathBuffer) == false)
				{
					return null;
				}

				var result = Marshal.PtrToStringUni(pathBuffer);
				return string.IsNullOrWhiteSpace(result) ? null : result;
			}
			finally
			{
				CoTaskMemFree(itemIdList);
			}
		}
		finally
		{
			if (initialDirectoryPtr != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(initialDirectoryPtr);
			}

			Marshal.FreeHGlobal(displayNameBuffer);
			Marshal.FreeHGlobal(pathBuffer);
			GC.KeepAlive(callback);
		}
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

	[DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW browseInfo);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SHGetPathFromIDListW(IntPtr pidl, IntPtr pszPath);

	[DllImport("ole32.dll")]
	private static extern void CoTaskMemFree(IntPtr ptr);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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

	private delegate int BrowseCallbackProc(IntPtr hwnd, int message, IntPtr lParam, IntPtr lpData);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct BROWSEINFOW
	{
		public IntPtr hwndOwner;
		public IntPtr pidlRoot;
		public IntPtr pszDisplayName;
		public string? lpszTitle;
		public uint ulFlags;
		public BrowseCallbackProc? lpfn;
		public IntPtr lParam;
		public int iImage;
	}
}
