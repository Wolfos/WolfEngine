using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WolfEngine.Rendering.Backend.Metal;

/// <summary>
/// A diagnostic record of every live Metal resource the backend created, with the size Metal actually allocated,
/// its storage mode and the engine code that asked for it. Off unless <c>WOLF_GPU_MEMORY_CENSUS=1</c> is set before
/// launch, because finding the owner walks the stack on every allocation.
/// </summary>
internal static class GpuMemoryCensus
{
	public static readonly bool Enabled = Environment.GetEnvironmentVariable("WOLF_GPU_MEMORY_CENSUS") == "1";

	private static readonly ConcurrentDictionary<(object Wrapper, string Part), Entry> LiveResources = new();
	private static readonly IntPtr AllocatedSizeSelector = sel_registerName("allocatedSize");
	private static readonly IntPtr StorageModeSelector = sel_registerName("storageMode");
	private static readonly IntPtr CurrentAllocatedSizeSelector = sel_registerName("currentAllocatedSize");

	private readonly record struct Entry(string Kind, string Owner, string Storage, ulong Bytes);

	/// <summary>Records one native resource owned by <paramref name="wrapper"/>. Untracked by <see cref="Untrack"/>.</summary>
	public static void Track(object wrapper, string part, IntPtr resource, string? name)
	{
		if (Enabled == false || resource == IntPtr.Zero)
		{
			return;
		}

		var bytes = SendULong(resource, AllocatedSizeSelector);
		var storage = SendULong(resource, StorageModeSelector) switch
		{
			0 => "Shared",
			1 => "Managed",
			2 => "Private",
			3 => "Memoryless",
			_ => "Other"
		};
		LiveResources[(wrapper, part)] = new Entry(part, name ?? FindOwner(), storage, bytes);
	}

	/// <summary>Forgets every native resource <paramref name="wrapper"/> owned.</summary>
	public static void Untrack(object wrapper)
	{
		if (Enabled == false)
		{
			return;
		}

		foreach (var key in LiveResources.Keys)
		{
			if (ReferenceEquals(key.Wrapper, wrapper))
			{
				LiveResources.TryRemove(key, out _);
			}
		}
	}

	/// <summary>Live resources grouped by kind and owner, largest first, against the device's own total.</summary>
	public static string FormatReport(IntPtr device)
	{
		var entries = LiveResources.Values.ToArray();
		var tracked = entries.Aggregate(0UL, (sum, entry) => sum + entry.Bytes);
		var deviceTotal = device == IntPtr.Zero ? 0UL : SendULong(device, CurrentAllocatedSizeSelector);
		var report = new StringBuilder();
		report.AppendLine($"GPU memory: device reports {Megabytes(deviceTotal)}, census tracks {Megabytes(tracked)} in {entries.Length} resources.");

		report.AppendLine("By kind and storage:");
		foreach (var group in entries
			         .GroupBy(entry => (entry.Kind, entry.Storage))
			         .OrderByDescending(group => group.Sum(entry => (double)entry.Bytes)))
		{
			report.AppendLine($"  {Megabytes(Sum(group)),10}  {group.Count(),6}x  {group.Key.Kind} ({group.Key.Storage})");
		}

		report.AppendLine("By owner (top 40):");
		foreach (var group in entries
			         .GroupBy(entry => (entry.Kind, entry.Owner))
			         .OrderByDescending(group => group.Sum(entry => (double)entry.Bytes))
			         .Take(40))
		{
			report.AppendLine($"  {Megabytes(Sum(group)),10}  {group.Count(),6}x  {group.Key.Kind}  {group.Key.Owner}");
		}

		return report.ToString();
	}

	private static ulong Sum(IEnumerable<Entry> entries)
	{
		return entries.Aggregate(0UL, (sum, entry) => sum + entry.Bytes);
	}

	private static string Megabytes(ulong bytes)
	{
		return $"{bytes / (1024.0 * 1024.0):F1} MB";
	}

	/// <summary>The first engine method on the stack outside the Metal backend and the render graph's allocators.</summary>
	private static string FindOwner()
	{
		var frames = new StackTrace(2, false).GetFrames();
		foreach (var frame in frames)
		{
			var method = frame.GetMethod();
			var type = method?.DeclaringType;
			var ns = type?.Namespace ?? string.Empty;
			if (type is null || ns.StartsWith("WolfEngine", StringComparison.Ordinal) == false)
			{
				continue;
			}

			if (ns.StartsWith("WolfEngine.Rendering.Backend", StringComparison.Ordinal) ||
			    type.Name.Contains("ResourceRegistry", StringComparison.Ordinal) ||
			    type.Name.Contains("TexturePool", StringComparison.Ordinal))
			{
				continue;
			}

			var typeName = type.DeclaringType is null ? type.Name : $"{type.DeclaringType.Name}.{type.Name}";
			return $"{typeName}.{method!.Name}";
		}

		return "(unknown)";
	}

	[DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
	private static extern ulong SendULong(IntPtr receiver, IntPtr selector);

	[DllImport("/usr/lib/libobjc.A.dylib")]
	private static extern IntPtr sel_registerName(string name);
}
