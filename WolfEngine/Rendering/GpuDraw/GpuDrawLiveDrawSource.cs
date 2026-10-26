using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

/// <summary>
/// The live draws of every view's draw database, as the shared draw update last saw them. Re-encoding from one
/// view's database alone would wipe the draws of every other view from a shared slot.
/// </summary>
internal sealed class GpuDrawLiveDrawSource : ILiveDrawSource
{
	private readonly IRenderer _renderer;
	private readonly List<GpuDrawSource> _sources = new();
	private readonly List<GpuDrawEntry> _entryScratch = new();
	private bool _loggedUnsupportedDrawKind;

	public GpuDrawLiveDrawSource(IRenderer renderer)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
	}

	/// <summary>Replaces the databases whose draws are in the shared tables. Called by each shared draw update.</summary>
	public void SetSources(ReadOnlySpan<GpuDrawSource> sources)
	{
		_sources.Clear();
		for (var i = 0; i < sources.Length; i++)
		{
			_sources.Add(sources[i]);
		}
	}

	/// <remarks>
	/// Draw generations come from the handle registry every database shares, so any one database answers for all
	/// of them.
	/// </remarks>
	public bool IsCurrent(in GpuDrawHandle drawHandle) =>
		_sources.Count > 0 && _sources[0].Database.IsCurrentDrawHandle(drawHandle);

	public void CollectResidentDraws(List<LiveDraw> destination)
	{
		ArgumentNullException.ThrowIfNull(destination);
		destination.Clear();
		for (var sourceIndex = 0; sourceIndex < _sources.Count; sourceIndex++)
		{
			_sources[sourceIndex].Database.CollectDrawEntries(_entryScratch);
			for (var i = 0; i < _entryScratch.Count; i++)
			{
				var entry = _entryScratch[i];
				if (entry.DrawIndex <= 0 || entry.DrawIndex >= GpuDrawResources.MaxDrawCount)
				{
					continue;
				}

				if (GpuDrawClassification.SupportsMeshBackedGeometry(entry.DrawKind) == false)
				{
					LogUnsupportedDrawKindOnce(entry.DrawKind);
					continue;
				}

				_renderer.EnsureMeshResources(entry.Mesh);
				var executionKey = new GpuDrawExecutionKey(
					entry.DrawKind,
					GpuDrawClassification.ResolveBucketId(entry.DrawKind, entry.Material),
					GpuDrawClassification.ResolveSidedness(entry.DrawKind, entry.Material),
					entry.DrawKind == GpuDrawKind.Mesh && entry.Material.AlphaMode != AlphaMode.AlphaBlend && entry.Mesh.IsSkinned,
					GpuDrawClassification.ReversesWinding(entry.World));
				destination.Add(new LiveDraw((uint)entry.DrawIndex, executionKey, entry.Mesh));
			}
		}
	}

	public void EnsureResident(Mesh mesh)
	{
		ArgumentNullException.ThrowIfNull(mesh);
		_renderer.EnsureMeshResources(mesh);
	}

	private void LogUnsupportedDrawKindOnce(GpuDrawKind drawKind)
	{
		if (_loggedUnsupportedDrawKind)
		{
			return;
		}

		_loggedUnsupportedDrawKind = true;
		Console.WriteLine(
			$"GpuDraw: shared draw kind '{drawKind}' is not supported by the current shared draw path; draw will be skipped.");
	}
}
