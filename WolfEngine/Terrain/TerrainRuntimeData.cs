using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;

namespace WolfEngine;

public sealed class TerrainRuntimeData
{
	private const int LODCount = 3;
	private readonly List<TerrainChunkRuntime> _chunks = new();
	private Texture? _resolvedHeightmap;
	private Texture? _resolvedControlMap;
	private TerrainLayerSet? _resolvedLayerSet;
	private Vector2 _resolvedWorldSize;
	private float _resolvedHeightScale;
	private int _resolvedChunkSize;
	private Guid _heightmapNodeId;
	private Guid _controlMapNodeId;
	private Guid _layerSetNodeId;
	private Vector2 _lastWorldSize;
	private float _lastHeightScale;
	private int _lastChunkSize;
	private int _lastHeightResourceRevision = -1;
	private int _lastControlResourceRevision = -1;
	private int _lastHeightWidth;
	private int _lastHeightHeight;
	private TextureFormat _lastHeightFormat;
	private byte[]? _lastHeightTopMipData;
	private int _lastControlWidth;
	private int _lastControlHeight;
	private TextureFormat _lastControlFormat;
	private byte[]? _lastControlTopMipData;
	private bool _built;
	private float[]? _heightSamples;
	private Vector3[]? _normals;

	public IReadOnlyList<TerrainChunkRuntime> Chunks => _chunks;
	public int HeightSampleWidth { get; private set; }
	public int HeightSampleHeight { get; private set; }
	public Vector2 ResolvedWorldSize => _resolvedWorldSize;
	public float ResolvedHeightScale => _resolvedHeightScale;
	public Vector2 SampleSpacing { get; private set; }
	public Box LocalBounds { get; private set; }
	public Mesh? CollisionMesh { get; private set; }
	public int RuntimeVersion { get; private set; }

	public bool EnsureBuilt(TerrainComponent component)
	{
		Resolve(component);
		if (NeedsRebuild(component) == false)
		{
			return _built;
		}

		_chunks.Clear();
		_built = false;
		_heightSamples = null;
		_normals = null;
		CollisionMesh = null;
		HeightSampleWidth = 0;
		HeightSampleHeight = 0;
		SampleSpacing = Vector2.Zero;
		LocalBounds = default;
		if (_resolvedHeightmap is null)
		{
			CaptureBuildState(component);
			return false;
		}

		var heightSamples = DecodeHeightSamples(_resolvedHeightmap, out var sampleWidth, out var sampleHeight);
		if (heightSamples is null || sampleWidth < 2 || sampleHeight < 2)
		{
			CaptureBuildState(component);
			return false;
		}

		HeightSampleWidth = sampleWidth;
		HeightSampleHeight = sampleHeight;
		SampleSpacing = new Vector2(
			_resolvedWorldSize.X / Math.Max(sampleWidth - 1, 1),
			_resolvedWorldSize.Y / Math.Max(sampleHeight - 1, 1));
		_heightSamples = heightSamples;
		_normals = BuildNormals(heightSamples, sampleWidth, sampleHeight, _resolvedWorldSize, _resolvedHeightScale);
		LocalBounds = new Box
		{
			Center = new Vector3(0.0f, _resolvedHeightScale * 0.5f, 0.0f),
			Size = new Vector3(_resolvedWorldSize.X, _resolvedHeightScale, _resolvedWorldSize.Y)
		};
		CollisionMesh = BuildCollisionMesh(heightSamples, _normals, sampleWidth, sampleHeight);
		BuildChunks(heightSamples, _normals, sampleWidth, sampleHeight);
		CaptureBuildState(component);
		_built = _chunks.Count > 0;
		if (_built)
		{
			RuntimeVersion++;
		}

		return _built;
	}

	public void CollectChunkDrawRecords(
		RenderGraph renderGraph,
		Material material,
		Vector3 cameraOrigin,
		Matrix4x4 worldTransform,
		List<TerrainChunkDrawRecord> destination)
	{
		ArgumentNullException.ThrowIfNull(renderGraph);
		ArgumentNullException.ThrowIfNull(material);
		ArgumentNullException.ThrowIfNull(destination);
		if (_built == false || _chunks.Count == 0)
		{
			return;
		}

		EnsureTerrainResources(renderGraph);
		var selectedLods = new int[_chunks.Count];
		for (var i = 0; i < _chunks.Count; i++)
		{
			selectedLods[i] = SelectLod(_chunks[i], worldTransform, cameraOrigin);
		}

		EnforceNeighborLodDelta(selectedLods);

		var layerSet = _resolvedLayerSet;
		var layerCount = layerSet?.ResolvedLayerCount ?? 1;
		var heightBlendSharpness = layerSet?.HeightBlendSharpness ?? 4.0f;
		var layers = ResolveLayers(layerSet);
		for (var i = 0; i < _chunks.Count; i++)
		{
			var chunk = _chunks[i];
			var mesh = chunk.LodMeshes[selectedLods[i]];
			if (mesh is null)
			{
				continue;
			}

			renderGraph.EnsureMeshResources(mesh);
			destination.Add(new TerrainChunkDrawRecord(
				i,
				mesh,
				material,
				worldTransform,
				new TerrainDrawSurface(
					_resolvedControlMap,
					layerCount,
					heightBlendSharpness,
					layers)));
		}
	}

	public bool TrySampleHeight(Matrix4x4 localToWorld, Vector3 worldPosition, out float height)
	{
		if (TrySampleSurface(localToWorld, worldPosition, out var surfacePoint, out _))
		{
			height = surfacePoint.Y;
			return true;
		}

		height = 0.0f;
		return false;
	}

	public bool TrySampleNormal(Matrix4x4 localToWorld, Vector3 worldPosition, out Vector3 normal)
	{
		if (TrySampleSurface(localToWorld, worldPosition, out _, out normal))
		{
			return true;
		}

		normal = Vector3.UnitY;
		return false;
	}

	public bool TrySampleSurface(Matrix4x4 localToWorld, Vector3 worldPosition, out Vector3 surfacePoint, out Vector3 surfaceNormal)
	{
		surfacePoint = Vector3.Zero;
		surfaceNormal = Vector3.UnitY;
		if (TryGetInverseTransform(localToWorld, out var worldToLocal) == false)
		{
			return false;
		}

		var localPosition = Vector3.Transform(worldPosition, worldToLocal);
		if (TrySampleLocalSurface(localPosition.X, localPosition.Z, out var localSurfacePoint, out var localSurfaceNormal) == false)
		{
			return false;
		}

		surfacePoint = Vector3.Transform(localSurfacePoint, localToWorld);
		surfaceNormal = TransformNormal(localSurfaceNormal, localToWorld, worldToLocal);
		return true;
	}

	public bool TryRaycast(Matrix4x4 localToWorld, Vector3 origin, Vector3 direction, out TerrainRaycastHit hit)
	{
		hit = default;
		if (_built == false ||
		    _heightSamples is null ||
		    HeightSampleWidth < 2 ||
		    HeightSampleHeight < 2 ||
		    direction.LengthSquared() <= 1e-8f ||
		    TryGetInverseTransform(localToWorld, out var worldToLocal) == false)
		{
			return false;
		}

		var localOrigin = Vector3.Transform(origin, worldToLocal);
		var localEnd = Vector3.Transform(origin + direction, worldToLocal);
		var localDirection = localEnd - localOrigin;
		if (localDirection.LengthSquared() <= 1e-8f)
		{
			return false;
		}

		var bestFraction = float.MaxValue;
		var bestPoint = Vector3.Zero;
		var bestNormal = Vector3.UnitY;
		var foundHit = false;
		for (var y = 0; y < HeightSampleHeight - 1; y++)
		{
			for (var x = 0; x < HeightSampleWidth - 1; x++)
			{
				var p00 = GetLocalVertexPosition(x, y);
				var p10 = GetLocalVertexPosition(x + 1, y);
				var p01 = GetLocalVertexPosition(x, y + 1);
				var p11 = GetLocalVertexPosition(x + 1, y + 1);

				TryUpdateClosestHit(p00, p01, p10);
				TryUpdateClosestHit(p10, p01, p11);
			}
		}

		if (foundHit == false)
		{
			return false;
		}

		var worldPoint = Vector3.Transform(bestPoint, localToWorld);
		var worldNormal = TransformNormal(bestNormal, localToWorld, worldToLocal);
		hit = new TerrainRaycastHit(worldPoint, worldNormal, bestFraction);
		return true;

		void TryUpdateClosestHit(Vector3 a, Vector3 b, Vector3 c)
		{
			if (TryIntersectSegmentTriangle(localOrigin, localDirection, a, b, c, out var fraction, out var point, out var normal) == false ||
			    fraction >= bestFraction)
			{
				return;
			}

			bestFraction = fraction;
			bestPoint = point;
			bestNormal = normal;
			foundHit = true;
		}
	}

	private void Resolve(TerrainComponent component)
	{
		_heightmapNodeId = component.HeightmapAsset.NodeId;
		_controlMapNodeId = component.ControlMapAsset.NodeId;
		_layerSetNodeId = component.LayerSetAsset.NodeId;
		_resolvedHeightmap = component.HeightmapAsset.Asset;
		_resolvedControlMap = component.ControlMapAsset.Asset;
		_resolvedLayerSet = component.LayerSetAsset.Asset;
		_resolvedWorldSize = component.GetResolvedWorldSize();
		_resolvedHeightScale = component.GetResolvedHeightScale();
		_resolvedChunkSize = component.GetResolvedChunkSizeInQuads();
	}

	private bool NeedsRebuild(TerrainComponent component)
	{
		if (_built == false)
		{
			return true;
		}

		if (_heightmapNodeId != component.HeightmapAsset.NodeId ||
		    _controlMapNodeId != component.ControlMapAsset.NodeId ||
		    _layerSetNodeId != component.LayerSetAsset.NodeId)
		{
			return true;
		}

		if (_lastWorldSize != component.GetResolvedWorldSize() ||
		    Math.Abs(_lastHeightScale - component.GetResolvedHeightScale()) > 0.0001f ||
		    _lastChunkSize != component.GetResolvedChunkSizeInQuads())
		{
			return true;
		}

		var heightRevision = _resolvedHeightmap?.ResourceRevision ?? -1;
		if (_lastHeightResourceRevision != heightRevision)
		{
			return true;
		}

		if (HasTextureContentChanged(
			    _resolvedHeightmap,
			    _lastHeightWidth,
			    _lastHeightHeight,
			    _lastHeightFormat,
			    _lastHeightTopMipData))
		{
			return true;
		}

		var controlRevision = _resolvedControlMap?.ResourceRevision ?? -1;
		if (_lastControlResourceRevision != controlRevision)
		{
			return true;
		}

		return HasTextureContentChanged(
			_resolvedControlMap,
			_lastControlWidth,
			_lastControlHeight,
			_lastControlFormat,
			_lastControlTopMipData);
	}

	private void CaptureBuildState(TerrainComponent component)
	{
		_lastWorldSize = component.GetResolvedWorldSize();
		_lastHeightScale = component.GetResolvedHeightScale();
		_lastChunkSize = component.GetResolvedChunkSizeInQuads();
		_lastHeightResourceRevision = _resolvedHeightmap?.ResourceRevision ?? -1;
		_lastControlResourceRevision = _resolvedControlMap?.ResourceRevision ?? -1;
		_lastHeightWidth = _resolvedHeightmap?.Width ?? 0;
		_lastHeightHeight = _resolvedHeightmap?.Height ?? 0;
		_lastHeightFormat = _resolvedHeightmap?.Format ?? default;
		_lastHeightTopMipData = GetTopMipData(_resolvedHeightmap);
		_lastControlWidth = _resolvedControlMap?.Width ?? 0;
		_lastControlHeight = _resolvedControlMap?.Height ?? 0;
		_lastControlFormat = _resolvedControlMap?.Format ?? default;
		_lastControlTopMipData = GetTopMipData(_resolvedControlMap);
	}

	private static bool HasTextureContentChanged(
		Texture? texture,
		int lastWidth,
		int lastHeight,
		TextureFormat lastFormat,
		byte[]? lastTopMipData)
	{
		if (texture is null)
		{
			return lastWidth != 0 || lastHeight != 0 || lastTopMipData is not null;
		}

		return texture.Width != lastWidth ||
		       texture.Height != lastHeight ||
		       texture.Format != lastFormat ||
		       ReferenceEquals(GetTopMipData(texture), lastTopMipData) == false;
	}

	private static byte[]? GetTopMipData(Texture? texture)
	{
		return texture is { MipLevels.Length: > 0 } ? texture.MipLevels[0].Data : null;
	}

	private void EnsureTerrainResources(RenderGraph renderGraph)
	{
		if (_resolvedControlMap is not null)
		{
			renderGraph.EnsureTextureResources(_resolvedControlMap);
		}

		if (_resolvedLayerSet is null)
		{
			return;
		}

		for (var i = 0; i < _resolvedLayerSet.ResolvedLayerCount; i++)
		{
			var layer = _resolvedLayerSet.GetLayer(i);
			if (layer.Albedo.Asset is { } albedo)
			{
				renderGraph.EnsureTextureResources(albedo);
			}
			if (layer.Normal.Asset is { } normal)
			{
				renderGraph.EnsureTextureResources(normal);
			}
			if (layer.MetallicRoughness.Asset is { } metallicRoughness)
			{
				renderGraph.EnsureTextureResources(metallicRoughness);
			}
			if (layer.Occlusion.Asset is { } occlusion)
			{
				renderGraph.EnsureTextureResources(occlusion);
			}
			if (layer.Height.Asset is { } height)
			{
				renderGraph.EnsureTextureResources(height);
			}
		}
	}

	private static TerrainResolvedLayer[] ResolveLayers(TerrainLayerSet? layerSet)
	{
		if (layerSet is null)
		{
			return [default];
		}

		var resolvedCount = layerSet.ResolvedLayerCount;
		var layers = new TerrainResolvedLayer[resolvedCount];
		for (var i = 0; i < resolvedCount; i++)
		{
			var layer = layerSet.GetLayer(i);
			layers[i] = new TerrainResolvedLayer(
				layer.Albedo.Asset,
				layer.Normal.Asset,
				layer.MetallicRoughness.Asset,
				layer.Occlusion.Asset,
				layer.Height.Asset,
				layer.Scale);
		}

		return layers;
	}

	private void BuildChunks(float[] heights, Vector3[] normals, int sampleWidth, int sampleHeight)
	{
		var quadsX = sampleWidth - 1;
		var quadsY = sampleHeight - 1;
		var chunkCountX = (quadsX + _resolvedChunkSize - 1) / _resolvedChunkSize;
		var chunkCountY = (quadsY + _resolvedChunkSize - 1) / _resolvedChunkSize;
		for (var chunkY = 0; chunkY < chunkCountY; chunkY++)
		{
			for (var chunkX = 0; chunkX < chunkCountX; chunkX++)
			{
				var startX = chunkX * _resolvedChunkSize;
				var startY = chunkY * _resolvedChunkSize;
				var chunkQuadsX = Math.Min(_resolvedChunkSize, quadsX - startX);
				var chunkQuadsY = Math.Min(_resolvedChunkSize, quadsY - startY);
				var lodMeshes = new Mesh[LODCount];
				for (var lodIndex = 0; lodIndex < LODCount; lodIndex++)
				{
					var step = 1 << lodIndex;
					lodMeshes[lodIndex] = BuildChunkMesh(heights, normals, sampleWidth, sampleHeight, startX, startY, chunkQuadsX, chunkQuadsY, step);
				}

				var primaryMesh = lodMeshes[0];
				if (primaryMesh is null)
				{
					continue;
				}

				_chunks.Add(new TerrainChunkRuntime(chunkX, chunkY, lodMeshes, primaryMesh.BoundingSphere));
			}
		}
	}

	private Mesh BuildChunkMesh(
		float[] heights,
		Vector3[] normals,
		int sampleWidth,
		int sampleHeight,
		int startX,
		int startY,
		int chunkQuadsX,
		int chunkQuadsY,
		int step)
	{
		var effectiveQuadsX = Math.Max(step, (chunkQuadsX / step) * step);
		var effectiveQuadsY = Math.Max(step, (chunkQuadsY / step) * step);
		var vertsX = effectiveQuadsX / step + 1;
		var vertsY = effectiveQuadsY / step + 1;
		var baseVertexCount = vertsX * vertsY;
		var vertices = new List<Vector4>(baseVertexCount + vertsX * 2 + vertsY * 2);
		var vertexNormals = new List<Vector3>(baseVertexCount + vertsX * 2 + vertsY * 2);
		var uvs = new List<Vector2>(baseVertexCount + vertsX * 2 + vertsY * 2);
		var tangents = new List<Vector4>(baseVertexCount + vertsX * 2 + vertsY * 2);
		var indices = new List<uint>(effectiveQuadsX * effectiveQuadsY * 6);

		var totalQuadsX = sampleWidth - 1;
		var totalQuadsY = sampleHeight - 1;
		var spacingX = _resolvedWorldSize.X / Math.Max(totalQuadsX, 1);
		var spacingY = _resolvedWorldSize.Y / Math.Max(totalQuadsY, 1);
		var halfWidth = _resolvedWorldSize.X * 0.5f;
		var halfHeight = _resolvedWorldSize.Y * 0.5f;

		for (var localY = 0; localY < vertsY; localY++)
		{
			for (var localX = 0; localX < vertsX; localX++)
			{
				var sampleX = startX + localX * step;
				var sampleY = startY + localY * step;
				AddVertex(sampleX, sampleY, false);
			}
		}

		for (var localY = 0; localY < vertsY - 1; localY++)
		{
			for (var localX = 0; localX < vertsX - 1; localX++)
			{
				var i0 = localY * vertsX + localX;
				var i1 = i0 + 1;
				var i2 = i0 + vertsX;
				var i3 = i2 + 1;
				indices.Add((uint)i0);
				indices.Add((uint)i2);
				indices.Add((uint)i1);
				indices.Add((uint)i1);
				indices.Add((uint)i2);
				indices.Add((uint)i3);
			}
		}

		var skirtDepth = Math.Max(spacingX, spacingY) * (2.0f * step) + _resolvedHeightScale * 0.05f;
		var topStart = vertices.Count;
		for (var localX = 0; localX < vertsX; localX++)
		{
			AddVertex(startX + localX * step, startY, true, skirtDepth);
		}

		var bottomStart = vertices.Count;
		for (var localX = 0; localX < vertsX; localX++)
		{
			AddVertex(startX + localX * step, startY + effectiveQuadsY, true, skirtDepth);
		}

		var leftStart = vertices.Count;
		for (var localY = 0; localY < vertsY; localY++)
		{
			AddVertex(startX, startY + localY * step, true, skirtDepth);
		}

		var rightStart = vertices.Count;
		for (var localY = 0; localY < vertsY; localY++)
		{
			AddVertex(startX + effectiveQuadsX, startY + localY * step, true, skirtDepth);
		}

		for (var localX = 0; localX < vertsX - 1; localX++)
		{
			AddSkirtQuad(localX, localX + 1, topStart + localX, topStart + localX + 1);
			AddSkirtQuad((vertsY - 1) * vertsX + localX + 1, (vertsY - 1) * vertsX + localX, bottomStart + localX + 1, bottomStart + localX);
		}

		for (var localY = 0; localY < vertsY - 1; localY++)
		{
			AddSkirtQuad((localY + 1) * vertsX, localY * vertsX, leftStart + localY + 1, leftStart + localY);
			AddSkirtQuad(localY * vertsX + (vertsX - 1), (localY + 1) * vertsX + (vertsX - 1), rightStart + localY, rightStart + localY + 1);
		}

		return new Mesh(vertices, indices, vertexNormals, uvs, tangents);

		void AddVertex(int sampleX, int sampleY, bool isSkirt, float additionalDepth = 0.0f)
		{
			sampleX = Math.Clamp(sampleX, 0, sampleWidth - 1);
			sampleY = Math.Clamp(sampleY, 0, sampleHeight - 1);
			var index = sampleY * sampleWidth + sampleX;
			var height = heights[index] * _resolvedHeightScale - (isSkirt ? additionalDepth : 0.0f);
			var x = sampleX * spacingX - halfWidth;
			var z = sampleY * spacingY - halfHeight;
			vertices.Add(new Vector4(x, height, z, 1.0f));
			vertexNormals.Add(normals[index]);
			uvs.Add(new Vector2(
				totalQuadsX > 0 ? sampleX / (float)totalQuadsX : 0.0f,
				totalQuadsY > 0 ? sampleY / (float)totalQuadsY : 0.0f));
			tangents.Add(new Vector4(1.0f, 0.0f, 0.0f, 1.0f));
		}

		void AddSkirtQuad(int a, int b, int skirtA, int skirtB)
		{
			indices.Add((uint)a);
			indices.Add((uint)b);
			indices.Add((uint)skirtA);
			indices.Add((uint)skirtA);
			indices.Add((uint)b);
			indices.Add((uint)skirtB);
		}
	}

	private static Vector3[] BuildNormals(float[] heights, int width, int height, Vector2 worldSize, float heightScale)
	{
		var normals = new Vector3[width * height];
		var spacingX = worldSize.X / Math.Max(width - 1, 1);
		var spacingY = worldSize.Y / Math.Max(height - 1, 1);
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var x0 = Math.Max(0, x - 1);
				var x1 = Math.Min(width - 1, x + 1);
				var y0 = Math.Max(0, y - 1);
				var y1 = Math.Min(height - 1, y + 1);
				var left = heights[y * width + x0] * heightScale;
				var right = heights[y * width + x1] * heightScale;
				var down = heights[y0 * width + x] * heightScale;
				var up = heights[y1 * width + x] * heightScale;
				var tangentX = new Vector3(Math.Max(spacingX * (x1 - x0), 0.001f), right - left, 0.0f);
				var tangentY = new Vector3(0.0f, up - down, Math.Max(spacingY * (y1 - y0), 0.001f));
				var normal = Vector3.Cross(tangentY, tangentX);
				normals[y * width + x] = normal.LengthSquared() > 0.0f
					? Vector3.Normalize(normal)
					: Vector3.UnitY;
			}
		}

		return normals;
	}

	private Mesh BuildCollisionMesh(float[] heights, Vector3[] normals, int sampleWidth, int sampleHeight)
	{
		var vertexCount = sampleWidth * sampleHeight;
		var vertices = new Vector4[vertexCount];
		var uvs = new Vector2[vertexCount];
		var tangents = new Vector4[vertexCount];
		var totalQuadsX = sampleWidth - 1;
		var totalQuadsY = sampleHeight - 1;
		for (var y = 0; y < sampleHeight; y++)
		{
			for (var x = 0; x < sampleWidth; x++)
			{
				var index = y * sampleWidth + x;
				var position = CreateLocalVertexPosition(x, y, heights[index]);
				vertices[index] = new Vector4(position, 1.0f);
				uvs[index] = new Vector2(
					totalQuadsX > 0 ? x / (float)totalQuadsX : 0.0f,
					totalQuadsY > 0 ? y / (float)totalQuadsY : 0.0f);
				tangents[index] = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
			}
		}

		var indices = new uint[totalQuadsX * totalQuadsY * 6];
		var writeIndex = 0;
		for (var y = 0; y < totalQuadsY; y++)
		{
			for (var x = 0; x < totalQuadsX; x++)
			{
				var i0 = y * sampleWidth + x;
				var i1 = i0 + 1;
				var i2 = i0 + sampleWidth;
				var i3 = i2 + 1;
				indices[writeIndex++] = (uint)i0;
				indices[writeIndex++] = (uint)i2;
				indices[writeIndex++] = (uint)i1;
				indices[writeIndex++] = (uint)i1;
				indices[writeIndex++] = (uint)i2;
				indices[writeIndex++] = (uint)i3;
			}
		}

		return new Mesh(vertices, indices, normals, uvs, tangents);
	}

	private static float[]? DecodeHeightSamples(Texture texture, out int width, out int height)
	{
		width = 0;
		height = 0;
		if (texture.MipLevels.Length == 0)
		{
			return null;
		}

		var mip = texture.MipLevels[0];
		width = mip.Width;
		height = mip.Height;
		if (width <= 0 || height <= 0)
		{
			return null;
		}

		return texture.Format switch
		{
			TextureFormat.Rgba8Unorm => DecodeRgba8Height(mip.Data, width, height),
			TextureFormat.Bgra8Unorm => DecodeBgra8Height(mip.Data, width, height),
			TextureFormat.Bc1Unorm => DecodeBc1Height(mip.Data, width, height),
			_ => null
		};
	}

	private static float[] DecodeRgba8Height(byte[] data, int width, int height)
	{
		var result = new float[width * height];
		for (var i = 0; i < result.Length; i++)
		{
			result[i] = data[i * 4] / 255.0f;
		}

		return result;
	}

	private static float[] DecodeBgra8Height(byte[] data, int width, int height)
	{
		var result = new float[width * height];
		for (var i = 0; i < result.Length; i++)
		{
			result[i] = data[i * 4 + 2] / 255.0f;
		}

		return result;
	}

	public static ColorRGBA[] DecodeColorMap(Texture texture)
	{
		if (texture.MipLevels.Length == 0)
		{
			return Array.Empty<ColorRGBA>();
		}

		var mip = texture.MipLevels[0];
		return texture.Format switch
		{
			TextureFormat.Rgba8Unorm => DecodeRgba8Colors(mip.Data, mip.Width, mip.Height),
			TextureFormat.Bgra8Unorm => DecodeBgra8Colors(mip.Data, mip.Width, mip.Height),
			TextureFormat.Bc1Unorm => DecodeBc1Colors(mip.Data, mip.Width, mip.Height),
			_ => Array.Empty<ColorRGBA>()
		};
	}

	private static ColorRGBA[] DecodeRgba8Colors(byte[] data, int width, int height)
	{
		var result = new ColorRGBA[width * height];
		for (var i = 0; i < result.Length; i++)
		{
			var offset = i * 4;
			result[i] = new ColorRGBA(
				data[offset] / 255.0f,
				data[offset + 1] / 255.0f,
				data[offset + 2] / 255.0f,
				data[offset + 3] / 255.0f);
		}

		return result;
	}

	private static ColorRGBA[] DecodeBgra8Colors(byte[] data, int width, int height)
	{
		var result = new ColorRGBA[width * height];
		for (var i = 0; i < result.Length; i++)
		{
			var offset = i * 4;
			result[i] = new ColorRGBA(
				data[offset + 2] / 255.0f,
				data[offset + 1] / 255.0f,
				data[offset] / 255.0f,
				data[offset + 3] / 255.0f);
		}

		return result;
	}

	private static float[] DecodeBc1Height(byte[] data, int width, int height)
	{
		var colors = DecodeBc1Colors(data, width, height);
		var heights = new float[colors.Length];
		for (var i = 0; i < colors.Length; i++)
		{
			heights[i] = colors[i].R;
		}

		return heights;
	}

	private static ColorRGBA[] DecodeBc1Colors(byte[] data, int width, int height)
	{
		var result = new ColorRGBA[width * height];
		var blockWidth = (width + 3) / 4;
		var blockHeight = (height + 3) / 4;
		var offset = 0;
		Span<ColorRGBA> palette = stackalloc ColorRGBA[4];
		for (var blockY = 0; blockY < blockHeight; blockY++)
		{
			for (var blockX = 0; blockX < blockWidth; blockX++)
			{
				var color0 = (ushort)(data[offset] | (data[offset + 1] << 8));
				var color1 = (ushort)(data[offset + 2] | (data[offset + 3] << 8));
				var codes = (uint)(data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16) | (data[offset + 7] << 24));
				offset += 8;

				palette[0] = DecodeRgb565(color0);
				palette[1] = DecodeRgb565(color1);
				if (color0 > color1)
				{
					palette[2] = Lerp(palette[0], palette[1], 1.0f / 3.0f, 2.0f / 3.0f);
					palette[3] = Lerp(palette[0], palette[1], 2.0f / 3.0f, 1.0f / 3.0f);
				}
				else
				{
					palette[2] = Lerp(palette[0], palette[1], 0.5f, 0.5f);
					palette[3] = new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f);
				}

				for (var localY = 0; localY < 4; localY++)
				{
					for (var localX = 0; localX < 4; localX++)
					{
						var sampleX = blockX * 4 + localX;
						var sampleY = blockY * 4 + localY;
						if (sampleX >= width || sampleY >= height)
						{
							continue;
						}

						var codeIndex = localY * 4 + localX;
						var paletteIndex = (int)((codes >> (codeIndex * 2)) & 0x3);
						result[sampleY * width + sampleX] = palette[paletteIndex];
					}
				}
			}
		}

		return result;
	}

	private static ColorRGBA DecodeRgb565(ushort packed)
	{
		var r = (packed >> 11) & 0x1F;
		var g = (packed >> 5) & 0x3F;
		var b = packed & 0x1F;
		return new ColorRGBA(
			r / 31.0f,
			g / 63.0f,
			b / 31.0f,
			1.0f);
	}

	private static ColorRGBA Lerp(ColorRGBA a, ColorRGBA b, float aWeight, float bWeight)
	{
		return new ColorRGBA(
			a.R * aWeight + b.R * bWeight,
			a.G * aWeight + b.G * bWeight,
			a.B * aWeight + b.B * bWeight,
			a.A * aWeight + b.A * bWeight);
	}

	private static int SelectLod(TerrainChunkRuntime chunk, Matrix4x4 worldTransform, Vector3 cameraOrigin)
	{
		var center = TransformPoint(chunk.LocalBounds.Center, worldTransform);
		var distance = Vector3.Distance(center, cameraOrigin);
		if (distance < 120.0f)
		{
			return 0;
		}
		if (distance < 320.0f)
		{
			return 1;
		}

		return 2;
	}

	private void EnforceNeighborLodDelta(int[] lods)
	{
		var indexByCoordinate = new Dictionary<(int X, int Y), int>(_chunks.Count);
		for (var i = 0; i < _chunks.Count; i++)
		{
			indexByCoordinate[(_chunks[i].ChunkX, _chunks[i].ChunkY)] = i;
		}

		var changed = true;
		while (changed)
		{
			changed = false;
			for (var i = 0; i < _chunks.Count; i++)
			{
				var chunk = _chunks[i];
				ClampNeighbor(chunk.ChunkX - 1, chunk.ChunkY, i);
				ClampNeighbor(chunk.ChunkX + 1, chunk.ChunkY, i);
				ClampNeighbor(chunk.ChunkX, chunk.ChunkY - 1, i);
				ClampNeighbor(chunk.ChunkX, chunk.ChunkY + 1, i);
			}
		}

		void ClampNeighbor(int neighborX, int neighborY, int sourceIndex)
		{
			if (indexByCoordinate.TryGetValue((neighborX, neighborY), out var neighborIndex) == false)
			{
				return;
			}

			if (lods[sourceIndex] > lods[neighborIndex] + 1)
			{
				lods[sourceIndex] = lods[neighborIndex] + 1;
				changed = true;
			}
		}
	}

	private bool TryGetInverseTransform(Matrix4x4 localToWorld, out Matrix4x4 worldToLocal)
	{
		worldToLocal = Matrix4x4.Identity;
		return _built &&
		       _heightSamples is not null &&
		       _normals is not null &&
		       Matrix4x4.Invert(localToWorld, out worldToLocal);
	}

	private bool TrySampleLocalSurface(float localX, float localZ, out Vector3 surfacePoint, out Vector3 surfaceNormal)
	{
		surfacePoint = Vector3.Zero;
		surfaceNormal = Vector3.UnitY;
		if (_built == false ||
		    _heightSamples is null ||
		    _normals is null ||
		    HeightSampleWidth < 2 ||
		    HeightSampleHeight < 2 ||
		    _resolvedWorldSize.X <= 1e-6f ||
		    _resolvedWorldSize.Y <= 1e-6f)
		{
			return false;
		}

		var halfWidth = _resolvedWorldSize.X * 0.5f;
		var halfDepth = _resolvedWorldSize.Y * 0.5f;
		if (localX < -halfWidth || localX > halfWidth || localZ < -halfDepth || localZ > halfDepth)
		{
			return false;
		}

		var sampleX = ((localX + halfWidth) / _resolvedWorldSize.X) * (HeightSampleWidth - 1);
		var sampleZ = ((localZ + halfDepth) / _resolvedWorldSize.Y) * (HeightSampleHeight - 1);
		sampleX = Math.Clamp(sampleX, 0.0f, HeightSampleWidth - 1);
		sampleZ = Math.Clamp(sampleZ, 0.0f, HeightSampleHeight - 1);
		var cellX = Math.Min((int)MathF.Floor(sampleX), HeightSampleWidth - 2);
		var cellZ = Math.Min((int)MathF.Floor(sampleZ), HeightSampleHeight - 2);
		var tx = Math.Clamp(sampleX - cellX, 0.0f, 1.0f);
		var tz = Math.Clamp(sampleZ - cellZ, 0.0f, 1.0f);

		var h00 = _heightSamples[cellZ * HeightSampleWidth + cellX] * _resolvedHeightScale;
		var h10 = _heightSamples[cellZ * HeightSampleWidth + cellX + 1] * _resolvedHeightScale;
		var h01 = _heightSamples[(cellZ + 1) * HeightSampleWidth + cellX] * _resolvedHeightScale;
		var h11 = _heightSamples[(cellZ + 1) * HeightSampleWidth + cellX + 1] * _resolvedHeightScale;
		var n00 = _normals[cellZ * HeightSampleWidth + cellX];
		var n10 = _normals[cellZ * HeightSampleWidth + cellX + 1];
		var n01 = _normals[(cellZ + 1) * HeightSampleWidth + cellX];
		var n11 = _normals[(cellZ + 1) * HeightSampleWidth + cellX + 1];

		if (tx + tz <= 1.0f)
		{
			var w00 = 1.0f - tx - tz;
			var w01 = tz;
			var w10 = tx;
			surfacePoint = new Vector3(localX, h00 * w00 + h01 * w01 + h10 * w10, localZ);
			surfaceNormal = NormalizeDirection(n00 * w00 + n01 * w01 + n10 * w10);
			return true;
		}

		var w10b = 1.0f - tz;
		var w01b = 1.0f - tx;
		var w11 = tx + tz - 1.0f;
		surfacePoint = new Vector3(localX, h10 * w10b + h01 * w01b + h11 * w11, localZ);
		surfaceNormal = NormalizeDirection(n10 * w10b + n01 * w01b + n11 * w11);
		return true;
	}

	private Vector3 GetLocalVertexPosition(int sampleX, int sampleY)
	{
		if (_heightSamples is null)
		{
			return Vector3.Zero;
		}

		var index = sampleY * HeightSampleWidth + sampleX;
		return CreateLocalVertexPosition(sampleX, sampleY, _heightSamples[index]);
	}

	private Vector3 CreateLocalVertexPosition(int sampleX, int sampleY, float normalizedHeight)
	{
		var halfWidth = _resolvedWorldSize.X * 0.5f;
		var halfDepth = _resolvedWorldSize.Y * 0.5f;
		var spacingX = _resolvedWorldSize.X / Math.Max(HeightSampleWidth - 1, 1);
		var spacingZ = _resolvedWorldSize.Y / Math.Max(HeightSampleHeight - 1, 1);
		return new Vector3(
			sampleX * spacingX - halfWidth,
			normalizedHeight * _resolvedHeightScale,
			sampleY * spacingZ - halfDepth);
	}

	private static bool TryIntersectSegmentTriangle(
		Vector3 origin,
		Vector3 direction,
		Vector3 a,
		Vector3 b,
		Vector3 c,
		out float fraction,
		out Vector3 point,
		out Vector3 normal)
	{
		fraction = 0.0f;
		point = Vector3.Zero;
		normal = Vector3.UnitY;

		var edge1 = b - a;
		var edge2 = c - a;
		var p = Vector3.Cross(direction, edge2);
		var determinant = Vector3.Dot(edge1, p);
		if (MathF.Abs(determinant) <= 1e-8f)
		{
			return false;
		}

		var inverseDeterminant = 1.0f / determinant;
		var tVector = origin - a;
		var u = Vector3.Dot(tVector, p) * inverseDeterminant;
		if (u < 0.0f || u > 1.0f)
		{
			return false;
		}

		var q = Vector3.Cross(tVector, edge1);
		var v = Vector3.Dot(direction, q) * inverseDeterminant;
		if (v < 0.0f || u + v > 1.0f)
		{
			return false;
		}

		var t = Vector3.Dot(edge2, q) * inverseDeterminant;
		if (t < 0.0f || t > 1.0f)
		{
			return false;
		}

		var triangleNormal = Vector3.Cross(edge1, edge2);
		if (triangleNormal.LengthSquared() <= 1e-8f)
		{
			return false;
		}

		fraction = t;
		point = origin + direction * t;
		normal = Vector3.Normalize(triangleNormal);
		return true;
	}

	private static Vector3 TransformPoint(Vector3 point, Matrix4x4 matrix) => Vector3.Transform(point, matrix);

	private static Vector3 TransformNormal(Vector3 normal, Matrix4x4 localToWorld, Matrix4x4 worldToLocal)
	{
		var normalMatrix = Matrix4x4.Transpose(worldToLocal);
		return NormalizeDirection(Vector3.TransformNormal(normal, normalMatrix));
	}

	private static float TransformRadius(float radius, Matrix4x4 matrix)
	{
		var scaleX = new Vector3(matrix.M11, matrix.M12, matrix.M13).Length();
		var scaleY = new Vector3(matrix.M21, matrix.M22, matrix.M23).Length();
		var scaleZ = new Vector3(matrix.M31, matrix.M32, matrix.M33).Length();
		var scale = Math.Max(scaleX, Math.Max(scaleY, scaleZ));
		return radius * scale;
	}

	private static Vector3 NormalizeDirection(Vector3 value)
	{
		return value.LengthSquared() > 0.0f ? Vector3.Normalize(value) : Vector3.UnitY;
	}
}

public readonly record struct TerrainRaycastHit(Vector3 Point, Vector3 Normal, float Fraction);

public sealed class TerrainChunkRuntime
{
	public TerrainChunkRuntime(int chunkX, int chunkY, Mesh[] lodMeshes, BoundingSphere localBounds)
	{
		ChunkX = chunkX;
		ChunkY = chunkY;
		LodMeshes = lodMeshes ?? throw new ArgumentNullException(nameof(lodMeshes));
		LocalBounds = localBounds;
	}

	public int ChunkX { get; }
	public int ChunkY { get; }
	public Mesh[] LodMeshes { get; }
	public BoundingSphere LocalBounds { get; }
}
