using WolfEngine.Animation;

namespace WolfEngine.Editor.UI;

internal enum DopesheetRowKind
{
	Summary,
	Bone
}

/// <param name="BoneIndex">Skeleton bone index, or -1 for the summary row.</param>
/// <param name="IsExpanded">False when the row is collapsed and stands in for its whole subtree.</param>
internal readonly record struct DopesheetRow(
	DopesheetRowKind Kind,
	int BoneIndex,
	string Name,
	int Depth,
	bool HasChildren,
	bool IsExpanded);

/// <summary>
/// The rows and key diamonds of the dopesheet. A bone row shows that bone's keys; a collapsed bone
/// row shows the keys of its whole subtree; the summary row shows every key. A diamond on an
/// aggregate row stands for all the keys it covers on that frame, so selecting or moving it acts
/// on all of them.
/// </summary>
internal sealed class DopesheetModel
{
	public const string SummaryRowName = "Summary";

	private readonly List<DopesheetRow> _rows = new();
	private readonly List<string[]> _rowBones = new();
	private readonly List<int[]> _rowFrames = new();
	private IReadOnlyDictionary<string, int[]> _boneKeys = new Dictionary<string, int[]>();

	public IReadOnlyList<DopesheetRow> Rows => _rows;

	/// <param name="boneKeys">Keyed frames per bone name, ascending.</param>
	/// <param name="collapsedBones">Bones whose subtree is folded into their own row.</param>
	/// <param name="keyedOnly">Hide bones whose subtree has no keys.</param>
	public void Rebuild(
		Skeleton skeleton,
		IReadOnlyDictionary<string, int[]> boneKeys,
		IReadOnlySet<string> collapsedBones,
		bool keyedOnly)
	{
		ArgumentNullException.ThrowIfNull(skeleton);
		ArgumentNullException.ThrowIfNull(boneKeys);
		ArgumentNullException.ThrowIfNull(collapsedBones);

		_boneKeys = boneKeys;
		_rows.Clear();
		_rowBones.Clear();
		_rowFrames.Clear();

		var children = BuildChildren(skeleton);
		var subtreeKeyed = BuildSubtreeKeyed(skeleton, children, boneKeys);

		var keyedBones = new List<string>();
		for (var i = 0; i < skeleton.BoneCount; i++)
		{
			if (HasKeys(skeleton.BoneNames[i]))
			{
				keyedBones.Add(skeleton.BoneNames[i]);
			}
		}

		AddRow(new DopesheetRow(DopesheetRowKind.Summary, -1, SummaryRowName, 0, false, true), keyedBones.ToArray());

		for (var i = 0; i < skeleton.BoneCount; i++)
		{
			if (skeleton.ParentIndices[i] < 0)
			{
				AddBone(skeleton, children, subtreeKeyed, collapsedBones, keyedOnly, i, 0);
			}
		}
	}

	/// <summary>Frames that show a diamond on this row, ascending.</summary>
	public IReadOnlyList<int> GetRowFrames(int rowIndex) => _rowFrames[rowIndex];

	/// <summary>The bones a row stands for: one bone, a collapsed subtree, or every keyed bone.</summary>
	public IReadOnlyList<string> GetRowBones(int rowIndex) => _rowBones[rowIndex];

	/// <summary>The real keys a diamond on this row and frame stands for.</summary>
	public List<AnimationKeyRef> GetRowKeysAt(int rowIndex, int frame)
	{
		var keys = new List<AnimationKeyRef>();
		foreach (var bone in _rowBones[rowIndex])
		{
			if (HasKeyAt(bone, frame))
			{
				keys.Add(new AnimationKeyRef(bone, frame));
			}
		}

		return keys;
	}

	/// <summary>Every real key under the diamonds inside a row and frame range, both ends inclusive.</summary>
	public HashSet<AnimationKeyRef> GetKeysInBox(int firstRow, int lastRow, int firstFrame, int lastFrame)
	{
		var keys = new HashSet<AnimationKeyRef>();
		if (_rows.Count == 0)
		{
			return keys;
		}

		firstRow = Math.Max(0, firstRow);
		lastRow = Math.Min(_rows.Count - 1, lastRow);
		for (var row = firstRow; row <= lastRow; row++)
		{
			foreach (var frame in _rowFrames[row])
			{
				if (frame < firstFrame || frame > lastFrame)
				{
					continue;
				}

				keys.UnionWith(GetRowKeysAt(row, frame));
			}
		}

		return keys;
	}

	/// <summary>A diamond reads as selected when every key it stands for is selected.</summary>
	public bool IsRowKeySelected(int rowIndex, int frame, IReadOnlySet<AnimationKeyRef> selection)
	{
		if (selection.Count == 0)
		{
			return false;
		}

		var any = false;
		foreach (var bone in _rowBones[rowIndex])
		{
			if (HasKeyAt(bone, frame) == false)
			{
				continue;
			}

			if (selection.Contains(new AnimationKeyRef(bone, frame)) == false)
			{
				return false;
			}

			any = true;
		}

		return any;
	}

	private void AddBone(
		Skeleton skeleton,
		List<int>[] children,
		bool[] subtreeKeyed,
		IReadOnlySet<string> collapsedBones,
		bool keyedOnly,
		int bone,
		int depth)
	{
		if (keyedOnly && subtreeKeyed[bone] == false)
		{
			return;
		}

		var name = skeleton.BoneNames[bone];
		var hasChildren = HasVisibleChildren(children[bone], subtreeKeyed, keyedOnly);
		var expanded = hasChildren == false || collapsedBones.Contains(name) == false;

		var bones = expanded
			? new[] { name }
			: CollectSubtree(skeleton, children, bone);
		AddRow(new DopesheetRow(DopesheetRowKind.Bone, bone, name, depth, hasChildren, expanded), bones);

		if (expanded == false)
		{
			return;
		}

		foreach (var child in children[bone])
		{
			AddBone(skeleton, children, subtreeKeyed, collapsedBones, keyedOnly, child, depth + 1);
		}
	}

	private void AddRow(DopesheetRow row, string[] bones)
	{
		_rows.Add(row);
		_rowBones.Add(bones);

		var frames = new SortedSet<int>();
		foreach (var bone in bones)
		{
			if (_boneKeys.TryGetValue(bone, out var boneFrames))
			{
				frames.UnionWith(boneFrames);
			}
		}

		_rowFrames.Add(frames.ToArray());
	}

	private bool HasKeys(string bone) =>
		_boneKeys.TryGetValue(bone, out var frames) && frames.Length > 0;

	private bool HasKeyAt(string bone, int frame) =>
		_boneKeys.TryGetValue(bone, out var frames) && Array.BinarySearch(frames, frame) >= 0;

	private static bool HasVisibleChildren(List<int> children, bool[] subtreeKeyed, bool keyedOnly)
	{
		if (keyedOnly == false)
		{
			return children.Count > 0;
		}

		foreach (var child in children)
		{
			if (subtreeKeyed[child])
			{
				return true;
			}
		}

		return false;
	}

	private static string[] CollectSubtree(Skeleton skeleton, List<int>[] children, int root)
	{
		var bones = new List<string>();
		var pending = new Stack<int>();
		pending.Push(root);
		while (pending.TryPop(out var bone))
		{
			bones.Add(skeleton.BoneNames[bone]);
			foreach (var child in children[bone])
			{
				pending.Push(child);
			}
		}

		return bones.ToArray();
	}

	private static List<int>[] BuildChildren(Skeleton skeleton)
	{
		var children = new List<int>[skeleton.BoneCount];
		for (var i = 0; i < skeleton.BoneCount; i++)
		{
			children[i] = new List<int>();
		}

		for (var i = 0; i < skeleton.BoneCount; i++)
		{
			var parent = skeleton.ParentIndices[i];
			if (parent >= 0)
			{
				children[parent].Add(i);
			}
		}

		return children;
	}

	/// <summary>Parents precede children in a skeleton, so a reverse pass sees every child first.</summary>
	private static bool[] BuildSubtreeKeyed(Skeleton skeleton, List<int>[] children, IReadOnlyDictionary<string, int[]> boneKeys)
	{
		var keyed = new bool[skeleton.BoneCount];
		for (var i = skeleton.BoneCount - 1; i >= 0; i--)
		{
			keyed[i] = boneKeys.TryGetValue(skeleton.BoneNames[i], out var frames) && frames.Length > 0;
			foreach (var child in children[i])
			{
				keyed[i] |= keyed[child];
			}
		}

		return keyed;
	}
}

/// <summary>Maps frames to horizontal screen positions in the dopesheet's key area.</summary>
internal readonly record struct DopesheetView(float KeyAreaLeft, float PixelsPerFrame, float StartFrame)
{
	public float FrameToX(float frame) => KeyAreaLeft + (frame - StartFrame) * PixelsPerFrame;

	public float XToFrame(float x) => StartFrame + (x - KeyAreaLeft) / PixelsPerFrame;

	public int XToNearestFrame(float x) => (int)MathF.Round(XToFrame(x));

	/// <summary>A view that fits frames 0 to <paramref name="lengthFrames"/> into the width, with a small margin each side.</summary>
	public static DopesheetView Fit(float keyAreaLeft, float keyAreaWidth, int lengthFrames, float marginPixels)
	{
		var usable = MathF.Max(1.0f, keyAreaWidth - marginPixels * 2.0f);
		var pixelsPerFrame = usable / Math.Max(1, lengthFrames);
		return new DopesheetView(keyAreaLeft, pixelsPerFrame, -marginPixels / pixelsPerFrame);
	}

	/// <summary>Zooms by a factor while keeping the frame under <paramref name="anchorX"/> in place.</summary>
	public DopesheetView ZoomAround(float anchorX, float factor, float minPixelsPerFrame, float maxPixelsPerFrame)
	{
		var anchorFrame = XToFrame(anchorX);
		var pixelsPerFrame = Math.Clamp(PixelsPerFrame * factor, minPixelsPerFrame, maxPixelsPerFrame);
		return new DopesheetView(KeyAreaLeft, pixelsPerFrame, anchorFrame - (anchorX - KeyAreaLeft) / pixelsPerFrame);
	}
}
