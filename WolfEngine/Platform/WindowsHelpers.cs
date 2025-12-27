using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using WolfEngine.Utility;

namespace WolfEngine.Platform;

[SupportedOSPlatform("windows")]
internal static class WindowsHelpers
{
	private const int OFN_EXPLORER = 0x00080000;
	private const int OFN_FILEMUSTEXIST = 0x00001000;
	private const int OFN_PATHMUSTEXIST = 0x00000800;
	private const int OFN_NOCHANGEDIR = 0x00000008;

	public static string? OpenFile(FileDialogOptions options)
	{
		var buffer = new StringBuilder(1024);
		var filter = BuildFilterString(options.AllowedExtensions);
		var ofn = new OPENFILENAME
		{
			lStructSize = Marshal.SizeOf<OPENFILENAME>(),
			hwndOwner = IntPtr.Zero,
			lpstrFilter = filter,
			lpstrFile = buffer,
			nMaxFile = buffer.Capacity,
			lpstrTitle = options.Title,
			lpstrInitialDir = options.InitialDirectory,
			Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
			lpstrDefExt = GetDefaultExtension(options.AllowedExtensions)
		};

		if (GetOpenFileNameW(ref ofn) == false)
		{
			return null;
		}

		var result = buffer.ToString();
		return string.IsNullOrWhiteSpace(result) ? null : result;
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

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct OPENFILENAME
	{
		public int lStructSize;
		public IntPtr hwndOwner;
		public IntPtr hInstance;
		public string? lpstrFilter;
		public StringBuilder? lpstrCustomFilter;
		public int nMaxCustFilter;
		public int nFilterIndex;
		public StringBuilder lpstrFile;
		public int nMaxFile;
		public StringBuilder? lpstrFileTitle;
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
}
