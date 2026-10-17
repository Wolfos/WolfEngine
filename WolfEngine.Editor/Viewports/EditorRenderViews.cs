using WolfEngine.Rendering;

namespace WolfEngine.Editor;

/// <summary>
/// A render view the editor publishes alongside the scene view — a preview, a prefab document's viewport.
/// </summary>
public interface IEditorRenderViewSource
{
	RenderViewId View { get; }

	/// <summary>
	/// Brings the view's world up to date for this frame and describes what to render. Called once per frame on the
	/// editor thread, just before the snapshot is published. Return false to skip the view this frame.
	/// </summary>
	bool PrepareSubmission(float deltaTime, out RenderViewSubmission submission);
}

/// <summary>
/// The secondary views the editor renders each frame. The scene view is published by the editor itself; every
/// other view registers here while it exists.
/// </summary>
public sealed class EditorRenderViews
{
	private readonly object _sync = new();
	private readonly List<IEditorRenderViewSource> _sources = new();

	public void Register(IEditorRenderViewSource source)
	{
		ArgumentNullException.ThrowIfNull(source);
		lock (_sync)
		{
			if (_sources.Contains(source) == false)
			{
				_sources.Add(source);
			}
		}
	}

	public void Unregister(IEditorRenderViewSource source)
	{
		lock (_sync)
		{
			_sources.Remove(source);
		}
	}

	/// <summary>Adds a submission for every registered view that has one this frame.</summary>
	public void AppendSubmissions(float deltaTime, List<RenderViewSubmission> destination)
	{
		lock (_sync)
		{
			for (var i = 0; i < _sources.Count; i++)
			{
				if (_sources[i].PrepareSubmission(deltaTime, out var submission))
				{
					destination.Add(submission);
				}
			}
		}
	}
}
