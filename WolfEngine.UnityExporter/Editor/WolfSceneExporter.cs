using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WolfEngine.UnityExporter
{
	/// <summary>
	/// Exports the open scene's terrains, terrain trees, prefab placements, loose mesh objects, lights and cameras to a
	/// folder the WolfEngine editor imports. Everything stays in Unity's own space (left-handed, Y up, metres) and is
	/// referenced by Unity asset path and GUID; the importer converts the space and maps assets in one place.
	/// </summary>
	/// <remarks>
	/// Folder contents: <c>scene.json</c>, and per terrain <c>terrain_N_heights.r16</c> (heightmap resolution squared
	/// little-endian 16-bit heights, row-major with rows along Unity Z) and <c>terrain_N_alphamaps.u8</c> (one plane per
	/// terrain layer of alphamap resolution squared bytes, same row order) and <c>terrain_N_details.u16</c> (one plane per
	/// detail prototype of detail resolution squared little-endian 16-bit values, as <c>GetDetailLayer</c> returns them,
	/// same row order). WolfEngine grows detail from terrain layers, so the importer reads the detail maps only to fit
	/// each layer's density.
	/// </remarks>
	public static class WolfSceneExporter
	{
		public const int FormatVersion = 1;

		private const string SceneArgument = "-wolfScene";
		private const string OutputArgument = "-wolfOutput";

		[MenuItem("Tools/WolfEngine/Export Open Scene...")]
		private static void ExportFromMenu()
		{
			var folder = EditorUtility.SaveFolderPanel("Export scene for WolfEngine", "", SceneManager.GetActiveScene().name);
			if (string.IsNullOrEmpty(folder)) return;

			var summary = Export(SceneManager.GetActiveScene(), folder);
			EditorUtility.DisplayDialog("WolfEngine export", summary, "OK");
		}

		/// <summary>
		/// Batch-mode entry point: <c>-executeMethod WolfEngine.UnityExporter.WolfSceneExporter.ExportFromCommandLine
		/// -wolfScene Assets/Path/Scene.unity -wolfOutput /absolute/folder</c>.
		/// </summary>
		public static void ExportFromCommandLine()
		{
			try
			{
				var arguments = Environment.GetCommandLineArgs();
				var scenePath = GetArgument(arguments, SceneArgument);
				var outputFolder = GetArgument(arguments, OutputArgument);
				var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
				var summary = Export(scene, outputFolder);
				Debug.Log("WolfEngine export: " + summary);
				EditorApplication.Exit(0);
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
				EditorApplication.Exit(1);
			}
		}

		public static string Export(Scene scene, string outputFolder)
		{
			if (scene.IsValid() == false || scene.isLoaded == false)
			{
				throw new InvalidOperationException("The scene to export is not loaded.");
			}

			Directory.CreateDirectory(outputFolder);
			var collector = new SceneCollector();
			foreach (var root in scene.GetRootGameObjects())
			{
				collector.Visit(root);
			}

			var json = new ExportJsonWriter();
			json.BeginObject();
			json.Write("formatVersion", FormatVersion);
			json.Write("unityVersion", Application.unityVersion);
			json.Write("scenePath", scene.path);
			json.Write("coordinateSystem", "unity: left-handed, +Y up, +Z forward, metres");

			json.BeginArray("terrains");
			for (var i = 0; i < collector.Terrains.Count; i++)
			{
				WriteTerrain(json, collector.Terrains[i], i, outputFolder);
			}

			json.EndArray();

			json.BeginArray("prefabInstances");
			foreach (var instance in collector.PrefabInstances)
			{
				WritePrefabInstance(json, instance);
			}

			json.EndArray();

			json.BeginArray("meshObjects");
			foreach (var meshObject in collector.MeshObjects)
			{
				WriteMeshObject(json, meshObject);
			}

			json.EndArray();

			json.BeginArray("lights");
			foreach (var light in collector.Lights)
			{
				WriteLight(json, light);
			}

			json.EndArray();

			json.BeginArray("cameras");
			foreach (var camera in collector.Cameras)
			{
				WriteCamera(json, camera);
			}

			json.EndArray();

			// The prefabs the scene places, as terrain trees or as instances, with their materials and meshes.
			var prefabs = new PrefabAssetExporter();
			var prefabPaths = collector.Terrains
				.SelectMany(terrain => terrain.terrainData.treePrototypes)
				.Where(prototype => prototype.prefab != null)
				.Select(prototype => AssetDatabase.GetAssetPath(prototype.prefab))
				.Concat(collector.Terrains
					.SelectMany(terrain => terrain.terrainData.detailPrototypes)
					.Where(prototype => prototype.usePrototypeMesh && prototype.prototype != null)
					.Select(prototype => AssetDatabase.GetAssetPath(prototype.prototype)))
				.Concat(collector.PrefabInstances.Select(instance => instance.PrefabPath))
				.Where(path => string.IsNullOrEmpty(path) == false);
			prefabs.WritePrefabs(json, prefabPaths);
			prefabs.WriteMaterials(json);
			prefabs.WriteMeshes(json, outputFolder);
			json.WriteStrings("warnings", prefabs.Warnings);

			json.BeginObject("skipped");
			json.Write("inactiveObjects", collector.InactiveObjectCount);
			json.Write("prefabInstancesWithOverrides", collector.PrefabInstances.Count(instance => instance.OverrideCount > 0));
			json.EndObject();
			json.EndObject();
			json.Save(Path.Combine(outputFolder, "scene.json"));

			var treeCount = collector.Terrains.Sum(terrain => terrain.terrainData.treeInstanceCount);

			return $"{collector.Terrains.Count} terrains, {treeCount} trees, {collector.PrefabInstances.Count} prefab instances, " +
			       $"{prefabs.MeshCount} meshes, {prefabs.MaterialCount} materials, {prefabs.Warnings.Count} warnings, " +
			       $"{collector.MeshObjects.Count} mesh objects, {collector.Lights.Count} lights, {collector.Cameras.Count} cameras, " +
			       $"{collector.InactiveObjectCount} inactive objects skipped, written to {outputFolder}";
		}

		private static void WriteTerrain(ExportJsonWriter json, Terrain terrain, int index, string outputFolder)
		{
			var data = terrain.terrainData;
			var heightsFile = $"terrain_{index}_heights.r16";
			var alphamapFile = $"terrain_{index}_alphamaps.u8";
			var detailsFile = $"terrain_{index}_details.u16";
			WriteHeights(data, Path.Combine(outputFolder, heightsFile));
			WriteAlphamaps(data, Path.Combine(outputFolder, alphamapFile));
			WriteDetails(data, Path.Combine(outputFolder, detailsFile));

			json.BeginObject();
			json.Write("name", terrain.name);
			json.Write("terrainDataPath", AssetDatabase.GetAssetPath(data));
			json.Write("position", terrain.transform.position);
			json.Write("size", data.size);
			json.Write("heightmapResolution", data.heightmapResolution);
			json.Write("heightsFile", heightsFile);
			json.Write("alphamapResolution", data.alphamapResolution);
			json.Write("alphamapFile", alphamapFile);
			json.Write("treeDistance", terrain.treeDistance);
			json.Write("treeBillboardDistance", terrain.treeBillboardDistance);

			json.BeginArray("layers");
			foreach (var layer in data.terrainLayers)
			{
				WriteTerrainLayer(json, layer);
			}

			json.EndArray();

			json.Write("detailResolution", data.detailResolution);
			json.Write("detailResolutionPerPatch", data.detailResolutionPerPatch);
			json.Write("detailScatterMode", data.detailScatterMode.ToString());
			json.Write("detailsFile", detailsFile);
			json.Write("detailObjectDistance", terrain.detailObjectDistance);
			json.Write("detailObjectDensity", terrain.detailObjectDensity);

			json.BeginArray("detailPrototypes");
			foreach (var prototype in data.detailPrototypes)
			{
				WriteDetailPrototype(json, prototype);
			}

			json.EndArray();

			json.BeginArray("treePrototypes");
			foreach (var prototype in data.treePrototypes)
			{
				json.BeginObject();
				WriteAssetReference(json, "prefab", prototype.prefab);
				json.Write("bendFactor", prototype.bendFactor);
				json.EndObject();
			}

			json.EndArray();

			// Tree positions are normalised to the terrain: 0 to 1 across its size on each axis, from its corner.
			json.BeginArray("trees");
			foreach (var tree in data.treeInstances)
			{
				json.BeginObject();
				json.Write("prototype", tree.prototypeIndex);
				json.Write("position", tree.position);
				json.Write("rotation", tree.rotation);
				json.Write("widthScale", tree.widthScale);
				json.Write("heightScale", tree.heightScale);
				json.EndObject();
			}

			json.EndArray();
			json.EndObject();
		}

		private static void WriteDetailPrototype(ExportJsonWriter json, DetailPrototype prototype)
		{
			json.BeginObject();
			json.Write("usePrototypeMesh", prototype.usePrototypeMesh);
			WriteAssetReference(json, "prefab", prototype.prototype);
			WriteAssetReference(json, "texture", prototype.prototypeTexture);
			json.Write("renderMode", prototype.renderMode.ToString());
			json.Write("minWidth", prototype.minWidth);
			json.Write("maxWidth", prototype.maxWidth);
			json.Write("minHeight", prototype.minHeight);
			json.Write("maxHeight", prototype.maxHeight);
			json.Write("noiseSeed", prototype.noiseSeed);
			json.Write("noiseSpread", prototype.noiseSpread);
			json.Write("density", prototype.density);
			json.Write("alignToGround", prototype.alignToGround);
			json.Write("positionJitter", prototype.positionJitter);
			json.Write("useDensityScaling", prototype.useDensityScaling);
			json.Write("holeEdgePadding", prototype.holeEdgePadding);
			json.Write("healthyColor", prototype.healthyColor);
			json.Write("dryColor", prototype.dryColor);
			json.EndObject();
		}

		private static void WriteDetails(TerrainData data, string path)
		{
			var resolution = data.detailResolution;
			var prototypeCount = data.detailPrototypes.Length;
			var planeSize = resolution * resolution;
			var bytes = new byte[planeSize * prototypeCount * 2];
			for (var prototype = 0; prototype < prototypeCount; prototype++)
			{
				var layer = data.GetDetailLayer(0, 0, resolution, resolution, prototype);
				for (var z = 0; z < resolution; z++)
				{
					for (var x = 0; x < resolution; x++)
					{
						var value = (ushort)Mathf.Clamp(layer[z, x], 0, ushort.MaxValue);
						var offset = ((prototype * planeSize) + (z * resolution) + x) * 2;
						bytes[offset] = (byte)(value & 0xff);
						bytes[offset + 1] = (byte)(value >> 8);
					}
				}
			}

			File.WriteAllBytes(path, bytes);
		}

		private static void WriteHeights(TerrainData data, string path)
		{
			var resolution = data.heightmapResolution;
			var heights = data.GetHeights(0, 0, resolution, resolution);
			var bytes = new byte[resolution * resolution * 2];
			for (var z = 0; z < resolution; z++)
			{
				for (var x = 0; x < resolution; x++)
				{
					var encoded = (ushort)Mathf.RoundToInt(Mathf.Clamp01(heights[z, x]) * 65535.0f);
					var offset = ((z * resolution) + x) * 2;
					bytes[offset] = (byte)(encoded & 0xff);
					bytes[offset + 1] = (byte)(encoded >> 8);
				}
			}

			File.WriteAllBytes(path, bytes);
		}

		private static void WriteAlphamaps(TerrainData data, string path)
		{
			var resolution = data.alphamapResolution;
			var layerCount = data.alphamapLayers;
			var weights = data.GetAlphamaps(0, 0, resolution, resolution);
			var planeSize = resolution * resolution;
			var bytes = new byte[planeSize * layerCount];
			for (var layer = 0; layer < layerCount; layer++)
			{
				for (var z = 0; z < resolution; z++)
				{
					for (var x = 0; x < resolution; x++)
					{
						bytes[(layer * planeSize) + (z * resolution) + x] = (byte)Mathf.RoundToInt(Mathf.Clamp01(weights[z, x, layer]) * 255.0f);
					}
				}
			}

			File.WriteAllBytes(path, bytes);
		}

		private static void WriteTerrainLayer(ExportJsonWriter json, TerrainLayer layer)
		{
			json.BeginObject();
			if (layer == null)
			{
				json.Write("missing", true);
				json.EndObject();
				return;
			}

			json.Write("name", layer.name);
			WriteAssetReference(json, "asset", layer);
			WriteAssetReference(json, "diffuseTexture", layer.diffuseTexture);
			WriteAssetReference(json, "normalTexture", layer.normalMapTexture);
			WriteAssetReference(json, "maskTexture", layer.maskMapTexture);
			json.Write("tileSize", layer.tileSize);
			json.Write("tileOffset", layer.tileOffset);
			json.Write("normalScale", layer.normalScale);
			json.Write("metallic", layer.metallic);
			json.Write("smoothness", layer.smoothness);
			json.Write("specular", layer.specular);
			json.Write("diffuseRemapMin", layer.diffuseRemapMin);
			json.Write("diffuseRemapMax", layer.diffuseRemapMax);
			json.Write("maskMapRemapMin", layer.maskMapRemapMin);
			json.Write("maskMapRemapMax", layer.maskMapRemapMax);
			json.EndObject();
		}

		private static void WritePrefabInstance(ExportJsonWriter json, PrefabPlacement placement)
		{
			var transform = placement.Root.transform;
			json.BeginObject();
			json.Write("name", placement.Root.name);
			json.Write("prefabPath", placement.PrefabPath);
			json.Write("prefabGuid", AssetDatabase.AssetPathToGUID(placement.PrefabPath));
			WriteTransform(json, transform);
			json.Write("overrides", placement.OverrideCount);
			if (placement.OverrideKinds.Count > 0)
			{
				json.WriteStrings("overrideKinds", placement.OverrideKinds);
			}

			json.EndObject();
		}

		private static void WriteMeshObject(ExportJsonWriter json, MeshRenderer renderer)
		{
			var filter = renderer.GetComponent<MeshFilter>();
			json.BeginObject();
			json.Write("name", renderer.name);
			WriteAssetReference(json, "mesh", filter.sharedMesh);
			json.Write("meshName", filter.sharedMesh != null ? filter.sharedMesh.name : null);
			json.WriteStrings("materials", renderer.sharedMaterials.Select(material => material != null ? AssetDatabase.GetAssetPath(material) : null).ToArray());
			WriteTransform(json, renderer.transform);
			json.EndObject();
		}

		private static void WriteLight(ExportJsonWriter json, Light light)
		{
			json.BeginObject();
			json.Write("name", light.name);
			json.Write("type", light.type.ToString());
			WriteTransform(json, light.transform);
			json.Write("color", light.color);
			json.Write("intensity", light.intensity);
			json.Write("useColorTemperature", light.useColorTemperature);
			json.Write("colorTemperature", light.colorTemperature);
			json.Write("range", light.range);
			json.Write("spotAngle", light.spotAngle);
			json.Write("shadows", light.shadows.ToString());
			json.EndObject();
		}

		private static void WriteCamera(ExportJsonWriter json, Camera camera)
		{
			json.BeginObject();
			json.Write("name", camera.name);
			json.Write("isMain", camera.CompareTag("MainCamera"));
			WriteTransform(json, camera.transform);
			json.Write("fieldOfView", camera.fieldOfView);
			json.Write("nearClip", camera.nearClipPlane);
			json.Write("farClip", camera.farClipPlane);
			json.EndObject();
		}

		private static void WriteTransform(ExportJsonWriter json, Transform transform)
		{
			json.Write("position", transform.position);
			json.Write("rotation", transform.rotation);
			json.Write("scale", transform.lossyScale);
		}

		private static void WriteAssetReference(ExportJsonWriter json, string name, UnityEngine.Object asset)
		{
			var path = asset != null ? AssetDatabase.GetAssetPath(asset) : null;
			json.Write(name, string.IsNullOrEmpty(path) ? null : path);
		}

		private static string GetArgument(string[] arguments, string name)
		{
			var index = Array.IndexOf(arguments, name);
			if (index < 0 || index + 1 >= arguments.Length)
			{
				throw new ArgumentException($"Missing command-line argument {name} <value>.");
			}

			return arguments[index + 1];
		}

		private sealed class PrefabPlacement
		{
			public GameObject Root;
			public string PrefabPath;
			public int OverrideCount;
			public List<string> OverrideKinds = new List<string>();
		}

		/// <summary>
		/// Walks the hierarchy. An outermost prefab instance is exported as a placement and not walked further, since its
		/// contents come from the prefab; overrides to it other than its root transform are counted and named so the
		/// import can report what a placement alone loses.
		/// </summary>
		private sealed class SceneCollector
		{
			public readonly List<Terrain> Terrains = new List<Terrain>();
			public readonly List<PrefabPlacement> PrefabInstances = new List<PrefabPlacement>();
			public readonly List<MeshRenderer> MeshObjects = new List<MeshRenderer>();
			public readonly List<Light> Lights = new List<Light>();
			public readonly List<Camera> Cameras = new List<Camera>();
			public int InactiveObjectCount;

			public void Visit(GameObject gameObject)
			{
				if (gameObject.activeInHierarchy == false)
				{
					InactiveObjectCount++;
					return;
				}

				CollectComponents(gameObject);
				if (PrefabUtility.IsOutermostPrefabInstanceRoot(gameObject))
				{
					PrefabInstances.Add(CreatePlacement(gameObject));
					return;
				}

				foreach (Transform child in gameObject.transform)
				{
					Visit(child.gameObject);
				}
			}

			private void CollectComponents(GameObject gameObject)
			{
				if (gameObject.TryGetComponent<Terrain>(out var terrain) && terrain.enabled && terrain.terrainData != null)
				{
					Terrains.Add(terrain);
				}

				if (gameObject.TryGetComponent<Light>(out var light) && light.enabled)
				{
					Lights.Add(light);
				}

				if (gameObject.TryGetComponent<Camera>(out var camera) && camera.enabled)
				{
					Cameras.Add(camera);
				}

				if (PrefabUtility.IsPartOfPrefabInstance(gameObject) == false &&
				    gameObject.TryGetComponent<MeshRenderer>(out var renderer) &&
				    renderer.enabled &&
				    gameObject.TryGetComponent<MeshFilter>(out _))
				{
					MeshObjects.Add(renderer);
				}
			}

			private static PrefabPlacement CreatePlacement(GameObject root)
			{
				var placement = new PrefabPlacement
				{
					Root = root,
					PrefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root)
				};
				var kinds = new HashSet<string>();
				foreach (var objectOverride in PrefabUtility.GetObjectOverrides(root, false))
				{
					var instanceObject = objectOverride.instanceObject;
					if (instanceObject is Transform transform && transform.gameObject == root)
					{
						continue;
					}

					if (instanceObject is GameObject gameObject && gameObject == root)
					{
						continue;
					}

					placement.OverrideCount++;
					kinds.Add("modified " + instanceObject.GetType().Name);
				}

				foreach (var added in PrefabUtility.GetAddedComponents(root))
				{
					placement.OverrideCount++;
					kinds.Add("added " + added.instanceComponent.GetType().Name);
				}

				foreach (var removed in PrefabUtility.GetRemovedComponents(root))
				{
					placement.OverrideCount++;
					kinds.Add("removed " + removed.assetComponent.GetType().Name);
				}

				foreach (var added in PrefabUtility.GetAddedGameObjects(root))
				{
					placement.OverrideCount++;
					kinds.Add("added GameObject");
				}

				placement.OverrideKinds.AddRange(kinds.OrderBy(kind => kind));

				return placement;
			}
		}
	}
}
