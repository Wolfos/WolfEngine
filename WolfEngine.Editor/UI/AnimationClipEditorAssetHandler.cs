using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

/// <summary>Creates animation clips and opens them, authored or imported, in the Animate workspace.</summary>
public sealed class AnimationClipEditorAssetHandler : IEditorAssetHandler
{
	private readonly AnimationClipEditorSession _session;
	private readonly IEditorProjectService _project;
	private readonly IProjectAssetPipelineService _pipeline;
	private readonly IAssetSelectionService _selection;

	public AnimationClipEditorAssetHandler(
		AnimationClipEditorSession session,
		IEditorProjectService project,
		IProjectAssetPipelineService pipeline,
		IAssetSelectionService selection)
	{
		_session = session;
		_project = project;
		_pipeline = pipeline;
		_selection = selection;
	}

	public AssetType AssetType => AssetType.AnimationClip;
	public string DisplayName => "Animation Clip";
	public string ThumbnailLabel => "CLIP";

	public string GetSubtitle(AssetDatabaseEntry asset) =>
		AnimationClipEditorSession.IsAuthoredClip(asset)
			? DisplayName
			: "Imported Animation Clip";

	public IReadOnlyList<EditorAssetCreateMenuItem> GetCreateMenuItems() =>
		[
			new()
			{
				Label = DisplayName,
				CreateAction = Create
			}
		];

	public void DrawEditor(AssetDatabaseEntry asset)
	{
		if (ImGui.Button("Open in Animate"))
		{
			_session.Open(asset);
		}

		if (AnimationClipEditorSession.IsAuthoredClip(asset))
		{
			return;
		}

		ImGui.TextDisabled("Imported motion is read-only. Edit curves and markers in Animate.");
		if (ImGui.Button("Duplicate as Editable Clip"))
		{
			_session.DuplicateAsEditable(asset);
		}
	}

	/// <summary>
	/// Creates a clip bound to the skeleton the selection implies (a selected skeleton or model), or
	/// to the clip editor's current skeleton. With neither, the skeleton is chosen in the inspector.
	/// </summary>
	private EditorAssetCreationResult Create(string folder)
	{
		try
		{
			var assets = _project.CurrentAssetDatabase.Assets;
			AssetDatabaseEntry? selected = null;
			if (_selection.SelectedAssetId is { } selectedId && _project.TryGetAsset(selectedId, out var selectedAsset))
			{
				selected = selectedAsset;
			}

			var skeletonId = AnimationClipAssetFactory.FindSkeletonForAsset(selected, assets);
			if (skeletonId == Guid.Empty)
			{
				skeletonId = _session.BoundSkeletonId;
			}

			var path = AnimationClipAssetFactory.GetUniquePath(_project, folder, AnimationClipAssetFactory.DefaultName);
			var clip = new AuthoredAnimationClipFile
			{
				Skeleton = new AssetRef<Skeleton>
				{
					NodeId = skeletonId
				}
			};
			var asset = AnimationClipAssetFactory.Create(_project, _pipeline, path, clip);
			return EditorAssetCreationResult.Succeeded(asset.Id);
		}
		catch (Exception exception)
		{
			return EditorAssetCreationResult.Failed(exception.Message);
		}
	}
}
