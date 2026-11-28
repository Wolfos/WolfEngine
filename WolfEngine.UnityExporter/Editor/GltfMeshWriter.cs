using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace WolfEngine.UnityExporter
{
	/// <summary>
	/// Writes a Unity mesh as a glTF 2.0 file with one glTF mesh per submesh, named <c>sub0</c>, <c>sub1</c> and so on.
	/// </summary>
	/// <remarks>
	/// glTF is right-handed, so Z is negated (normals and tangents too, with the tangent's handedness flipped) and the
	/// triangle order is kept, which turns Unity's clockwise front faces into glTF's counter-clockwise ones. V is flipped
	/// because glTF's texture origin is the top left. An importer that converts glTF to a left-handed space by negating
	/// Z, as WolfEngine's does, therefore gets Unity's vertices back exactly, so Unity transforms apply to them unchanged.
	/// </remarks>
	internal static class GltfMeshWriter
	{
		private const int ArrayBufferTarget = 34962;
		private const int ElementArrayBufferTarget = 34963;
		private const int FloatComponent = 5126;
		private const int UnsignedIntComponent = 5125;

		public static void Write(Mesh mesh, string gltfPath)
		{
			var positions = mesh.vertices;
			var normals = mesh.normals;
			var tangents = mesh.tangents;
			var uvs = mesh.uv;
			var hasNormals = normals.Length == positions.Length;
			var hasTangents = tangents.Length == positions.Length;
			var hasUvs = uvs.Length == positions.Length;

			var binary = new MemoryStream();
			var bufferViews = new StringBuilder();
			var accessors = new StringBuilder();
			var meshes = new StringBuilder();
			var nodes = new StringBuilder();
			var accessorCount = 0;
			var bufferViewCount = 0;
			var meshCount = 0;

			for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
			{
				var sourceIndices = mesh.GetIndices(submesh);
				if (mesh.GetTopology(submesh) != MeshTopology.Triangles || sourceIndices.Length == 0)
				{
					continue;
				}

				// Each submesh gets its own compact vertex arrays, so the importer can make it a mesh of its own.
				var remap = new Dictionary<int, int>();
				var used = new List<int>();
				var indices = new uint[sourceIndices.Length];
				for (var i = 0; i < sourceIndices.Length; i++)
				{
					var source = sourceIndices[i];
					if (remap.TryGetValue(source, out var compact) == false)
					{
						compact = used.Count;
						remap.Add(source, compact);
						used.Add(source);
					}

					indices[i] = (uint)compact;
				}

				var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
				var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
				var positionData = new float[used.Count * 3];
				for (var i = 0; i < used.Count; i++)
				{
					var position = positions[used[i]];
					position.z = -position.z;
					min = Vector3.Min(min, position);
					max = Vector3.Max(max, position);
					positionData[(i * 3) + 0] = position.x;
					positionData[(i * 3) + 1] = position.y;
					positionData[(i * 3) + 2] = position.z;
				}

				var attributes = new StringBuilder();
				var positionAccessor = AddFloatAccessor(binary, bufferViews, accessors, ref bufferViewCount, ref accessorCount, positionData, used.Count, "VEC3", min, max);
				attributes.Append($"\"POSITION\": {positionAccessor}");

				if (hasNormals)
				{
					var data = new float[used.Count * 3];
					for (var i = 0; i < used.Count; i++)
					{
						var normal = normals[used[i]];
						data[(i * 3) + 0] = normal.x;
						data[(i * 3) + 1] = normal.y;
						data[(i * 3) + 2] = -normal.z;
					}

					var accessor = AddFloatAccessor(binary, bufferViews, accessors, ref bufferViewCount, ref accessorCount, data, used.Count, "VEC3", null, null);
					attributes.Append($", \"NORMAL\": {accessor}");
				}

				if (hasTangents)
				{
					var data = new float[used.Count * 4];
					for (var i = 0; i < used.Count; i++)
					{
						var tangent = tangents[used[i]];
						data[(i * 4) + 0] = tangent.x;
						data[(i * 4) + 1] = tangent.y;
						data[(i * 4) + 2] = -tangent.z;
						data[(i * 4) + 3] = tangent.w < 0.0f ? 1.0f : -1.0f;
					}

					var accessor = AddFloatAccessor(binary, bufferViews, accessors, ref bufferViewCount, ref accessorCount, data, used.Count, "VEC4", null, null);
					attributes.Append($", \"TANGENT\": {accessor}");
				}

				if (hasUvs)
				{
					var data = new float[used.Count * 2];
					for (var i = 0; i < used.Count; i++)
					{
						var uv = uvs[used[i]];
						data[(i * 2) + 0] = uv.x;
						data[(i * 2) + 1] = 1.0f - uv.y;
					}

					var accessor = AddFloatAccessor(binary, bufferViews, accessors, ref bufferViewCount, ref accessorCount, data, used.Count, "VEC2", null, null);
					attributes.Append($", \"TEXCOORD_0\": {accessor}");
				}

				var indexAccessor = AddIndexAccessor(binary, bufferViews, accessors, ref bufferViewCount, ref accessorCount, indices);
				AppendEntry(meshes, $"{{\"name\": \"sub{submesh}\", \"primitives\": [{{\"attributes\": {{{attributes}}}, \"indices\": {indexAccessor}, \"mode\": 4}}]}}");
				AppendEntry(nodes, $"{{\"name\": \"sub{submesh}\", \"mesh\": {meshCount}}}");
				meshCount++;
			}

			var sceneNodes = new StringBuilder();
			for (var i = 0; i < meshCount; i++)
			{
				if (i > 0)
				{
					sceneNodes.Append(", ");
				}

				sceneNodes.Append(i.ToString(CultureInfo.InvariantCulture));
			}

			var binPath = Path.ChangeExtension(gltfPath, ".bin");
			File.WriteAllBytes(binPath, binary.ToArray());
			var json = new StringBuilder();
			json.Append("{\n");
			json.Append("\t\"asset\": {\"version\": \"2.0\", \"generator\": \"WolfEngine.UnityExporter\"},\n");
			json.Append("\t\"scene\": 0,\n");
			json.Append($"\t\"scenes\": [{{\"nodes\": [{sceneNodes}]}}],\n");
			json.Append($"\t\"nodes\": [{nodes}],\n");
			json.Append($"\t\"meshes\": [{meshes}],\n");
			json.Append($"\t\"accessors\": [{accessors}],\n");
			json.Append($"\t\"bufferViews\": [{bufferViews}],\n");
			json.Append($"\t\"buffers\": [{{\"uri\": \"{Path.GetFileName(binPath)}\", \"byteLength\": {binary.Length}}}]\n");
			json.Append("}\n");
			File.WriteAllText(gltfPath, json.ToString(), new UTF8Encoding(false));
		}

		private static int AddFloatAccessor(
			MemoryStream binary,
			StringBuilder bufferViews,
			StringBuilder accessors,
			ref int bufferViewCount,
			ref int accessorCount,
			float[] data,
			int count,
			string type,
			Vector3? min,
			Vector3? max)
		{
			var offset = Align(binary);
			var bytes = new byte[data.Length * 4];
			Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
			binary.Write(bytes, 0, bytes.Length);
			AppendEntry(bufferViews, $"{{\"buffer\": 0, \"byteOffset\": {offset}, \"byteLength\": {bytes.Length}, \"target\": {ArrayBufferTarget}}}");

			var bounds = min.HasValue && max.HasValue
				? $", \"min\": [{Number(min.Value.x)}, {Number(min.Value.y)}, {Number(min.Value.z)}], \"max\": [{Number(max.Value.x)}, {Number(max.Value.y)}, {Number(max.Value.z)}]"
				: string.Empty;
			AppendEntry(accessors, $"{{\"bufferView\": {bufferViewCount}, \"componentType\": {FloatComponent}, \"count\": {count}, \"type\": \"{type}\"{bounds}}}");
			bufferViewCount++;

			return accessorCount++;
		}

		private static int AddIndexAccessor(
			MemoryStream binary,
			StringBuilder bufferViews,
			StringBuilder accessors,
			ref int bufferViewCount,
			ref int accessorCount,
			uint[] indices)
		{
			var offset = Align(binary);
			var bytes = new byte[indices.Length * 4];
			Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
			binary.Write(bytes, 0, bytes.Length);
			AppendEntry(bufferViews, $"{{\"buffer\": 0, \"byteOffset\": {offset}, \"byteLength\": {bytes.Length}, \"target\": {ElementArrayBufferTarget}}}");
			AppendEntry(accessors, $"{{\"bufferView\": {bufferViewCount}, \"componentType\": {UnsignedIntComponent}, \"count\": {indices.Length}, \"type\": \"SCALAR\"}}");
			bufferViewCount++;

			return accessorCount++;
		}

		private static long Align(MemoryStream binary)
		{
			while (binary.Length % 4 != 0)
			{
				binary.WriteByte(0);
			}

			return binary.Length;
		}

		private static void AppendEntry(StringBuilder builder, string entry)
		{
			if (builder.Length > 0)
			{
				builder.Append(",\n\t\t");
			}
			else
			{
				builder.Append("\n\t\t");
			}

			builder.Append(entry);
		}

		private static string Number(float value)
		{
			return value.ToString("R", CultureInfo.InvariantCulture);
		}
	}
}
