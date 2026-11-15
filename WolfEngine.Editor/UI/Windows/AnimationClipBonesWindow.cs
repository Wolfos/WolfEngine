using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

/// <summary>The clip's skeleton as a tree. Selecting a bone here selects it everywhere in the clip editor.</summary>
public sealed class AnimationClipBonesWindow : EditorWindow
{
	private readonly AnimationClipEditorSession _session;
	private readonly HashSet<string> _keyedBones = new(StringComparer.Ordinal);
	private readonly HashSet<int> _revealAncestors = new();
	private Skeleton? _hierarchySkeleton;
	private List<int>[] _children = [];
	private List<int> _roots = new();
	private AnimationClipDocument? _keyedDocument;
	private long _keyedRevision = -1;
	private int _revealedSelection = -1;
	private bool _scrollToSelection;

	public AnimationClipBonesWindow(AnimationClipEditorSession session)
	{
		_session = session;
	}

	public override string Name => "Bones";

	public override void Draw(EditorScene scene)
	{
		Begin();
		_session.Tick();
		AnimationClipEditorPanels.HandleFocusAndShortcuts(_session);

		var skeleton = _session.Skeleton;
		if (_session.HasClip == false || skeleton is null)
		{
			ImGui.TextDisabled(_session.HasClip ? "The clip's skeleton is unavailable." : "No clip open.");
			ImGui.End();
			return;
		}

		RebuildHierarchy(skeleton);
		RefreshKeyedBones();
		PrepareReveal(skeleton);

		foreach (var root in _roots)
		{
			DrawBone(skeleton, root);
		}

		ImGui.End();
	}

	private void DrawBone(Skeleton skeleton, int bone)
	{
		var name = skeleton.BoneNames[bone];
		var children = _children[bone];
		var selected = string.Equals(_session.SelectedBone, name, StringComparison.Ordinal);

		var flags = ImGuiTreeNodeFlags.OpenOnArrow |
		            ImGuiTreeNodeFlags.OpenOnDoubleClick |
		            ImGuiTreeNodeFlags.SpanAvailWidth |
		            ImGuiTreeNodeFlags.DefaultOpen;
		if (children.Count == 0)
		{
			flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
		}

		if (selected)
		{
			flags |= ImGuiTreeNodeFlags.Selected;
		}

		if (_revealAncestors.Contains(bone))
		{
			ImGui.SetNextItemOpen(true);
		}

		var unkeyed = _session.HasUnkeyedChange(name);
		if (unkeyed)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, new ColorRGBA(1.0f, 0.7f, 0.3f, 1.0f));
		}

		var open = ImGui.TreeNodeEx($"{name}##bone{bone}", flags);
		if (unkeyed)
		{
			ImGui.PopStyleColor();
		}

		if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && ImGui.IsItemToggledOpen() == false)
		{
			_session.SelectBone(name);
			_revealedSelection = _session.SelectionRevision;
		}

		if (selected && _scrollToSelection)
		{
			ImGui.SetScrollHereY(0.5f);
			_scrollToSelection = false;
		}

		if (_keyedBones.Contains(name))
		{
			ImGui.SameLine();
			ImGui.TextColored(new ColorRGBA(1.0f, 0.78f, 0.25f, 1.0f), "◆");
		}

		if (open && children.Count > 0)
		{
			foreach (var child in children)
			{
				DrawBone(skeleton, child);
			}

			ImGui.TreePop();
		}
	}

	private void RebuildHierarchy(Skeleton skeleton)
	{
		if (ReferenceEquals(skeleton, _hierarchySkeleton))
		{
			return;
		}

		_hierarchySkeleton = skeleton;
		_children = new List<int>[skeleton.BoneCount];
		_roots = new List<int>();
		for (var i = 0; i < skeleton.BoneCount; i++)
		{
			_children[i] = new List<int>();
		}

		for (var i = 0; i < skeleton.BoneCount; i++)
		{
			var parent = skeleton.ParentIndices[i];
			if (parent >= 0)
			{
				_children[parent].Add(i);
			}
			else
			{
				_roots.Add(i);
			}
		}

		_revealedSelection = -1;
	}

	private void RefreshKeyedBones()
	{
		var document = _session.Document;
		if (ReferenceEquals(document, _keyedDocument) && document?.Revision == _keyedRevision)
		{
			return;
		}

		_keyedDocument = document;
		_keyedRevision = document?.Revision ?? -1;
		_keyedBones.Clear();
		if (document is null)
		{
			return;
		}

		foreach (var track in document.Clip.TransformTracks)
		{
			if (track.Kind == AnimationBindingKind.Bone && track.HasKeys())
			{
				_keyedBones.Add(track.Path);
			}
		}
	}

	/// <summary>Opens the path to a bone selected elsewhere, such as in the viewport, and scrolls to it once.</summary>
	private void PrepareReveal(Skeleton skeleton)
	{
		_revealAncestors.Clear();
		if (_revealedSelection == _session.SelectionRevision)
		{
			return;
		}

		_revealedSelection = _session.SelectionRevision;
		if (_session.SelectedBone is not { } bone || skeleton.TryGetBoneIndex(bone, out var index) == false)
		{
			return;
		}

		var parent = skeleton.ParentIndices[index];
		while (parent >= 0)
		{
			_revealAncestors.Add(parent);
			parent = skeleton.ParentIndices[parent];
		}

		_scrollToSelection = true;
	}
}
