using System.Runtime.InteropServices;

namespace WolfEngine.Runtime;

/// <summary>The build target a cooked game must have been built for to run on this device.</summary>
public static class RuntimeTarget
{
	/// <summary>The runtime identifier of this platform, as builds record it, such as <c>osx-arm64</c> or <c>ios-arm64</c>.</summary>
	public static string Current
	{
		get
		{
			var isArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
			if (OperatingSystem.IsIOS())
			{
				return "ios-arm64";
			}

			if (OperatingSystem.IsMacOS())
			{
				return isArm64 ? "osx-arm64" : "osx-x64";
			}

			if (OperatingSystem.IsWindows())
			{
				return isArm64 ? "win-arm64" : "win-x64";
			}

			throw new PlatformNotSupportedException();
		}
	}

	/// <summary>Throws when content built for <paramref name="buildTarget"/> cannot run here.</summary>
	public static void EnsureCanRun(string buildTarget)
	{
		var current = Current;
		if (string.Equals(buildTarget, current, StringComparison.Ordinal))
		{
			return;
		}

		throw new PlatformNotSupportedException($"Build target '{buildTarget}' cannot run on '{current}'.");
	}
}
