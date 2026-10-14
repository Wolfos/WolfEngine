using System.Diagnostics;

namespace WolfEngine;

/// <summary>
/// Compiles Slang-generated Metal Shading Language into a metallib with Apple's <c>metal</c> compiler.
/// </summary>
/// <remarks>
/// Slang's own <c>metallib</c> target runs <c>metal</c> as a child process that .NET does not know about. While any
/// <see cref="Process"/> runs elsewhere in the editor (a game build's <c>dotnet publish</c>, for example), the runtime's
/// child-process reaping can collect that child first, and Slang then returns empty bytecode without an error.
/// Starting <c>metal</c> through <see cref="Process"/> keeps the runtime aware of it.
/// </remarks>
internal static class MetalLibraryCompiler
{
	// The options Slang passes to metal for its own metallib target, so libraries stay byte-identical to the ones it
	// built. The deployment target is the one metal defaults to when run without an SDK; stating it keeps it fixed.
	private static readonly string[] CompilerOptions =
	[
		"-std=metal3.1",
		"-fno-strict-aliasing",
		"-Os",
		"-g0",
		"-mmacosx-version-min=14.0"
	];

	// metal is found once and then run directly. Running it as `xcrun -sdk macosx metal` would set SDKROOT, which
	// raises the deployment target to the SDK's own OS version.
	private static readonly Lazy<string> MetalCompilerPath = new(FindMetalCompiler);

	public static byte[] Compile(ReadOnlySpan<byte> metalSource, string shaderPath)
	{
		if (metalSource.IsEmpty)
		{
			throw new InvalidOperationException($"Slang produced no Metal source for '{shaderPath}'.");
		}

		var workDirectory = Directory.CreateTempSubdirectory("wolf-metal-");
		try
		{
			var sourcePath = Path.Combine(workDirectory.FullName, "shader.metal");
			var libraryPath = Path.Combine(workDirectory.FullName, "shader.metallib");
			File.WriteAllBytes(sourcePath, metalSource.ToArray());

			var startInfo = new ProcessStartInfo(MetalCompilerPath.Value)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			};
			foreach (var option in CompilerOptions)
			{
				startInfo.ArgumentList.Add(option);
			}

			startInfo.ArgumentList.Add("-o");
			startInfo.ArgumentList.Add(libraryPath);
			startInfo.ArgumentList.Add(sourcePath);

			var (exitCode, diagnostics) = Run(startInfo);
			if (exitCode != 0)
			{
				throw new InvalidOperationException(
					$"The Metal compiler failed for '{shaderPath}' with exit code {exitCode}:{Environment.NewLine}{diagnostics}");
			}

			var library = File.Exists(libraryPath) ? File.ReadAllBytes(libraryPath) : [];
			if (library.Length == 0)
			{
				throw new InvalidOperationException(
					$"The Metal compiler reported success for '{shaderPath}' but wrote no library.{Environment.NewLine}{diagnostics}");
			}

			return library;
		}
		finally
		{
			try
			{
				workDirectory.Delete(recursive: true);
			}
			catch (IOException)
			{
				// A leftover temp directory is harmless; a failed compile must still report its own error.
			}
		}
	}

	private static string FindMetalCompiler()
	{
		var startInfo = new ProcessStartInfo("/usr/bin/xcrun")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("--sdk");
		startInfo.ArgumentList.Add("macosx");
		startInfo.ArgumentList.Add("--find");
		startInfo.ArgumentList.Add("metal");

		var (exitCode, output) = Run(startInfo);
		var path = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
		if (exitCode != 0 ||
		    path is null ||
		    File.Exists(path) == false)
		{
			throw new InvalidOperationException(
				$"Could not find the Metal compiler. Install Xcode and its Metal toolchain.{Environment.NewLine}{output}");
		}

		return path;
	}

	private static (int ExitCode, string Output) Run(ProcessStartInfo startInfo)
	{
		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");

		// Both streams are read concurrently so a full pipe cannot stall the child.
		var standardOutput = process.StandardOutput.ReadToEndAsync();
		var standardError = process.StandardError.ReadToEndAsync();
		process.WaitForExit();

		return (process.ExitCode, (standardError.Result + standardOutput.Result).Trim());
	}
}
