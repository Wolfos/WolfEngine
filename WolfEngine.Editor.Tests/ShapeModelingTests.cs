using System.Numerics;
using WolfEngine.Editor.UI;
using WolfEngine.Modeling;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class ShapeModelingTests
{
	private string _directory = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"wolf-shape-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	[Test]
	public void Sphere_EvaluatesSignedDistance()
	{
		var program = ShapeSdfProgram.Compile(CreateTree(ShapeNode.CreatePrimitive(ShapePrimitiveKind.Sphere)));

		Assert.That(program.Evaluate(Vector3.Zero), Is.EqualTo(-0.5f).Within(1.0e-5f));
		Assert.That(program.Evaluate(new Vector3(1.0f, 0.0f, 0.0f)), Is.EqualTo(0.5f).Within(1.0e-5f));
	}

	[Test]
	public void GroupTransformAndScale_MoveAndScaleTheDistance()
	{
		var group = ShapeNode.CreateGroup("Scaled");
		group.Position = new Vector3(2.0f, 0.0f, 0.0f);
		group.Scale = 2.0f;
		group.Children.Add(ShapeNode.CreatePrimitive(ShapePrimitiveKind.Sphere));
		var program = ShapeSdfProgram.Compile(CreateTree(group));

		// A unit-diameter sphere scaled by two is a sphere of radius one around (2, 0, 0).
		Assert.That(program.Evaluate(new Vector3(2.0f, 0.0f, 0.0f)), Is.EqualTo(-1.0f).Within(1.0e-5f));
		Assert.That(program.Evaluate(new Vector3(4.0f, 0.0f, 0.0f)), Is.EqualTo(1.0f).Within(1.0e-5f));
		Assert.That(program.Bounds.Min.X, Is.EqualTo(1.0f).Within(1.0e-4f));
		Assert.That(program.Bounds.Max.X, Is.EqualTo(3.0f).Within(1.0e-4f));
	}

	[Test]
	public void Subtract_CarvesAndDoesNotGrowBounds()
	{
		var box = ShapeNode.CreatePrimitive(ShapePrimitiveKind.Box);
		var cut = ShapeNode.CreatePrimitive(ShapePrimitiveKind.Sphere);
		cut.Operation = ShapeOperation.Subtract;
		cut.Position = new Vector3(0.5f, 0.5f, 0.5f);
		cut.Dimensions = new Vector4(0.5f, 0.0f, 0.0f, 0.0f);
		var program = ShapeSdfProgram.Compile(CreateTree(box, cut));

		Assert.That(program.Evaluate(new Vector3(0.45f, 0.45f, 0.45f)), Is.GreaterThan(0.0f), "The corner inside the cut is removed.");
		Assert.That(program.Evaluate(new Vector3(-0.45f, -0.45f, -0.45f)), Is.LessThan(0.0f), "The far corner is untouched.");
		Assert.That(program.Bounds.Max.X, Is.EqualTo(0.5f).Within(1.0e-4f));
	}

	[Test]
	public void SubtractAsFirstChild_LeavesTheGroupEmpty()
	{
		var cut = ShapeNode.CreatePrimitive(ShapePrimitiveKind.Sphere);
		cut.Operation = ShapeOperation.Subtract;
		var program = ShapeSdfProgram.Compile(CreateTree(cut));

		Assert.That(program.Evaluate(Vector3.Zero), Is.GreaterThan(1.0e10f));
		Assert.That(program.Bounds.IsEmpty, Is.True);
	}

	[Test]
	public void SmoothUnion_BlendsBelowTheSharpMinimum()
	{
		var a = ShapeNode.CreatePrimitive(ShapePrimitiveKind.Sphere);
		a.Position = new Vector3(-0.4f, 0.0f, 0.0f);
		var b = ShapeNode.CreatePrimitive(ShapePrimitiveKind.Sphere);
		b.Position = new Vector3(0.4f, 0.0f, 0.0f);
		b.Smoothness = 0.3f;
		var sharp = ShapeSdfProgram.Compile(CreateTree(a.Clone(), Sharpened(b)));
		var smooth = ShapeSdfProgram.Compile(CreateTree(a, b));
		var between = new Vector3(0.0f, 0.4f, 0.0f);

		Assert.That(smooth.Evaluate(between), Is.LessThan(sharp.Evaluate(between)));
	}

	[Test]
	public void ExtractedSphere_IsClosedOutwardAndOnTheSurface()
	{
		var tree = CreateTree(ShapeNode.CreatePrimitive(ShapePrimitiveKind.Sphere));
		var settings = new ShapeCompileSettings
		{
			ReferenceResolution = 48
		};
		var result = new ShapeCompiler(new CpuSdfGridSampler()).Compile(ShapeSdfProgram.Compile(tree), settings, null);
		var mesh = result.Mesh;

		Assert.That(mesh.TriangleCount, Is.GreaterThan(500));

		var edgeUses = new Dictionary<(uint, uint), int>();
		for (var i = 0; i < mesh.Indices.Length; i += 3)
		{
			var a = mesh.Indices[i];
			var b = mesh.Indices[i + 1];
			var c = mesh.Indices[i + 2];
			CountEdge(edgeUses, a, b);
			CountEdge(edgeUses, b, c);
			CountEdge(edgeUses, c, a);

			var pa = mesh.Positions[a];
			var faceNormal = Vector3.Cross(mesh.Positions[b] - pa, mesh.Positions[c] - pa);
			var centroid = (pa + mesh.Positions[b] + mesh.Positions[c]) / 3.0f;
			Assert.That(Vector3.Dot(faceNormal, centroid), Is.GreaterThan(0.0f), $"Triangle {i / 3} winds inward.");
		}

		Assert.That(edgeUses.Values.All(count => count == 2), Is.True, "Every edge is shared by exactly two triangles.");

		var cellSize = result.Stats.CellSize;
		foreach (var position in mesh.Positions)
		{
			Assert.That(MathF.Abs(position.Length() - 0.5f), Is.LessThan(cellSize));
		}
	}

	[Test]
	public void ShapeFile_RoundTripsTreeSettingsAndMesh()
	{
		var asset = ShapeAsset.CreateDefault();
		var cut = ShapeNode.CreatePrimitive(ShapePrimitiveKind.Cylinder);
		cut.Operation = ShapeOperation.Subtract;
		cut.Smoothness = 0.05f;
		cut.Position = new Vector3(0.1f, 0.2f, 0.3f);
		cut.Rotation = Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f);
		cut.Scale = 0.75f;
		asset.Tree.Root.Children.Add(cut);
		asset.Settings.ReferenceResolution = 96;
		asset.Settings.MaterialId = Guid.NewGuid();
		asset.Mesh = new ShapeMesh(
			[Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
			[Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
			[0, 1, 2]);
		asset.MeshSourceHash = ShapeFile.ComputeSourceHash(asset.Tree, asset.Settings);
		var path = Path.Combine(_directory, "Round Trip.shape");

		ShapeFile.Write(path, asset);
		var read = ShapeFile.Read(path);

		Assert.That(ShapeFile.ComputeSourceHash(read.Tree, read.Settings), Is.EqualTo(asset.MeshSourceHash));
		Assert.That(read.MeshSourceHash, Is.EqualTo(asset.MeshSourceHash));
		Assert.That(read.Tree.Root.Children[1].Id, Is.EqualTo(cut.Id));
		Assert.That(read.Tree.Root.Children[1].Rotation, Is.EqualTo(cut.Rotation));
		Assert.That(read.Settings.MaterialId, Is.EqualTo(asset.Settings.MaterialId));
		Assert.That(read.Mesh!.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
		Assert.That(File.Exists(path + ".tmp"), Is.False);
	}

	[Test]
	public void ShapeFile_SkipsChunksItDoesNotKnow()
	{
		var asset = ShapeAsset.CreateDefault();
		using var original = new MemoryStream();
		ShapeFile.Write(original, asset);
		var bytes = original.ToArray();

		// Append a chunk from a future version and bump the chunk count.
		using var patched = new MemoryStream();
		patched.Write(bytes);
		using (var writer = new BinaryWriter(patched, System.Text.Encoding.UTF8, leaveOpen: true))
		{
			writer.Write(0x544E4948u); // "HINT"
			writer.Write(1);
			writer.Write(5L);
			writer.Write(new byte[] { 1, 2, 3, 4, 5 });
		}

		var patchedBytes = patched.ToArray();
		BitConverter.GetBytes(BitConverter.ToInt32(patchedBytes, 8) + 1).CopyTo(patchedBytes, 8);

		var read = ShapeFile.Read(new MemoryStream(patchedBytes));

		Assert.That(read.Tree.Root.Children, Has.Count.EqualTo(1));
	}

	[Test]
	public void MaterialChange_DoesNotChangeTheSourceHash()
	{
		var asset = ShapeAsset.CreateDefault();
		var before = ShapeFile.ComputeSourceHash(asset.Tree, asset.Settings);
		asset.Settings.MaterialId = Guid.NewGuid();

		Assert.That(ShapeFile.ComputeSourceHash(asset.Tree, asset.Settings), Is.EqualTo(before));
	}

	[Test]
	public void Undo_RestoresTheTreeAndItsCompiledMeshTogether()
	{
		var document = ShapeDocument.Create(Path.Combine(_directory, "Undo.shape"), ShapeAsset.CreateDefault());
		var scheduler = new ShapeCompileScheduler(new ShapeCompiler(new CpuSdfGridSampler()));
		Assert.That(scheduler.CompileNow(document), Is.True);
		var originalMesh = document.Mesh;
		var originalHash = document.MeshSourceHash;
		var primitiveId = document.Tree.Root.Children[0].Id;

		document.Edit("Move", (tree, _) => tree.Find(primitiveId)!.Position = new Vector3(0.3f, 0.0f, 0.0f));
		Assert.That(document.NeedsCompile, Is.True);
		Assert.That(scheduler.CompileNow(document), Is.True);
		var movedMesh = document.Mesh;
		Assert.That(movedMesh, Is.Not.SameAs(originalMesh));

		document.Undo();

		Assert.That(document.Tree.Find(primitiveId)!.Position, Is.EqualTo(Vector3.Zero));
		Assert.That(document.Mesh, Is.SameAs(originalMesh));
		Assert.That(document.MeshSourceHash, Is.EqualTo(originalHash));
		Assert.That(document.NeedsCompile, Is.False);

		document.Redo();

		Assert.That(document.Mesh, Is.SameAs(movedMesh));
		Assert.That(document.NeedsCompile, Is.False);
	}

	[Test]
	public void Transaction_GroupsEditsIntoOneUndoStep_AndUndoToSavedIsClean()
	{
		var path = Path.Combine(_directory, "Dirty.shape");
		var document = ShapeDocument.Create(path, ShapeAsset.CreateDefault());
		document.Save();
		Assert.That(document.IsDirty, Is.False);
		var primitiveId = document.Tree.Root.Children[0].Id;

		using (document.BeginTransaction("Drag"))
		{
			for (var i = 1; i <= 5; i++)
			{
				var x = i * 0.1f;
				document.Edit("Move", (tree, _) => tree.Find(primitiveId)!.Position = new Vector3(x, 0.0f, 0.0f));
			}
		}

		Assert.That(document.IsDirty, Is.True);
		Assert.That(document.UndoDescription, Is.EqualTo("Drag"));

		document.Undo();

		Assert.That(document.CanUndo, Is.False);
		Assert.That(document.IsDirty, Is.False);
	}

	[Test]
	public void StaleCompile_IsDroppedInsteadOfApplied()
	{
		var document = ShapeDocument.Create(Path.Combine(_directory, "Stale.shape"), ShapeAsset.CreateDefault());
		var sampler = new BlockingSampler();
		var scheduler = new ShapeCompileScheduler(new ShapeCompiler(sampler));
		var primitiveId = document.Tree.Root.Children[0].Id;

		scheduler.Update(document);
		document.Edit("Move", (tree, _) => tree.Find(primitiveId)!.Position = Vector3.UnitX);
		sampler.Release();
		Assert.That(scheduler.CompileNow(document), Is.True);

		Assert.That(scheduler.AppliedCompileCount, Is.EqualTo(1), "Only the compile of the current state lands.");
		Assert.That(document.MeshSourceHash, Is.EqualTo(document.CurrentSourceHash));
	}

	[Test]
	public void AxisScale_StretchesTheMatchingDimension()
	{
		var start = new Vector4(0.5f, 0.25f, 0.0f, 0.0f);

		var taller = ShapeNodeGizmoTarget.ScaleDimensions(ShapePrimitiveKind.Cylinder, start, new Vector3(1.0f, 2.0f, 1.0f));
		var wider = ShapeNodeGizmoTarget.ScaleDimensions(ShapePrimitiveKind.Cylinder, start, new Vector3(1.0f, 1.0f, 3.0f));

		Assert.That(taller, Is.EqualTo(new Vector4(0.5f, 0.5f, ShapePrimitives.MinDimension, ShapePrimitives.MinDimension)));
		Assert.That(wider.X, Is.EqualTo(1.5f).Within(1.0e-5f));
		Assert.That(wider.Y, Is.EqualTo(0.25f).Within(1.0e-5f));
	}

	private static ShapeTree CreateTree(params ShapeNode[] children)
	{
		var tree = new ShapeTree();
		tree.Root.Children.AddRange(children);
		return tree;
	}

	private static ShapeNode Sharpened(ShapeNode node)
	{
		var copy = node.Clone();
		copy.Smoothness = 0.0f;
		return copy;
	}

	private static void CountEdge(Dictionary<(uint, uint), int> edgeUses, uint a, uint b)
	{
		var key = a < b ? (a, b) : (b, a);
		edgeUses[key] = edgeUses.GetValueOrDefault(key) + 1;
	}

	/// <summary>A CPU sampler that holds its first sample until released, to make a compile go stale mid-flight.</summary>
	private sealed class BlockingSampler : ISdfGridSampler
	{
		private readonly ManualResetEventSlim _release = new(false);
		private readonly CpuSdfGridSampler _inner = new();

		public string Name => "blocking";

		public SdfGrid Sample(ShapeSdfProgram program, SdfGridLayout layout)
		{
			_release.Wait(TimeSpan.FromSeconds(10));
			return _inner.Sample(program, layout);
		}

		public void Release()
		{
			_release.Set();
		}
	}
}
