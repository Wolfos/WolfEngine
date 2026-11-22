using System.Numerics;
using System.Text.RegularExpressions;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Projects;

/// <summary>
/// Finds the levels of detail authored in an imported model and ties each coarser level to the level 0 mesh it
/// replaces. See docs/mesh-lod.md.
///
/// Asset packs mark levels by node name: <c>beech_tree_05_LOD0</c>, <c>beech_tree_05_LOD1</c>. Nodes whose names
/// differ only in that suffix, and that sit under the same parent, form a group. Within a group, each mesh of a
/// coarser level is matched to the level 0 mesh with the same material, so a node split into one mesh per
/// material keeps one level chain per material.
///
/// The model's node list is left as it is, so the hierarchy keys that mesh node ids derive from do not move. The
/// coarser level nodes are only marked as excluded: the importer still writes their meshes, as the levels, but
/// does not turn those nodes into entities.
///
/// Anything this cannot group safely imports exactly as it did before levels existed: a skinned mesh, a level node
/// with a mesh that has no level 0 counterpart, a missing level 0, or a level placed with a mirroring transform.
/// </summary>
internal sealed class ModelLodGroups
{
	private static readonly Regex LodSuffix = new(
		@"^(?<base>.+?)[_ .\-]?LOD(?<level>\d+)$",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>Assimp names the pivot nodes it inserts for an FBX node after that node, with this infix.</summary>
	private const string AssimpPivotInfix = "_$AssimpFbx$_";

	private const float IdentityTolerance = 1e-4f;

	private readonly Dictionary<(int NodeIndex, int MeshIndex), IReadOnlyList<(int NodeIndex, int MeshIndex)>> _levels;
	private readonly HashSet<int> _excludedNodes;

	private ModelLodGroups(
		ImportedScene scene,
		Dictionary<(int NodeIndex, int MeshIndex), IReadOnlyList<(int NodeIndex, int MeshIndex)>> levels,
		HashSet<int> excludedNodes)
	{
		Scene = scene;
		_levels = levels;
		_excludedNodes = excludedNodes;
	}

	/// <summary>
	/// The imported scene with every coarser level's mesh moved into the local space of the level 0 node it belongs
	/// to. Node order and names are unchanged.
	/// </summary>
	public ImportedScene Scene { get; }

	public int GroupedMeshCount => _levels.Count;

	/// <summary>Whether a node only carries coarser levels, and so must not become an entity.</summary>
	public bool IsExcluded(int nodeIndex)
	{
		return _excludedNodes.Contains(nodeIndex);
	}

	/// <summary>The coarser levels of a level 0 mesh, finest first, or an empty list.</summary>
	public IReadOnlyList<(int NodeIndex, int MeshIndex)> GetLevels(int nodeIndex, int meshIndex)
	{
		return _levels.TryGetValue((nodeIndex, meshIndex), out var levels)
			? levels
			: Array.Empty<(int NodeIndex, int MeshIndex)>();
	}

	public static ModelLodGroups Find(ImportedScene scene)
	{
		ArgumentNullException.ThrowIfNull(scene);

		var nodes = scene.Nodes;
		var world = ComputeWorldTransforms(nodes);
		var children = BuildChildLists(nodes);

		var candidates = new Dictionary<(int Parent, string Base), List<(int Level, int NodeIndex)>>();
		for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			var node = nodes[nodeIndex];
			if (node.Meshes.Count == 0)
			{
				continue;
			}

			var match = LodSuffix.Match(node.Name);
			if (match.Success == false)
			{
				continue;
			}

			var key = (FindGroupParent(nodes, nodeIndex), match.Groups["base"].Value.ToUpperInvariant());
			if (candidates.TryGetValue(key, out var members) == false)
			{
				members = new List<(int Level, int NodeIndex)>();
				candidates.Add(key, members);
			}

			members.Add((int.Parse(match.Groups["level"].Value), nodeIndex));
		}

		var levels = new Dictionary<(int NodeIndex, int MeshIndex), IReadOnlyList<(int NodeIndex, int MeshIndex)>>();
		var excludedNodes = new HashSet<int>();
		var bakedMeshes = new Dictionary<(int NodeIndex, int MeshIndex), Mesh>();
		foreach (var members in candidates.Values)
		{
			TryGroup(nodes, world, children, members, levels, excludedNodes, bakedMeshes);
		}

		if (bakedMeshes.Count == 0)
		{
			return new ModelLodGroups(scene, levels, excludedNodes);
		}

		var rewrittenNodes = new List<ImportedNode>(nodes.Count);
		for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			var node = nodes[nodeIndex];
			var meshes = node.Meshes;
			for (var meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
			{
				if (bakedMeshes.TryGetValue((nodeIndex, meshIndex), out var baked))
				{
					meshes = meshes.ToList();
					meshes[meshIndex] = meshes[meshIndex] with
					{
						Mesh = baked
					};
				}
			}

			rewrittenNodes.Add(ReferenceEquals(meshes, node.Meshes) ? node : node with { Meshes = meshes });
		}

		return new ModelLodGroups(scene with { Nodes = rewrittenNodes }, levels, excludedNodes);
	}

	private static void TryGroup(
		List<ImportedNode> nodes,
		Matrix4x4[] world,
		List<int>[] children,
		List<(int Level, int NodeIndex)> members,
		Dictionary<(int NodeIndex, int MeshIndex), IReadOnlyList<(int NodeIndex, int MeshIndex)>> levels,
		HashSet<int> excludedNodes,
		Dictionary<(int NodeIndex, int MeshIndex), Mesh> bakedMeshes)
	{
		members.Sort((left, right) => left.Level.CompareTo(right.Level));
		if (members.Count < 2 ||
		    members[0].Level != 0)
		{
			return;
		}

		for (var i = 1; i < members.Count; i++)
		{
			if (members[i].Level == members[i - 1].Level)
			{
				return;
			}
		}

		var levelZeroNode = members[0].NodeIndex;
		var levelZeroMeshes = nodes[levelZeroNode].Meshes;
		var meshByMaterial = new Dictionary<int, int>();
		for (var meshIndex = 0; meshIndex < levelZeroMeshes.Count; meshIndex++)
		{
			var mesh = levelZeroMeshes[meshIndex];
			if (IsSkinned(mesh) ||
			    meshByMaterial.TryAdd(mesh.MaterialIndex, meshIndex) == false)
			{
				return;
			}
		}

		if (Matrix4x4.Invert(world[levelZeroNode], out var levelZeroInverse) == false)
		{
			return;
		}

		var chains = new List<(int NodeIndex, int MeshIndex)>[levelZeroMeshes.Count];
		for (var meshIndex = 0; meshIndex < chains.Length; meshIndex++)
		{
			chains[meshIndex] = new List<(int NodeIndex, int MeshIndex)>();
		}

		var groupExcluded = new HashSet<int>();
		var groupBaked = new Dictionary<(int NodeIndex, int MeshIndex), Mesh>();
		for (var memberIndex = 1; memberIndex < members.Count; memberIndex++)
		{
			var levelNode = members[memberIndex].NodeIndex;
			var levelMeshes = nodes[levelNode].Meshes;
			var toLevelZero = world[levelNode] * levelZeroInverse;
			if (Determinant3x3(toLevelZero) <= 0.0f)
			{
				return;
			}

			var isIdentity = IsNearIdentity(toLevelZero);
			for (var meshIndex = 0; meshIndex < levelMeshes.Count; meshIndex++)
			{
				var levelMesh = levelMeshes[meshIndex];
				if (IsSkinned(levelMesh) ||
				    meshByMaterial.TryGetValue(levelMesh.MaterialIndex, out var levelZeroMeshIndex) == false)
				{
					return;
				}

				var chain = chains[levelZeroMeshIndex];

				// A level that skips a mesh ends that mesh's chain. A later level that brings it back would be
				// geometry with nowhere to go, so the group imports as separate nodes instead.
				if (chain.Count != memberIndex - 1)
				{
					return;
				}

				chain.Add((levelNode, meshIndex));
				if (isIdentity == false)
				{
					groupBaked[(levelNode, meshIndex)] = Transform(levelMesh.Mesh, toLevelZero);
				}
			}

			if (TryExcludeLevelNode(nodes, children, levelNode, groupExcluded) == false)
			{
				return;
			}
		}

		for (var meshIndex = 0; meshIndex < chains.Length; meshIndex++)
		{
			if (chains[meshIndex].Count > 0)
			{
				levels[(levelZeroNode, meshIndex)] = chains[meshIndex];
			}
		}

		excludedNodes.UnionWith(groupExcluded);
		foreach (var pair in groupBaked)
		{
			bakedMeshes[pair.Key] = pair.Value;
		}
	}

	/// <summary>
	/// Marks a coarser level's node, its subtree and the pivot nodes Assimp inserted for it as excluded. Fails when
	/// that would drop a mesh that is not one of the levels, or orphan a node that is not.
	/// </summary>
	private static bool TryExcludeLevelNode(
		List<ImportedNode> nodes,
		List<int>[] children,
		int levelNode,
		HashSet<int> excluded)
	{
		var pending = new Stack<int>();
		pending.Push(levelNode);
		while (pending.TryPop(out var nodeIndex))
		{
			if (nodeIndex != levelNode &&
			    nodes[nodeIndex].Meshes.Count > 0)
			{
				return false;
			}

			excluded.Add(nodeIndex);
			foreach (var child in children[nodeIndex])
			{
				pending.Push(child);
			}
		}

		var pivotPrefix = nodes[levelNode].Name + AssimpPivotInfix;
		for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			if (nodes[nodeIndex].Name.StartsWith(pivotPrefix, StringComparison.Ordinal) == false)
			{
				continue;
			}

			// A pivot node that leads anywhere but the level node and its own pivots would orphan that subtree.
			if (SubtreeIsWithin(children, nodeIndex, candidate =>
				    excluded.Contains(candidate) ||
				    nodes[candidate].Name.StartsWith(pivotPrefix, StringComparison.Ordinal)) == false)
			{
				return false;
			}

			excluded.Add(nodeIndex);
		}

		return true;
	}

	private static bool SubtreeIsWithin(List<int>[] children, int root, Func<int, bool> isAllowed)
	{
		var pending = new Stack<int>();
		pending.Push(root);
		while (pending.TryPop(out var nodeIndex))
		{
			if (isAllowed(nodeIndex) == false)
			{
				return false;
			}

			foreach (var child in children[nodeIndex])
			{
				pending.Push(child);
			}
		}

		return true;
	}

	/// <summary>
	/// The node a level hangs from, skipping the pivot nodes Assimp inserts above an FBX node. Each level gets its
	/// own pivot chain, so without this no two levels would share a parent.
	/// </summary>
	private static int FindGroupParent(List<ImportedNode> nodes, int nodeIndex)
	{
		var pivotPrefix = nodes[nodeIndex].Name + AssimpPivotInfix;
		var parent = nodes[nodeIndex].ParentIndex;
		while (parent >= 0 &&
		       nodes[parent].Name.StartsWith(pivotPrefix, StringComparison.Ordinal))
		{
			parent = nodes[parent].ParentIndex;
		}

		return parent;
	}

	private static bool IsSkinned(ImportedNodeMesh mesh)
	{
		return mesh.SkeletonIndex >= 0 || mesh.Mesh.IsSkinned;
	}

	private static Matrix4x4[] ComputeWorldTransforms(List<ImportedNode> nodes)
	{
		var world = new Matrix4x4[nodes.Count];
		for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			var node = nodes[nodeIndex];
			world[nodeIndex] = node.ParentIndex >= 0
				? node.LocalTransform * world[node.ParentIndex]
				: node.LocalTransform;
		}

		return world;
	}

	private static List<int>[] BuildChildLists(List<ImportedNode> nodes)
	{
		var children = new List<int>[nodes.Count];
		for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			children[nodeIndex] = new List<int>();
		}

		for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			var parent = nodes[nodeIndex].ParentIndex;
			if (parent >= 0)
			{
				children[parent].Add(nodeIndex);
			}
		}

		return children;
	}

	private static float Determinant3x3(Matrix4x4 matrix)
	{
		return matrix.M11 * (matrix.M22 * matrix.M33 - matrix.M23 * matrix.M32) -
		       matrix.M12 * (matrix.M21 * matrix.M33 - matrix.M23 * matrix.M31) +
		       matrix.M13 * (matrix.M21 * matrix.M32 - matrix.M22 * matrix.M31);
	}

	private static bool IsNearIdentity(Matrix4x4 matrix)
	{
		var identity = Matrix4x4.Identity;
		for (var row = 0; row < 4; row++)
		{
			for (var column = 0; column < 4; column++)
			{
				if (MathF.Abs(matrix[row, column] - identity[row, column]) > IdentityTolerance)
				{
					return false;
				}
			}
		}

		return true;
	}

	/// <summary>
	/// Moves a level's geometry from its own node's space into the level 0 node's space, so drawing it with the level
	/// 0 entity's transform puts it where the source file placed it.
	/// </summary>
	private static Mesh Transform(Mesh mesh, Matrix4x4 transform)
	{
		var normalTransform = Matrix4x4.Invert(transform, out var inverse)
			? Matrix4x4.Transpose(inverse)
			: transform;

		var vertices = mesh.Vertices;
		var positions = new Vector4[vertices.Length];
		for (var i = 0; i < vertices.Length; i++)
		{
			var position = Vector3.Transform(new Vector3(vertices[i].X, vertices[i].Y, vertices[i].Z), transform);
			positions[i] = new Vector4(position, 1.0f);
		}

		var sourceNormals = mesh.Normals;
		var normals = new Vector3[sourceNormals.Length];
		for (var i = 0; i < sourceNormals.Length; i++)
		{
			normals[i] = Vector3.Normalize(Vector3.TransformNormal(sourceNormals[i], normalTransform));
		}

		var sourceTangents = mesh.Tangents;
		var tangents = new Vector4[sourceTangents.Length];
		for (var i = 0; i < sourceTangents.Length; i++)
		{
			var tangent = Vector3.TransformNormal(
				new Vector3(sourceTangents[i].X, sourceTangents[i].Y, sourceTangents[i].Z),
				transform);
			tangents[i] = new Vector4(Vector3.Normalize(tangent), sourceTangents[i].W);
		}

		return new Mesh(
			positions,
			mesh.Indices,
			normals,
			mesh.UVs,
			tangents,
			null,
			null,
			mesh.ShadowOpaqueIndices,
			mesh.ShadowAlphaTestIndices);
	}
}
