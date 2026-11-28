using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace WolfEngine.UnityExporter
{
	/// <summary>
	/// Exports the prefabs a scene uses, so they can be rebuilt in WolfEngine from Unity's own data rather than from
	/// guesses about its files: each prefab's hierarchy with local transforms, its mesh renderers and LOD group, every
	/// material those use with all of its shader properties, and every mesh as glTF (see <see cref="GltfMeshWriter"/>).
	/// </summary>
	internal sealed class PrefabAssetExporter
	{
		public const string MeshFolder = "meshes";

		private readonly Dictionary<string, Mesh> _meshes = new Dictionary<string, Mesh>();
		private readonly Dictionary<string, Material> _materials = new Dictionary<string, Material>();

		public int MeshCount => _meshes.Count;

		public int MaterialCount => _materials.Count;

		public List<string> Warnings { get; } = new List<string>();

		public void WritePrefabs(ExportJsonWriter json, IEnumerable<string> prefabPaths)
		{
			json.BeginArray("prefabs");
			foreach (var path in prefabPaths.Distinct().OrderBy(path => path))
			{
				var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
				if (prefab == null)
				{
					Warnings.Add($"Prefab '{path}' could not be loaded.");
					continue;
				}

				WritePrefab(json, path, prefab);
			}

			json.EndArray();
		}

		public void WriteMaterials(ExportJsonWriter json)
		{
			json.BeginArray("materials");
			foreach (var entry in _materials.OrderBy(entry => entry.Key))
			{
				WriteMaterial(json, entry.Key, entry.Value);
			}

			json.EndArray();
		}

		public void WriteMeshes(ExportJsonWriter json, string outputFolder)
		{
			var meshFolder = Path.Combine(outputFolder, MeshFolder);
			Directory.CreateDirectory(meshFolder);
			json.BeginArray("meshes");
			foreach (var entry in _meshes.OrderBy(entry => entry.Key))
			{
				var mesh = entry.Value;
				var file = $"{MeshFolder}/{entry.Key}.gltf";
				GltfMeshWriter.Write(mesh, Path.Combine(outputFolder, file));
				json.BeginObject();
				json.Write("key", entry.Key);
				json.Write("name", mesh.name);
				json.Write("assetPath", AssetDatabase.GetAssetPath(mesh));
				json.Write("file", file);
				json.Write("subMeshCount", mesh.subMeshCount);
				json.Write("vertexCount", mesh.vertexCount);
				json.EndObject();
			}

			json.EndArray();
		}

		private void WritePrefab(ExportJsonWriter json, string path, GameObject prefab)
		{
			var transforms = new List<Transform>();
			Collect(prefab.transform, transforms);
			var indices = new Dictionary<Transform, int>();
			for (var i = 0; i < transforms.Count; i++)
			{
				indices.Add(transforms[i], i);
			}

			json.BeginObject();
			json.Write("path", path);
			json.Write("guid", AssetDatabase.AssetPathToGUID(path));
			json.BeginArray("nodes");
			foreach (var transform in transforms)
			{
				WriteNode(json, transform, indices);
			}

			json.EndArray();

			var group = prefab.GetComponentInChildren<LODGroup>(true);
			if (group != null)
			{
				WriteLodGroup(json, group, indices);
			}

			json.EndObject();
		}

		private static void Collect(Transform transform, List<Transform> transforms)
		{
			transforms.Add(transform);
			foreach (Transform child in transform)
			{
				Collect(child, transforms);
			}
		}

		private void WriteNode(ExportJsonWriter json, Transform transform, Dictionary<Transform, int> indices)
		{
			var gameObject = transform.gameObject;
			json.BeginObject();
			json.Write("name", gameObject.name);
			json.Write("parent", transform.parent != null && indices.TryGetValue(transform.parent, out var parent) ? parent : -1);
			json.Write("active", gameObject.activeSelf);
			json.Write("position", transform.localPosition);
			json.Write("rotation", transform.localRotation);
			json.Write("scale", transform.localScale);

			if (gameObject.TryGetComponent<MeshFilter>(out var filter) &&
			    gameObject.TryGetComponent<MeshRenderer>(out var renderer) &&
			    filter.sharedMesh != null)
			{
				json.BeginObject("renderer");
				json.Write("enabled", renderer.enabled);
				json.Write("mesh", RegisterMesh(filter.sharedMesh));
				json.WriteStrings("materials", renderer.sharedMaterials.Select(RegisterMaterial).ToArray());
				json.Write("castShadows", renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off);
				json.EndObject();
			}

			var others = gameObject.GetComponents<Component>()
				.Where(component => component != null &&
				                    (component is Transform || component is MeshFilter || component is MeshRenderer || component is LODGroup) == false)
				.Select(component => component.GetType().Name)
				.Distinct()
				.ToArray();
			if (others.Length > 0)
			{
				json.WriteStrings("otherComponents", others);
			}

			json.EndObject();
		}

		/// <summary>Unity measures LOD thresholds on the group's size: the largest axis of its bounds, around its reference point.</summary>
		private static void WriteLodGroup(ExportJsonWriter json, LODGroup group, Dictionary<Transform, int> indices)
		{
			json.BeginObject("lodGroup");
			json.Write("node", indices[group.transform]);
			json.Write("localReferencePoint", group.localReferencePoint);
			json.Write("size", group.size);
			json.BeginArray("lods");
			foreach (var lod in group.GetLODs())
			{
				json.BeginObject();
				json.Write("screenRelativeTransitionHeight", lod.screenRelativeTransitionHeight);
				var nodes = lod.renderers
					.Where(renderer => renderer != null && indices.ContainsKey(renderer.transform))
					.Select(renderer => indices[renderer.transform])
					.ToArray();
				json.WriteInts("rendererNodes", nodes);
				json.EndObject();
			}

			json.EndArray();
			json.EndObject();
		}

		private string RegisterMesh(Mesh mesh)
		{
			var key = GetKey(mesh, mesh.name);
			if (_meshes.ContainsKey(key) == false)
			{
				_meshes.Add(key, mesh);
			}

			return key;
		}

		private string RegisterMaterial(Material material)
		{
			if (material == null) return null;

			var path = AssetDatabase.GetAssetPath(material);
			var key = string.IsNullOrEmpty(path) ? "builtin:" + material.name : path;
			if (_materials.ContainsKey(key) == false)
			{
				_materials.Add(key, material);
			}

			return key;
		}

		/// <summary>A file-safe key unique to the asset: its name, its asset's GUID and its id within that asset.</summary>
		private static string GetKey(Object asset, string name)
		{
			var safeName = new string(name.Select(character => char.IsLetterOrDigit(character) || character == '_' || character == '-' ? character : '_').ToArray());
			if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out var guid, out long localId))
			{
				return $"{safeName}_{guid.Substring(0, 8)}_{localId}";
			}

			return $"{safeName}_{asset.GetInstanceID()}";
		}

		private static void WriteMaterial(ExportJsonWriter json, string key, Material material)
		{
			var shader = material.shader;
			json.BeginObject();
			json.Write("key", key);
			json.Write("name", material.name);
			json.Write("shader", shader != null ? shader.name : null);
			json.Write("renderQueue", material.renderQueue);
			json.WriteStrings("keywords", material.shaderKeywords);

			json.BeginObject("floats");
			ForEachProperty(material, (name, type) =>
			{
				if (type == UnityEngine.Rendering.ShaderPropertyType.Float || type == UnityEngine.Rendering.ShaderPropertyType.Range)
				{
					json.Write(name, material.GetFloat(name));
				}
			});
			json.EndObject();

			json.BeginObject("colors");
			ForEachProperty(material, (name, type) =>
			{
				if (type == UnityEngine.Rendering.ShaderPropertyType.Color)
				{
					json.Write(name, material.GetColor(name));
				}
			});
			json.EndObject();

			json.BeginObject("vectors");
			ForEachProperty(material, (name, type) =>
			{
				if (type == UnityEngine.Rendering.ShaderPropertyType.Vector)
				{
					json.Write(name, material.GetVector(name));
				}
			});
			json.EndObject();

			json.BeginObject("textures");
			ForEachProperty(material, (name, type) =>
			{
				if (type != UnityEngine.Rendering.ShaderPropertyType.Texture)
				{
					return;
				}

				var texture = material.GetTexture(name);
				var path = texture != null ? AssetDatabase.GetAssetPath(texture) : null;
				if (string.IsNullOrEmpty(path))
				{
					return;
				}

				json.BeginObject(name);
				json.Write("path", path);
				json.Write("scale", material.GetTextureScale(name));
				json.Write("offset", material.GetTextureOffset(name));
				json.Write("isNormalMap", AssetImporter.GetAtPath(path) is TextureImporter importer && importer.textureType == TextureImporterType.NormalMap);
				json.EndObject();
			});
			json.EndObject();
			json.EndObject();
		}

		private static void ForEachProperty(Material material, System.Action<string, UnityEngine.Rendering.ShaderPropertyType> visit)
		{
			var shader = material.shader;
			if (shader == null) return;

			var count = shader.GetPropertyCount();
			for (var i = 0; i < count; i++)
			{
				var name = shader.GetPropertyName(i);
				if (material.HasProperty(name))
				{
					visit(name, shader.GetPropertyType(i));
				}
			}
		}
	}
}
