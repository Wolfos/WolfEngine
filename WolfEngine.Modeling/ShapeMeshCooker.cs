using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;

namespace WolfEngine.Modeling;

/// <summary>Turns a compiled <see cref="ShapeMesh"/> into the engine's mesh data, for the importer and the preview alike.</summary>
public static class ShapeMeshCooker
{
	/// <summary>The cooked form the importer writes to <c>Library/</c>.</summary>
	public static ImportedMeshAssetFile ToImportedMesh(ShapeMesh mesh)
	{
		ArgumentNullException.ThrowIfNull(mesh);
		if (mesh.IsEmpty)
		{
			throw new ArgumentException("An empty mesh cannot be cooked.", nameof(mesh));
		}

		var vertices = new Vector4[mesh.VertexCount];
		var tangents = new Vector4[mesh.VertexCount];
		for (var i = 0; i < mesh.VertexCount; i++)
		{
			vertices[i] = new Vector4(mesh.Positions[i], 1.0f);
			tangents[i] = GetTangent(mesh.Normals[i]);
		}

		return new ImportedMeshAssetFile
		{
			Vertices = vertices,
			Indices = mesh.Indices,
			ShadowOpaqueIndices = mesh.Indices,
			ShadowAlphaTestIndices = mesh.Indices,
			Normals = mesh.Normals,
			Tangents = tangents,

			// UVs are authored in a later milestone; until then every vertex maps to the texture origin.
			UVs = new Vector2[mesh.VertexCount],
			BoneIndices = [],
			BoneWeights = []
		};
	}

	/// <summary>A renderable mesh, for the editor preview.</summary>
	public static Mesh ToRenderMesh(ShapeMesh mesh)
	{
		var cooked = ToImportedMesh(mesh);
		return new Mesh(
			cooked.Vertices,
			cooked.Indices,
			cooked.Normals,
			cooked.UVs,
			cooked.Tangents,
			shadowOpaqueIndices: cooked.ShadowOpaqueIndices,
			shadowAlphaTestIndices: cooked.ShadowAlphaTestIndices);
	}

	/// <summary>
	/// Any unit vector perpendicular to the normal. Without UVs there is no texture direction to follow, so this only
	/// keeps the tangent frame valid for shaders that build one.
	/// </summary>
	private static Vector4 GetTangent(Vector3 normal)
	{
		var reference = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
		var tangent = Vector3.Cross(reference, normal);
		var length = tangent.Length();
		tangent = length > 1.0e-6f ? tangent / length : Vector3.UnitX;

		return new Vector4(tangent, 1.0f);
	}
}
