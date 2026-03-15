using System;
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
	private static readonly Guid FileOpenDialogClsid = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
	private static readonly Guid ShellItemGuid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

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

	[DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

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
