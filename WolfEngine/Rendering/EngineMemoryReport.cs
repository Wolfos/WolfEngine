using WolfEngine.Rendering.Backend.Metal;

namespace WolfEngine.Rendering;

/// <summary>
/// A diagnostic summary of where the engine's memory goes: GPU resources by kind and owner, and the managed heap.
/// The GPU part needs <c>WOLF_GPU_MEMORY_CENSUS=1</c> set before launch.
/// </summary>
public static class EngineMemoryReport
{
	public static string Format(IRenderer renderer)
	{
		ArgumentNullException.ThrowIfNull(renderer);

		// A full, compacting collection first, so the figures are what is actually retained rather than what the last
		// young-generation collection happened to leave in the large object heap.
		System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
		GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
		var gc = GC.GetGCMemoryInfo(GCKind.FullBlocking);
		var managed =
			$"Managed heap: {Megabytes(gc.HeapSizeBytes)} live after a full GC, {Megabytes(gc.TotalCommittedBytes)} committed, " +
			$"{Megabytes(gc.GenerationInfo[3].SizeAfterBytes)} of it large objects.";
		if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
		{
			if (renderer.GetGfxDevice() is MetalDevice metalDevice)
			{
				return managed + Environment.NewLine + metalDevice.FormatMemoryReport();
			}
		}

		return managed + Environment.NewLine + "GPU memory: no census for this backend.";
	}

	private static string Megabytes(long bytes)
	{
		return $"{bytes / (1024.0 * 1024.0):F1} MB";
	}
}
