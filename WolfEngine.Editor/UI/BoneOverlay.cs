using System.Numerics;
using ImGuiNET;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

/// <summary>
/// Draws a skeleton as lines from each joint to its parent with a dot per joint, over the preview
/// image and without depth testing, and picks bones from it.
/// </summary>
internal sealed class BoneOverlay
{
	public const float PickRadiusPixels = 10.0f;

	private const float JointRadius = 3.5f;
	private const float SelectedJointRadius = 5.0f;
	private const float BoneThickness = 2.0f;
	private const float SelectedBoneThickness = 3.0f;

	private Vector2[] _jointScreen = [];
	private bool[] _jointVisible = [];

	public int BoneCount { get; private set; }

	/// <summary>Projects every joint. Joints behind the camera are marked invisible and never drawn or picked.</summary>
	public void Project(
		IReadOnlyList<Vector3> jointWorld,
		Matrix4x4 worldToClip,
		Vector2 imageMin,
		Vector2 imageMax)
	{
		BoneCount = jointWorld.Count;
		if (_jointScreen.Length < BoneCount)
		{
			_jointScreen = new Vector2[BoneCount];
			_jointVisible = new bool[BoneCount];
		}

		for (var i = 0; i < BoneCount; i++)
		{
			_jointVisible[i] = TransformGizmo.TryProjectToScreen(
				jointWorld[i],
				worldToClip,
				imageMin,
				imageMax,
				out _jointScreen[i]);
		}
	}

	public bool TryGetJointScreen(int bone, out Vector2 screen)
	{
		screen = default;
		if (bone < 0 || bone >= BoneCount || _jointVisible[bone] == false)
		{
			return false;
		}

		screen = _jointScreen[bone];
		return true;
	}

	public void Draw(ImDrawListPtr drawList, IReadOnlyList<int> parents, int selectedBone)
	{
		var boneColor = ImGui.ColorConvertFloat4ToU32(new ColorRGBA(0.85f, 0.85f, 0.88f, 0.75f));
		var selectedColor = ImGui.ColorConvertFloat4ToU32(new ColorRGBA(1.0f, 0.78f, 0.25f, 1.0f));

		for (var i = 0; i < BoneCount; i++)
		{
			var parent = parents[i];
			if (parent < 0 ||
			    _jointVisible[i] == false ||
			    _jointVisible[parent] == false)
			{
				continue;
			}

			var selected = i == selectedBone;
			drawList.AddLine(
				_jointScreen[parent],
				_jointScreen[i],
				selected ? selectedColor : boneColor,
				selected ? SelectedBoneThickness : BoneThickness);
		}

		for (var i = 0; i < BoneCount; i++)
		{
			if (_jointVisible[i] == false)
			{
				continue;
			}

			var selected = i == selectedBone;
			drawList.AddCircleFilled(
				_jointScreen[i],
				selected ? SelectedJointRadius : JointRadius,
				selected ? selectedColor : boneColor);
		}
	}

	/// <summary>
	/// The bone under the pointer, or -1. A joint dot wins over a segment; a segment selects the bone
	/// it ends at, which is the bone that segment rotates.
	/// </summary>
	public int Pick(Vector2 pointer, IReadOnlyList<int> parents)
	{
		var best = -1;
		var bestDistanceSquared = PickRadiusPixels * PickRadiusPixels;
		for (var i = 0; i < BoneCount; i++)
		{
			if (_jointVisible[i] == false)
			{
				continue;
			}

			var distanceSquared = Vector2.DistanceSquared(pointer, _jointScreen[i]);
			if (distanceSquared <= bestDistanceSquared)
			{
				best = i;
				bestDistanceSquared = distanceSquared;
			}
		}

		if (best >= 0)
		{
			return best;
		}

		bestDistanceSquared = PickRadiusPixels * PickRadiusPixels;
		for (var i = 0; i < BoneCount; i++)
		{
			var parent = parents[i];
			if (parent < 0 ||
			    _jointVisible[i] == false ||
			    _jointVisible[parent] == false)
			{
				continue;
			}

			var distanceSquared = DistanceToSegmentSquared(pointer, _jointScreen[parent], _jointScreen[i]);
			if (distanceSquared <= bestDistanceSquared)
			{
				best = i;
				bestDistanceSquared = distanceSquared;
			}
		}

		return best;
	}

	private static float DistanceToSegmentSquared(Vector2 point, Vector2 a, Vector2 b)
	{
		var ab = b - a;
		var lengthSquared = ab.LengthSquared();
		if (lengthSquared <= 1e-8f)
		{
			return Vector2.DistanceSquared(point, a);
		}

		var t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0.0f, 1.0f);
		return Vector2.DistanceSquared(point, a + ab * t);
	}
}
