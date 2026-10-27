using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AnimationClipEditorTests
{
	private static readonly Guid RigSourceId = Guid.NewGuid();
	private static readonly Guid TakeSourceId = Guid.NewGuid();

	private readonly AssetDatabaseEntry _rigModel = Asset(AssetType.Model3D, RigSourceId, "Rig.fbx");
	private readonly AssetDatabaseEntry _rigSkeleton = Asset(AssetType.Skeleton, RigSourceId, "rig");
	private readonly AssetDatabaseEntry _takeModel = Asset(AssetType.Model3D, TakeSourceId, "Take.fbx");
	private readonly AssetDatabaseEntry _takeSkeleton = Asset(AssetType.Skeleton, TakeSourceId, "take");
	private readonly AssetDatabaseEntry _takeClip = Asset(AssetType.AnimationClip, TakeSourceId, "Take");
	private readonly AssetDatabaseEntry _prefab = Asset(AssetType.Prefab, Guid.NewGuid(), "Knight.prefab");

	[Test]
	public void AuthoredClip_UsesItsSkeletonAndChosenPreviewModel()
	{
		var authored = Authored(_rigSkeleton.Id, _prefab.Id);

		var resolved = Resolve(Asset(AssetType.AnimationClip, Guid.NewGuid(), "Swing.anim.json"), authored, [], out var binding, out _);

		Assert.That(resolved, Is.True);
		Assert.That(binding, Is.EqualTo(new AnimationClipBinding(_rigSkeleton.Id, _prefab.Id)));
	}

	[Test]
	public void AuthoredClip_WithoutPreviewModelFallsBackToTheSkeletonsModel()
	{
		var authored = Authored(_rigSkeleton.Id, Guid.Empty);

		Resolve(Asset(AssetType.AnimationClip, Guid.NewGuid(), "Swing.anim.json"), authored, [], out var binding, out _);

		Assert.That(binding.PreviewModelId, Is.EqualTo(_rigModel.Id));
	}

	[Test]
	public void AuthoredClip_IgnoresAPreviewModelThatIsNotAModel()
	{
		var authored = Authored(_rigSkeleton.Id, _takeClip.Id);

		Resolve(Asset(AssetType.AnimationClip, Guid.NewGuid(), "Swing.anim.json"), authored, [], out var binding, out _);

		Assert.That(binding.PreviewModelId, Is.EqualTo(_rigModel.Id));
	}

	[Test]
	public void ImportedClip_PrefersAnExplicitRig()
	{
		AssetDependencyRecord[] dependencies =
		[
			Dependency(_takeClip.Id, _takeSkeleton.Id, AnimationClipBindingResolver.SkeletonDependencyKind),
			Dependency(_takeClip.Id, _rigSkeleton.Id, AnimationClipBindingResolver.RigDependencyKind)
		];

		Resolve(_takeClip, null, dependencies, out var binding, out _);

		Assert.That(binding, Is.EqualTo(new AnimationClipBinding(_rigSkeleton.Id, _rigModel.Id)));
	}

	[Test]
	public void ImportedClip_UsesItsSourceSkeletonDependency()
	{
		AssetDependencyRecord[] dependencies =
		[
			Dependency(_takeClip.Id, _takeSkeleton.Id, AnimationClipBindingResolver.SkeletonDependencyKind)
		];

		Resolve(_takeClip, null, dependencies, out var binding, out _);

		Assert.That(binding, Is.EqualTo(new AnimationClipBinding(_takeSkeleton.Id, _takeModel.Id)));
	}

	[Test]
	public void ImportedClip_WithoutDependenciesUsesTheSkeletonFromTheSameFile()
	{
		Resolve(_takeClip, null, [], out var binding, out _);

		Assert.That(binding.SkeletonId, Is.EqualTo(_takeSkeleton.Id));
	}

	[Test]
	public void MissingSkeleton_ReportsWhy()
	{
		var authored = Authored(Guid.NewGuid(), Guid.Empty);

		var resolved = Resolve(Asset(AssetType.AnimationClip, Guid.NewGuid(), "Swing.anim.json"), authored, [], out _, out var error);

		Assert.That(resolved, Is.False);
		Assert.That(error, Does.Contain("skeleton"));
	}

	[Test]
	public void SkeletonWithoutModel_ReportsThatThereIsNothingToPreview()
	{
		var lonelySkeleton = Asset(AssetType.Skeleton, Guid.NewGuid(), "lonely");
		var authored = Authored(lonelySkeleton.Id, Guid.Empty);
		var assets = AllAssets().Append(lonelySkeleton).ToList();

		var resolved = AnimationClipBindingResolver.TryResolve(
			Asset(AssetType.AnimationClip, Guid.NewGuid(), "Swing.anim.json"),
			authored,
			assets,
			[],
			out _,
			out var error);

		Assert.That(resolved, Is.False);
		Assert.That(error, Does.Contain("preview"));
	}

	[Test]
	public void BoneOverlay_PicksTheNearestJoint()
	{
		var overlay = ProjectChain(out var parents);

		// Joint 1 projects to (200, 100); a click 4 pixels away hits it.
		Assert.That(overlay.Pick(new Vector2(204.0f, 100.0f), parents), Is.EqualTo(1));
	}

	[Test]
	public void BoneOverlay_SegmentSelectsTheChildBone()
	{
		var overlay = ProjectChain(out var parents);

		// Halfway along the segment from joint 1 to joint 2.
		Assert.That(overlay.Pick(new Vector2(250.0f, 100.0f), parents), Is.EqualTo(2));
	}

	[Test]
	public void BoneOverlay_MissReturnsNoBone()
	{
		var overlay = ProjectChain(out var parents);

		Assert.That(overlay.Pick(new Vector2(250.0f, 180.0f), parents), Is.EqualTo(-1));
	}

	[Test]
	public void BoneOverlay_IgnoresJointsBehindTheCamera()
	{
		var overlay = new BoneOverlay();
		overlay.Project([new Vector3(0.0f, 0.0f, -0.5f)], Matrix4x4.Identity, Vector2.Zero, new Vector2(400.0f, 200.0f));

		Assert.That(overlay.TryGetJointScreen(0, out _), Is.False);
		Assert.That(overlay.Pick(new Vector2(200.0f, 100.0f), [-1]), Is.EqualTo(-1));
	}

	[Test]
	public void EulerAngles_RoundTripsAPlainRotation()
	{
		var degrees = new Vector3(10.0f, 20.0f, 30.0f);

		var roundTripped = EulerAngles.ToDegrees(EulerAngles.FromDegrees(degrees));

		Assert.That(Vector3.Distance(roundTripped, degrees), Is.LessThan(1e-3f));
	}

	/// <summary>
	/// Three joints along screen-space X. With an identity clip transform a point projects to
	/// ((x * 0.5 + 0.5) * 400, (0.5 - y * 0.5) * 200), so x = -0.5, 0 and 0.5 land at 100, 200 and 300.
	/// </summary>
	private static BoneOverlay ProjectChain(out int[] parents)
	{
		parents = [-1, 0, 1];
		var overlay = new BoneOverlay();
		overlay.Project(
			[
				new Vector3(-0.5f, 0.0f, 0.5f),
				new Vector3(0.0f, 0.0f, 0.5f),
				new Vector3(0.5f, 0.0f, 0.5f)
			],
			Matrix4x4.Identity,
			Vector2.Zero,
			new Vector2(400.0f, 200.0f));
		return overlay;
	}

	private bool Resolve(
		AssetDatabaseEntry clip,
		AuthoredAnimationClipFile? authored,
		IReadOnlyList<AssetDependencyRecord> dependencies,
		out AnimationClipBinding binding,
		out string error)
	{
		var assets = AllAssets().Append(clip).Distinct().ToList();
		return AnimationClipBindingResolver.TryResolve(clip, authored, assets, dependencies, out binding, out error);
	}

	private IEnumerable<AssetDatabaseEntry> AllAssets() =>
		[_rigModel, _rigSkeleton, _takeModel, _takeSkeleton, _takeClip, _prefab];

	private static AuthoredAnimationClipFile Authored(Guid skeletonId, Guid previewModelId) =>
		new()
		{
			Skeleton = new AssetRef<Skeleton>
			{
				NodeId = skeletonId
			},
			PreviewModelId = previewModelId
		};

	private static AssetDependencyRecord Dependency(Guid from, Guid to, string kind) =>
		new()
		{
			FromNodeId = from,
			ToNodeId = to,
			Kind = kind,
			IsHard = true
		};

	private static AssetDatabaseEntry Asset(AssetType type, Guid sourceId, string name) =>
		new()
		{
			Id = Guid.NewGuid(),
			SourceId = sourceId,
			Type = type,
			Name = name,
			RelativeSourcePath = "Assets/" + name
		};
}
