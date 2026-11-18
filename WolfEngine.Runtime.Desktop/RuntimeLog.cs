using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WolfEngine.Runtime.Desktop;

/// <summary>
/// Keeps a built game's output. The desktop runtime is a WinExe, so a game started from Explorer or Finder has no console
/// and nothing would see its output. Everything written to <see cref="Console"/> is also written to a log file, with the
/// previous run's log kept beside it, and a fatal error is shown in a dialog that points at that file.
/// </summary>
public sealed class RuntimeLog : IDisposable
{
	private readonly StreamWriter? _file;
	private readonly TextWriter _originalOut;
	private readonly TextWriter _originalError;

	private readonly bool _showDialogs;
	private int _dialogShown;

	private RuntimeLog(
		string gameName,
		string? path,
		StreamWriter? file,
		TextWriter originalOut,
		TextWriter originalError,
		bool showDialogs)
	{
		_showDialogs = showDialogs;
		GameName = gameName;
		Path = path;
		_file = file;
		_originalOut = originalOut;
		_originalError = originalError;
	}

	public string GameName { get; }

	/// <summary>The log file, or null when none could be created. Output then only goes to the console.</summary>
	public string? Path { get; }

	/// <summary>
	/// Starts logging. Call it before anything else writes to the console, so the log covers the whole run.
	/// </summary>
	/// <param name="openConsole">
	/// On Windows, attaches to the console of the shell that started the game, or opens one, so its output can be read
	/// live. Output a parent process redirects is left as it is.
	/// </param>
	/// <param name="showDialogs">
	/// Whether fatal errors are shown to the player. Automated runs leave them out, since nobody would dismiss them.
	/// </param>
	public static RuntimeLog Start(string[] args, bool openConsole, bool showDialogs)
	{
		var gameName = GetGameName();
		if (openConsole)
		{
			ConsoleAttacher.Attach();
		}

		var originalOut = Console.Out;
		var originalError = Console.Error;
		var (path, file) = OpenLogFile(gameName);
		var log = new RuntimeLog(gameName, path, file, originalOut, originalError, showDialogs);
		if (file is not null)
		{
			var sync = new object();
			var clock = Stopwatch.StartNew();
			Console.SetOut(new TeeWriter(originalOut, file, sync, clock, linePrefix: ""));
			Console.SetError(new TeeWriter(originalError, file, sync, clock, linePrefix: "error: "));
		}

		Console.WriteLine($"{gameName} started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
		Console.WriteLine($"  version: {Assembly.GetEntryAssembly()?.GetName().Version}");
		Console.WriteLine($"  os: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
		Console.WriteLine($"  runtime: {RuntimeInformation.FrameworkDescription}");
		Console.WriteLine($"  directory: {AppContext.BaseDirectory}");
		Console.WriteLine($"  arguments: {(args.Length == 0 ? "(none)" : string.Join(' ', args))}");
		Console.WriteLine($"  log: {path ?? "(could not create a log file)"}");

		// Exceptions on threads the runtime does not guard end the process before Main's handler sees them.
		AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
		{
			Console.Error.WriteLine($"runtime crashed: {eventArgs.ExceptionObject}");
			log.Flush();
			if (eventArgs.ExceptionObject is Exception exception)
			{
				log.ShowFatalError(exception);
			}
		};
		TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
			Console.Error.WriteLine($"unobserved task exception: {eventArgs.Exception}");

		return log;
	}

	/// <summary>Tells the player the game stopped and where its log is. Blocks until they dismiss it.</summary>
	public void ShowFatalError(Exception exception)
	{
		if (_showDialogs == false)
		{
			return;
		}

		var message = new StringBuilder()
			.AppendLine($"{GameName} stopped because of an error:")
			.AppendLine()
			.AppendLine(exception.GetBaseException().Message);
		if (Path is not null)
		{
			message.AppendLine().AppendLine("Details were written to:").Append(Path);
		}

		if (Interlocked.Exchange(ref _dialogShown, 1) != 0)
		{
			return;
		}

		// A dialog runs a modal loop on the thread that shows it. On the main thread that loop would keep pumping the
		// game window, whose next frame can fail again and open another dialog, so it gets a thread of its own.
		var dialogThread = new Thread(() =>
		{
			try
			{
				FatalErrorDialog.Show(GameName, message.ToString());
			}
			catch (Exception dialogException)
			{
				Console.Error.WriteLine($"could not show the error dialog: {dialogException.Message}");
			}
		})
		{
			Name = "FatalErrorDialog"
		};
		if (OperatingSystem.IsWindows())
		{
			dialogThread.SetApartmentState(ApartmentState.STA);
		}

		dialogThread.Start();
		dialogThread.Join();
	}

	public void Flush()
	{
		try
		{
			Console.Out.Flush();
			Console.Error.Flush();
		}
		catch
		{
			// Flushing is best effort while the process goes down.
		}
	}

	public void Dispose()
	{
		Flush();
		Console.SetOut(_originalOut);
		Console.SetError(_originalError);
		_file?.Dispose();
	}

	/// <summary>The executable carries the game's name, which the build gives it.</summary>
	private static string GetGameName()
	{
		var name = Environment.ProcessPath is { } processPath
			? System.IO.Path.GetFileNameWithoutExtension(processPath)
			: Assembly.GetEntryAssembly()?.GetName().Name;
		if (string.IsNullOrWhiteSpace(name) || name is "dotnet")
		{
			name = Assembly.GetEntryAssembly()?.GetName().Name ?? "WolfEngineGame";
		}

		foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
		{
			name = name.Replace(invalid, '_');
		}

		return name;
	}

	/// <summary>
	/// %LOCALAPPDATA%\&lt;Game&gt;\log.txt on Windows and ~/Library/Logs/&lt;Game&gt;/log.txt on macOS, falling back to
	/// the temporary folder. The previous run's log is kept as log-previous.txt, since the run that failed is usually
	/// followed by one the player starts to see if it happens again.
	/// </summary>
	private static (string? Path, StreamWriter? File) OpenLogFile(string gameName)
	{
		foreach (var root in GetLogRoots())
		{
			try
			{
				var directory = System.IO.Path.Combine(root, gameName);
				Directory.CreateDirectory(directory);
				var path = System.IO.Path.Combine(directory, "log.txt");
				if (File.Exists(path))
				{
					File.Copy(path, System.IO.Path.Combine(directory, "log-previous.txt"), overwrite: true);
				}

				// Readable while the game runs, and flushed per write so a native crash keeps what came before it.
				var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
				return (path, new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Try the next location. Without any, the game still runs, and its output only goes to the console.
			}
		}

		return (null, null);
	}

	private static IEnumerable<string> GetLogRoots()
	{
		if (OperatingSystem.IsMacOS())
		{
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (string.IsNullOrEmpty(home) == false)
			{
				yield return System.IO.Path.Combine(home, "Library", "Logs");
			}
		}
		else
		{
			var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (string.IsNullOrEmpty(localData) == false)
			{
				yield return localData;
			}
		}

		yield return System.IO.Path.GetTempPath();
	}

	/// <summary>
	/// Writes to the original console writer and to the log file, which gets each line stamped with the seconds since
	/// the game started. Standard output and standard error share the file and its lock.
	/// </summary>
	private sealed class TeeWriter(TextWriter console, StreamWriter file, object sync, Stopwatch clock, string linePrefix)
		: TextWriter
	{
		private bool _atLineStart = true;

		public override Encoding Encoding => file.Encoding;

		public override void Write(char value)
		{
			lock (sync)
			{
				WriteConsole(value);
				WriteFile(value);
			}
		}

		public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

		public override void Write(string? value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return;
			}

			lock (sync)
			{
				try
				{
					console.Write(value);
				}
				catch
				{
					// A console that went away must not stop the log.
				}

				foreach (var character in value)
				{
					WriteFile(character);
				}
			}
		}

		public override void Flush()
		{
			lock (sync)
			{
				try
				{
					console.Flush();
				}
				catch
				{
					// See Write.
				}

				file.Flush();
			}
		}

		private void WriteConsole(char value)
		{
			try
			{
				console.Write(value);
			}
			catch
			{
				// See Write.
			}
		}

		private void WriteFile(char value)
		{
			if (value == '\r')
			{
				return;
			}

			if (_atLineStart)
			{
				file.Write($"[{clock.Elapsed.TotalSeconds,9:0.000}] {linePrefix}");
				_atLineStart = false;
			}

			if (value == '\n')
			{
				file.WriteLine();
				_atLineStart = true;
			}
			else
			{
				file.Write(value);
			}
		}
	}

	private static class ConsoleAttacher
	{
		private const int AttachParentProcess = -1;
		private const uint StdOutputHandle = unchecked((uint)-11);
		private const uint FileTypeDisk = 1;
		private const uint FileTypePipe = 3;

		public static void Attach()
		{
			if (OperatingSystem.IsWindows() == false)
			{
				// Started from a terminal, a macOS game writes to it already.
				return;
			}

			// A parent that redirected the output reads it already, and attaching would take it away.
			var fileType = GetFileType(GetStdHandle(StdOutputHandle));
			if (fileType is FileTypeDisk or FileTypePipe)
			{
				return;
			}

			if (AttachConsole(AttachParentProcess) == false && AllocConsole() == false)
			{
				return;
			}

			Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true });
			Console.SetError(new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding) { AutoFlush = true });
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool AttachConsole(int processId);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool AllocConsole();

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetStdHandle(uint handle);

		[DllImport("kernel32.dll")]
		private static extern uint GetFileType(IntPtr handle);
	}

	private static class FatalErrorDialog
	{
		private const uint MbOk = 0x0;
		private const uint MbIconError = 0x10;
		private const uint MbSetForeground = 0x10000;
		private const uint MbTopmost = 0x40000;

		public static void Show(string title, string message)
		{
			if (OperatingSystem.IsWindows())
			{
				MessageBoxW(IntPtr.Zero, message, title, MbOk | MbIconError | MbSetForeground | MbTopmost);
			}
			else if (OperatingSystem.IsMacOS())
			{
				// Arguments reach the script as argv, so the message needs no quoting.
				var info = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
				foreach (var argument in new[]
				         {
					         "-e", "on run argv",
					         "-e", "display alert (item 1 of argv) message (item 2 of argv) as critical",
					         "-e", "end run",
					         title, message
				         })
				{
					info.ArgumentList.Add(argument);
				}

				using var process = Process.Start(info);
				process?.WaitForExit();
			}
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
	}
}
