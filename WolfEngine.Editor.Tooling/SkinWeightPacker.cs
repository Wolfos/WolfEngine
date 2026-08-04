using Silk.NET.Assimp;
using AssimpMesh = Silk.NET.Assimp.Mesh;
using EngineMesh = WolfEngine.Mesh;

namespace WolfEngine.Importing;

/// <summary>
/// Turns Assimp's bone-major weight lists into the vertex-major, fixed-width layout the skinning
/// compute shader reads.
/// </summary>
internal static class SkinWeightPacker
{
	private const int InfluencesPerVertex = EngineMesh.InfluencesPerVertex;

	internal static unsafe bool TryPack(
		AssimpMesh* mesh,
		IReadOnlyDictionary<string, int> boneIndicesByName,
		int vertexCount,
		string meshName,
		out uint[] boneIndices,
		out float[] boneWeights)
	{
		boneIndices = [];
		boneWeights = [];

		if (mesh is null || mesh->MNumBones == 0 || vertexCount == 0)
		{
			return false;
		}

		var influenceCounts = new int[vertexCount];
		var indices = new uint[vertexCount * InfluencesPerVertex];
		var weights = new float[vertexCount * InfluencesPerVertex];
		var droppedInfluences = 0;
		var unmappedBones = 0;

		for (var boneIndex = 0; boneIndex < mesh->MNumBones; boneIndex++)
		{
			var bone = mesh->MBones[boneIndex];
			if (bone is null)
			{
				continue;
			}

			if (boneIndicesByName.TryGetValue(bone->MName.AsString, out var skeletonBoneIndex) == false)
			{
				unmappedBones++;
				continue;
			}

			for (var weightIndex = 0; weightIndex < bone->MNumWeights; weightIndex++)
			{
				var weight = bone->MWeights[weightIndex];
				if (weight.MWeight <= 0.0f)
				{
					continue;
				}

				var vertexId = (int)weight.MVertexId;
				if (vertexId < 0 || vertexId >= vertexCount)
				{
					continue;
				}

				var slot = influenceCounts[vertexId];
				if (slot >= InfluencesPerVertex)
				{
					// LimitBoneWeights should have prevented this; count it rather than silently
					// deforming the mesh in a way nobody can trace back to import.
					droppedInfluences++;
					continue;
				}

				var offset = (vertexId * InfluencesPerVertex) + slot;
				indices[offset] = (uint)skeletonBoneIndex;
				weights[offset] = weight.MWeight;
				influenceCounts[vertexId] = slot + 1;
			}
		}

		var unweightedVertices = NormalizeWeights(weights, influenceCounts, vertexCount);

		if (unmappedBones > 0)
		{
			Console.Out.WriteLine(
				$"Mesh '{meshName}': {unmappedBones} bone(s) had no matching skeleton node and were ignored.");
		}

		if (droppedInfluences > 0)
		{
			Console.Out.WriteLine(
				$"Mesh '{meshName}': dropped {droppedInfluences} bone influence(s) beyond {InfluencesPerVertex} per vertex.");
		}

		if (unweightedVertices == vertexCount)
		{
			// Every bone was unmapped, so treating this as skinned would collapse the mesh onto bone 0.
			return false;
		}

		if (unweightedVertices > 0)
		{
			Console.Out.WriteLine(
				$"Mesh '{meshName}': {unweightedVertices} vertex/vertices had no bone influence and were bound rigidly to bone 0.");
		}

		boneIndices = indices;
		boneWeights = weights;
		return true;
	}

	/// <summary>
	/// Rescales each vertex's influences to sum to one and returns how many vertices had no
	/// influence at all. An unweighted vertex is pinned to bone 0 rather than left at zero weight,
	/// which would collapse it to the origin.
	/// </summary>
	private static int NormalizeWeights(float[] weights, int[] influenceCounts, int vertexCount)
	{
		var unweightedVertices = 0;
		for (var vertexId = 0; vertexId < vertexCount; vertexId++)
		{
			var offset = vertexId * InfluencesPerVertex;
			var total = 0.0f;
			for (var i = 0; i < InfluencesPerVertex; i++)
			{
				total += weights[offset + i];
			}

			if (total <= 0.0f)
			{
				weights[offset] = 1.0f;
				unweightedVertices++;
				continue;
			}

			if (influenceCounts[vertexId] == 0)
			{
				continue;
			}

			var scale = 1.0f / total;
			for (var i = 0; i < InfluencesPerVertex; i++)
			{
				weights[offset + i] *= scale;
			}
		}

		return unweightedVertices;
	}
}
