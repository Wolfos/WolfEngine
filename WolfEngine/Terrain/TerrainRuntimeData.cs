using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;

namespace WolfEngine;

public sealed class TerrainRuntimeData
{
	private const int MaxChunkTileCount = 10_000;
	private readonly List<TerrainChunkRuntime> _chunks = new();
	private readonly List<TerrainRayTracingChunkRuntime> _rayTracingChunks = new();
	private readonly List<Mesh> _pendingReleasedMeshes = new();
	private Mesh[] _sharedLodMeshes = Array.Empty<Mesh>();
	private TerrainAsset? _resolvedTerrainAsset;
	private Texture? _resolvedHeightmap;
	private Texture? _resolvedRenderHeightmap;
	private Texture? _resolvedRenderLayerIndexMap;
	private Texture? _resolvedRenderLayerWeightMap;
	private Texture? _layerIndexSamplingSource;
	private Texture? _layerIndexSamplingTexture;
	private int _layerIndexSamplingSourceRevision = -1;
	private TerrainLayerSet? _resolvedLayerSet;
	private Vector2 _resolvedWorldSize;
	private float _resolvedHeightScale;
	private float _resolvedChunkSizeMeters;
	private int _resolvedLodCount;
	private int _resolvedLod0Resolution;
	private int _resolvedRayTracingResolution;
	private float[] _resolvedLodDistances = Array.Empty<float>();
	private Guid _terrainAssetNodeId;
	private Guid _layerSetNodeId;
	private Vector2 _lastSampleWorldSize;
	private float _lastSampleHeightScale;
	private int _lastHeightResourceRevision = -1;
	private int _lastHeightWidth;
	private int _lastHeightHeight;
	private TextureFormat _lastHeightFormat;
	private byte[]? _lastHeightTopMipData;
	private bool _hasSampleState;
	private Vector2 _lastLayoutWorldSize;
	private float _lastLayoutHeightScale;
	private float _lastLayoutChunkSizeMeters;
	private int _lastLayoutLodCount;
	private int _lastLayoutLod0Resolution;
	private int _lastLayoutRayTracingResolution;
	private float[] _lastLayoutLodDistances = Array.Empty<float>();
	private TerrainHeightmapDirtyRegion? _pendingRayTracingDirtyRegion;
	private bool _hasLayoutState;
	private bool _built;
	private float[]? _heightSamples;
	private Vector3[]? _normals;

	public IReadOnlyList<TerrainChunkRuntime> Chunks => _chunks;
	public IReadOnlyList<TerrainRayTracingChunkRuntime> RayTracingChunks => _rayTracingChunks;
	public IReadOnlyList<Mesh> SharedLodMeshes => _sharedLodMeshes;
	public int HeightSampleWidth { get; private set; }
	public int HeightSampleHeight { get; private set; }
	public ReadOnlyMemory<float> HeightSamples => _heightSamples ?? Array.Empty<float>();
	public Vector2 ResolvedWorldSize => _resolvedWorldSize;
	public float ResolvedHeightScale => _resolvedHeightScale;
	public Vector2 SampleSpacing { get; private set; }
	public Box LocalBounds { get; private set; }
	public int RuntimeVersion { get; private set; }

	public bool EnsureBuilt(TerrainComponent component)
	{
		Resolve(component);
		if (EnsureSamplingState(component) == false)
		{
			ClearRenderLayout();
			_built = false;
			return false;
		}

		if (NeedsRenderLayoutRebuild())
		{
			RebuildRenderLayout();
		}

		ApplyPendingRayTracingDirtyRegion();
		_built = _sharedLodMeshes.Length > 0 && _chunks.Count > 0;
		return _built;
	}

	public void MarkHeightmapEdited(in TerrainHeightmapDirtyRegion dirtyRegion)
	{
		if (dirtyRegion.IsEmpty)
		{
			return;
		}

		_pendingRayTracingDirtyRegion = _pendingRayTracingDirtyRegion.HasValue
			? TerrainHeightmapDirtyRegion.Union(_pendingRayTracingDirtyRegion.Value, dirtyRegion)
			: dirtyRegion;
	}

	public void ReleasePendingMeshResources(RenderGraph renderGraph)
	{
		ArgumentNullException.ThrowIfNull(renderGraph);
		if (_pendingReleasedMeshes.Count == 0)
		{
			return;
		}

		for (var i = 0; i < _pendingReleasedMeshes.Count; i++)
		{
			renderGraph.ReleaseMeshResources(_pendingReleasedMeshes[i]);
		}

		_pendingReleasedMeshes.Clear();
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
		if (_built == false || _chunks.Count == 0 || _sharedLodMeshes.Length == 0)
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
		var autoMaterialBlendDegrees = layerSet?.AutoMaterialBlendDegrees ?? 12.0f;
		var layers = ResolveLayers(layerSet);
		for (var i = 0; i < _chunks.Count; i++)
		{
			var chunk = _chunks[i];
			var lodIndex = Math.Clamp(selectedLods[i], 0, _sharedLodMeshes.Length - 1);
			var mesh = _sharedLodMeshes[lodIndex];
			renderGraph.EnsureMeshResources(mesh);
			destination.Add(new TerrainChunkDrawRecord(
				i,
				mesh,
				material,
				worldTransform,
				chunk.LocalBounds,
				chunk.InstanceData,
				new TerrainDrawSurface(
					_resolvedRenderHeightmap,
					_resolvedRenderLayerIndexMap,
					_resolvedRenderLayerWeightMap,
					_resolvedHeightScale,
					layerCount,
					heightBlendSharpness,
					autoMaterialBlendDegrees,
					layers),
				_rayTracingChunks[i].CreateData(i)));
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
		surfaceNormal = TransformNormal(localSurfaceNormal, worldToLocal);
		return true;
	}

	public bool TryRaycast(Matrix4x4 localToWorld, Vector3 origin, Vector3 direction, out TerrainRaycastHit hit)
	{
		hit = default;
		if (_heightSamples is null ||
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
		var worldNormal = TransformNormal(bestNormal, worldToLocal);
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
		_terrainAssetNodeId = component.TerrainAsset.NodeId;
		_resolvedTerrainAsset = component.TerrainAsset.Asset;
		_layerSetNodeId = component.LayerSetAsset.NodeId;
		_resolvedHeightmap = _resolvedTerrainAsset?.Heightmap;
		_resolvedRenderHeightmap = component.AuthoringPreviewHeightmap ?? _resolvedHeightmap;
		_resolvedRenderLayerIndexMap = ResolveLayerIndexSamplingTexture(component.AuthoringPreviewLayerIndexMap ?? _resolvedTerrainAsset?.LayerIndexMap);
		_resolvedRenderLayerWeightMap = component.AuthoringPreviewLayerWeightMap ?? _resolvedTerrainAsset?.LayerWeightMap;
		_resolvedLayerSet = component.LayerSetAsset.Asset;
		_resolvedWorldSize = component.GetResolvedWorldSize();
		_resolvedHeightScale = component.GetResolvedHeightScale();
		_resolvedLodCount = component.GetResolvedLodCount();
		_resolvedLod0Resolution = component.GetResolvedLod0ResolutionInQuads();
		_resolvedRayTracingResolution = component.GetResolvedRayTracingResolutionInQuads();
		_resolvedLodDistances = component.GetResolvedLodDistancesMeters();
		_resolvedChunkSizeMeters = ResolveChunkSizeMeters(component);
	}

	private Texture? ResolveLayerIndexSamplingTexture(Texture? source)
	{
		if (source is null || source.Format != TextureFormat.Rgba8Uint)
		{
			return source;
		}

		var sourceRevision = source.ResourceRevision;
		if (ReferenceEquals(_layerIndexSamplingSource, source) &&
		    _layerIndexSamplingSourceRevision == sourceRevision &&
		    _layerIndexSamplingTexture is not null)
		{
			return _layerIndexSamplingTexture;
		}

		var mipLevels = new TextureMipData[source.MipLevels.Length];
		for (var i = 0; i < source.MipLevels.Length; i++)
		{
			var mip = source.MipLevels[i];
			mipLevels[i] = new TextureMipData(mip.Width, mip.Height, mip.Data.ToArray());
		}

		if (_layerIndexSamplingTexture is null)
		{
			_layerIndexSamplingTexture = new Texture(
				$"{source.Name}:sampling",
				source.Width,
				source.Height,
				false,
				TextureFormat.Rgba8Unorm,
				mipLevels);
		}
		else
		{
			_layerIndexSamplingTexture.ApplyTextureData(source.Width, source.Height, false, TextureFormat.Rgba8Unorm, mipLevels);
		}

		_layerIndexSamplingSource = source;
		_layerIndexSamplingSourceRevision = sourceRevision;
		return _layerIndexSamplingTexture;
	}

	private bool EnsureSamplingState(TerrainComponent component)
	{
		if (NeedsSamplingRefresh(component) == false)
		{
			return _heightSamples is not null && _normals is not null && HeightSampleWidth >= 2 && HeightSampleHeight >= 2;
		}

		_heightSamples = null;
		_normals = null;
		HeightSampleWidth = 0;
		HeightSampleHeight = 0;
		SampleSpacing = Vector2.Zero;
		LocalBounds = default;
		if (_resolvedHeightmap is null)
		{
			CaptureSamplingState(component);
			return false;
		}

		var heightSamples = DecodeHeightSamples(_resolvedHeightmap, out var sampleWidth, out var sampleHeight);
		if (heightSamples is null || sampleWidth < 2 || sampleHeight < 2)
		{
			CaptureSamplingState(component);
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
		_resolvedChunkSizeMeters = ResolveChunkSizeMeters(component);
		CaptureSamplingState(component);
		RuntimeVersion++;
		return true;
	}

	private bool NeedsSamplingRefresh(TerrainComponent component)
	{
		if (_hasSampleState == false)
		{
			return true;
		}

		if (_terrainAssetNodeId != component.TerrainAsset.NodeId)
		{
			return true;
		}

		if (_lastSampleWorldSize != component.GetResolvedWorldSize() ||
		    Math.Abs(_lastSampleHeightScale - component.GetResolvedHeightScale()) > 0.0001f)
		{
			return true;
		}

		var heightRevision = _resolvedHeightmap?.ResourceRevision ?? -1;
		if (_lastHeightResourceRevision != heightRevision)
		{
			return true;
		}

		return HasTextureContentChanged(
			_resolvedHeightmap,
			_lastHeightWidth,
			_lastHeightHeight,
			_lastHeightFormat,
			_lastHeightTopMipData);
	}

	private void CaptureSamplingState(TerrainComponent component)
	{
		_hasSampleState = true;
		_lastSampleWorldSize = component.GetResolvedWorldSize();
		_lastSampleHeightScale = component.GetResolvedHeightScale();
		_lastHeightResourceRevision = _resolvedHeightmap?.ResourceRevision ?? -1;
		_lastHeightWidth = _resolvedHeightmap?.Width ?? 0;
		_lastHeightHeight = _resolvedHeightmap?.Height ?? 0;
		_lastHeightFormat = _resolvedHeightmap?.Format ?? default;
		_lastHeightTopMipData = GetTopMipData(_resolvedHeightmap);
	}

	private bool NeedsRenderLayoutRebuild()
	{
		if (_hasLayoutState == false)
		{
			return true;
		}

		if (_lastLayoutWorldSize != _resolvedWorldSize ||
		    Math.Abs(_lastLayoutHeightScale - _resolvedHeightScale) > 0.0001f ||
		    Math.Abs(_lastLayoutChunkSizeMeters - _resolvedChunkSizeMeters) > 0.0001f ||
		    _lastLayoutLodCount != _resolvedLodCount ||
		    _lastLayoutLod0Resolution != _resolvedLod0Resolution ||
		    _lastLayoutRayTracingResolution != _resolvedRayTracingResolution)
		{
			return true;
		}

		if (_lastLayoutLodDistances.Length != _resolvedLodDistances.Length)
		{
			return true;
		}

		for (var i = 0; i < _resolvedLodDistances.Length; i++)
		{
			if (Math.Abs(_lastLayoutLodDistances[i] - _resolvedLodDistances[i]) > 0.0001f)
			{
				return true;
			}
		}

		return false;
	}

	private void CaptureRenderLayoutState()
	{
		_hasLayoutState = true;
		_lastLayoutWorldSize = _resolvedWorldSize;
		_lastLayoutHeightScale = _resolvedHeightScale;
		_lastLayoutChunkSizeMeters = _resolvedChunkSizeMeters;
		_lastLayoutLodCount = _resolvedLodCount;
		_lastLayoutLod0Resolution = _resolvedLod0Resolution;
		_lastLayoutRayTracingResolution = _resolvedRayTracingResolution;
		_lastLayoutLodDistances = (float[])_resolvedLodDistances.Clone();
	}

	private void RebuildRenderLayout()
	{
		CaptureReleasedSharedMeshes();
		_chunks.Clear();
		_rayTracingChunks.Clear();
		_sharedLodMeshes = Array.Empty<Mesh>();
		if (ExceedsChunkTileLimit(_resolvedWorldSize, _resolvedChunkSizeMeters, out var chunkTileCount))
		{
			Console.WriteLine(
				$"Terrain mesh build refused: {chunkTileCount} terrain tiles exceeds the limit of {MaxChunkTileCount}. " +
				$"WorldSize={_resolvedWorldSize.X}x{_resolvedWorldSize.Y}, ChunkSizeMeters={_resolvedChunkSizeMeters}.");
			CaptureRenderLayoutState();
			return;
		}

		_sharedLodMeshes = BuildSharedLodMeshes();
		BuildChunks();
		CaptureRenderLayoutState();
	}

	private void ClearRenderLayout()
	{
		CaptureReleasedSharedMeshes();
		_chunks.Clear();
		_rayTracingChunks.Clear();
		_sharedLodMeshes = Array.Empty<Mesh>();
		_hasLayoutState = false;
	}

	private void EnsureTerrainResources(RenderGraph renderGraph)
	{
		if (_resolvedRenderHeightmap is not null)
		{
			renderGraph.EnsureTextureResources(_resolvedRenderHeightmap);
		}

		if (_resolvedRenderLayerIndexMap is not null)
		{
			renderGraph.EnsureTextureResources(_resolvedRenderLayerIndexMap);
		}

		if (_resolvedRenderLayerWeightMap is not null)
		{
			renderGraph.EnsureTextureResources(_resolvedRenderLayerWeightMap);
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
			if (layer.Orm.Asset is { } orm)
			{
				renderGraph.EnsureTextureResources(orm);
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
				layer.Orm.Asset,
				layer.Height.Asset,
				layer.Scale,
				layer.AutoMaterial,
				layer.UseMinimumSlope,
				layer.MinimumSlopeDegrees);
		}

		return layers;
	}

	private Mesh[] BuildSharedLodMeshes()
	{
		var meshes = new Mesh[_resolvedLodCount];
		for (var lodIndex = 0; lodIndex < meshes.Length; lodIndex++)
		{
			var resolution = Math.Max(1, _resolvedLod0Resolution >> lodIndex);
			meshes[lodIndex] = BuildSharedChunkMesh(resolution);
		}

		return meshes;
	}

	private Mesh BuildSharedChunkMesh(int quadsPerAxis)
	{
		var vertsPerAxis = quadsPerAxis + 1;
		var baseVertexCount = vertsPerAxis * vertsPerAxis;
		var vertices = new List<Vector4>(baseVertexCount + vertsPerAxis * 4);
		var normals = new List<Vector3>(baseVertexCount + vertsPerAxis * 4);
		var uvs = new List<Vector2>(baseVertexCount + vertsPerAxis * 4);
		var tangents = new List<Vector4>(baseVertexCount + vertsPerAxis * 4);
		var indices = new List<uint>(quadsPerAxis * quadsPerAxis * 6 + vertsPerAxis * 24);

		for (var y = 0; y < vertsPerAxis; y++)
		{
			var v = quadsPerAxis > 0 ? y / (float)quadsPerAxis : 0.0f;
			for (var x = 0; x < vertsPerAxis; x++)
			{
				var u = quadsPerAxis > 0 ? x / (float)quadsPerAxis : 0.0f;
				AddVertex(u, v, 0.0f);
			}
		}

		for (var y = 0; y < quadsPerAxis; y++)
		{
			for (var x = 0; x < quadsPerAxis; x++)
			{
				var i0 = y * vertsPerAxis + x;
				var i1 = i0 + 1;
				var i2 = i0 + vertsPerAxis;
				var i3 = i2 + 1;
				indices.Add((uint)i0);
				indices.Add((uint)i2);
				indices.Add((uint)i1);
				indices.Add((uint)i1);
				indices.Add((uint)i2);
				indices.Add((uint)i3);
			}
		}

		var skirtDepth = (_resolvedChunkSizeMeters / Math.Max(quadsPerAxis, 1)) * 2.0f + _resolvedHeightScale * 0.05f;
		var topStart = vertices.Count;
		for (var x = 0; x < vertsPerAxis; x++)
		{
			var u = quadsPerAxis > 0 ? x / (float)quadsPerAxis : 0.0f;
			AddVertex(u, 0.0f, -skirtDepth);
		}

		var bottomStart = vertices.Count;
		for (var x = 0; x < vertsPerAxis; x++)
		{
			var u = quadsPerAxis > 0 ? x / (float)quadsPerAxis : 0.0f;
			AddVertex(u, 1.0f, -skirtDepth);
		}

		var leftStart = vertices.Count;
		for (var y = 0; y < vertsPerAxis; y++)
		{
			var v = quadsPerAxis > 0 ? y / (float)quadsPerAxis : 0.0f;
			AddVertex(0.0f, v, -skirtDepth);
		}

		var rightStart = vertices.Count;
		for (var y = 0; y < vertsPerAxis; y++)
		{
			var v = quadsPerAxis > 0 ? y / (float)quadsPerAxis : 0.0f;
			AddVertex(1.0f, v, -skirtDepth);
		}

		for (var x = 0; x < vertsPerAxis - 1; x++)
		{
			AddSkirtQuad(x, x + 1, topStart + x, topStart + x + 1);
			AddSkirtQuad((vertsPerAxis - 1) * vertsPerAxis + x + 1, (vertsPerAxis - 1) * vertsPerAxis + x, bottomStart + x + 1, bottomStart + x);
		}

		for (var y = 0; y < vertsPerAxis - 1; y++)
		{
			AddSkirtQuad((y + 1) * vertsPerAxis, y * vertsPerAxis, leftStart + y + 1, leftStart + y);
			AddSkirtQuad(y * vertsPerAxis + (vertsPerAxis - 1), (y + 1) * vertsPerAxis + (vertsPerAxis - 1), rightStart + y, rightStart + y + 1);
		}

		return new Mesh(vertices, indices, normals, uvs, tangents);

		void AddVertex(float x, float z, float y)
		{
			vertices.Add(new Vector4(x, y, z, 1.0f));
			normals.Add(Vector3.UnitY);
			uvs.Add(new Vector2(x, z));
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

	private void BuildChunks()
	{
		var chunkCountX = Math.Max(1, (int)MathF.Ceiling(_resolvedWorldSize.X / _resolvedChunkSizeMeters));
		var chunkCountY = Math.Max(1, (int)MathF.Ceiling(_resolvedWorldSize.Y / _resolvedChunkSizeMeters));
		var halfWidth = _resolvedWorldSize.X * 0.5f;
		var halfDepth = _resolvedWorldSize.Y * 0.5f;

		for (var chunkY = 0; chunkY < chunkCountY; chunkY++)
		{
			for (var chunkX = 0; chunkX < chunkCountX; chunkX++)
			{
				var originX = -halfWidth + chunkX * _resolvedChunkSizeMeters;
				var originZ = -halfDepth + chunkY * _resolvedChunkSizeMeters;
				var sizeX = MathF.Min(_resolvedChunkSizeMeters, halfWidth - originX);
				var sizeZ = MathF.Min(_resolvedChunkSizeMeters, halfDepth - originZ);
				sizeX = MathF.Max(sizeX, 0.001f);
				sizeZ = MathF.Max(sizeZ, 0.001f);
				var uvOffsetX = _resolvedWorldSize.X > 1e-6f ? (originX + halfWidth) / _resolvedWorldSize.X : 0.0f;
				var uvOffsetZ = _resolvedWorldSize.Y > 1e-6f ? (originZ + halfDepth) / _resolvedWorldSize.Y : 0.0f;
				var uvScaleX = _resolvedWorldSize.X > 1e-6f ? sizeX / _resolvedWorldSize.X : 1.0f;
				var uvScaleZ = _resolvedWorldSize.Y > 1e-6f ? sizeZ / _resolvedWorldSize.Y : 1.0f;
				var bounds = CreateChunkBounds(originX, originZ, sizeX, sizeZ, _resolvedHeightScale);
				var instanceData = new TerrainChunkInstanceData(
					new Vector4(originX, originZ, sizeX, sizeZ),
					new Vector4(uvScaleX, uvScaleZ, uvOffsetX, uvOffsetZ));
				_chunks.Add(new TerrainChunkRuntime(chunkX, chunkY, bounds, instanceData));
				_rayTracingChunks.Add(new TerrainRayTracingChunkRuntime(
					chunkX,
					chunkY,
					_resolvedRayTracingResolution,
					instanceData,
					geometryRevision: 1));
			}
		}
	}

	private void ApplyPendingRayTracingDirtyRegion()
	{
		if (_pendingRayTracingDirtyRegion.HasValue == false || _rayTracingChunks.Count == 0)
		{
			return;
		}

		var dirtyRegion = _pendingRayTracingDirtyRegion.Value;
		_pendingRayTracingDirtyRegion = null;
		if (dirtyRegion.IsEmpty || dirtyRegion.TextureWidth <= 0 || dirtyRegion.TextureHeight <= 0)
		{
			return;
		}

		for (var i = 0; i < _rayTracingChunks.Count; i++)
		{
			var chunk = _rayTracingChunks[i];
			if (DirtyRegionIntersectsChunk(dirtyRegion, chunk.InstanceData))
			{
				chunk.IncrementGeometryRevision();
			}
		}
	}

	private static bool DirtyRegionIntersectsChunk(in TerrainHeightmapDirtyRegion dirtyRegion, in TerrainChunkInstanceData instanceData)
	{
		var textureMaxX = Math.Max(dirtyRegion.TextureWidth - 1, 1);
		var textureMaxY = Math.Max(dirtyRegion.TextureHeight - 1, 1);
		var dirtyMinU = dirtyRegion.X / (float)textureMaxX;
		var dirtyMinV = dirtyRegion.Y / (float)textureMaxY;
		var dirtyMaxU = (dirtyRegion.X + dirtyRegion.Width - 1) / (float)textureMaxX;
		var dirtyMaxV = (dirtyRegion.Y + dirtyRegion.Height - 1) / (float)textureMaxY;
		var uv = instanceData.HeightmapUvScaleOffset;
		var chunkMinU = uv.Z;
		var chunkMinV = uv.W;
		var chunkMaxU = uv.Z + uv.X;
		var chunkMaxV = uv.W + uv.Y;
		return dirtyMaxU >= chunkMinU &&
		       dirtyMinU <= chunkMaxU &&
		       dirtyMaxV >= chunkMinV &&
		       dirtyMinV <= chunkMaxV;
	}

	private static BoundingSphere CreateChunkBounds(float originX, float originZ, float sizeX, float sizeZ, float heightScale)
	{
		var center = new Vector3(originX + sizeX * 0.5f, heightScale * 0.5f, originZ + sizeZ * 0.5f);
		var halfExtents = new Vector3(sizeX * 0.5f, heightScale * 0.5f, sizeZ * 0.5f);
		return new BoundingSphere(center, halfExtents.Length());
	}

	private void CaptureReleasedSharedMeshes()
	{
		for (var lodIndex = 0; lodIndex < _sharedLodMeshes.Length; lodIndex++)
		{
			var mesh = _sharedLodMeshes[lodIndex];
			if (mesh is not null)
			{
				_pendingReleasedMeshes.Add(mesh);
			}
		}
	}

	private static bool ExceedsChunkTileLimit(Vector2 worldSize, float chunkSizeMeters, out int chunkTileCount)
	{
		var chunkCountX = Math.Max(1, (int)MathF.Ceiling(worldSize.X / Math.Max(chunkSizeMeters, 1.0f)));
		var chunkCountY = Math.Max(1, (int)MathF.Ceiling(worldSize.Y / Math.Max(chunkSizeMeters, 1.0f)));
		chunkTileCount = chunkCountX * chunkCountY;
		return chunkTileCount > MaxChunkTileCount;
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

	private float ResolveChunkSizeMeters(TerrainComponent component)
	{
		if (component.ChunkSizeMeters > 0.01f)
		{
			return component.GetResolvedChunkSizeMeters();
		}

		if (_resolvedHeightmap is not null &&
		    _resolvedHeightmap.Width > 1 &&
		    _resolvedHeightmap.Height > 1 &&
		    component.ChunkSizeInQuads > 0)
		{
			var legacyQuads = component.GetResolvedLegacyChunkSizeInQuads();
			var quadsX = Math.Max(_resolvedHeightmap.Width - 1, 1);
			var quadsY = Math.Max(_resolvedHeightmap.Height - 1, 1);
			var chunkCountX = Math.Max(1, (quadsX + legacyQuads - 1) / legacyQuads);
			var chunkCountY = Math.Max(1, (quadsY + legacyQuads - 1) / legacyQuads);
			var sizeX = _resolvedWorldSize.X / chunkCountX;
			var sizeY = _resolvedWorldSize.Y / chunkCountY;
			return Math.Max(1.0f, MathF.Min(sizeX, sizeY));
		}

		return component.GetResolvedChunkSizeMeters();
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
			TextureFormat.R16Unorm => DecodeR16Height(mip.Data, width, height),
			TextureFormat.Rgba8Unorm => DecodeRgba8Height(mip.Data, width, height),
			TextureFormat.Bgra8Unorm => DecodeBgra8Height(mip.Data, width, height),
			TextureFormat.Bc1Unorm => DecodeBc1Height(mip.Data, width, height),
			_ => null
		};
	}

	private static float[] DecodeR16Height(byte[] data, int width, int height)
	{
		var result = new float[width * height];
		for (var i = 0; i < result.Length; i++)
		{
			var offset = i * 2;
			var encoded = (ushort)(data[offset] | (data[offset + 1] << 8));
			result[i] = encoded / 65535.0f;
		}

		return result;
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

	private int SelectLod(TerrainChunkRuntime chunk, Matrix4x4 worldTransform, Vector3 cameraOrigin)
	{
		var center = Vector3.Transform(chunk.LocalBounds.Center, worldTransform);
		var distance = Vector3.Distance(center, cameraOrigin);
		for (var lodIndex = 0; lodIndex < _resolvedLodDistances.Length; lodIndex++)
		{
			if (distance < _resolvedLodDistances[lodIndex])
			{
				return lodIndex;
			}
		}

		return Math.Max(_resolvedLodCount - 1, 0);
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
		return _heightSamples is not null &&
		       _normals is not null &&
		       Matrix4x4.Invert(localToWorld, out worldToLocal);
	}

	private bool TrySampleLocalSurface(float localX, float localZ, out Vector3 surfacePoint, out Vector3 surfaceNormal)
	{
		surfacePoint = Vector3.Zero;
		surfaceNormal = Vector3.UnitY;
		if (_heightSamples is null ||
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

	private static Vector3 TransformNormal(Vector3 normal, Matrix4x4 worldToLocal)
	{
		var normalMatrix = Matrix4x4.Transpose(worldToLocal);
		return NormalizeDirection(Vector3.TransformNormal(normal, normalMatrix));
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

	private static Vector3 NormalizeDirection(Vector3 value)
	{
		return value.LengthSquared() > 0.0f ? Vector3.Normalize(value) : Vector3.UnitY;
	}
}

public readonly record struct TerrainRaycastHit(Vector3 Point, Vector3 Normal, float Fraction);

public sealed class TerrainChunkRuntime
{
	public TerrainChunkRuntime(int chunkX, int chunkY, BoundingSphere localBounds, TerrainChunkInstanceData instanceData)
	{
		ChunkX = chunkX;
		ChunkY = chunkY;
		LocalBounds = localBounds;
		InstanceData = instanceData;
	}

	public int ChunkX { get; }
	public int ChunkY { get; }
	public BoundingSphere LocalBounds { get; }
	public TerrainChunkInstanceData InstanceData { get; }
}

public sealed class TerrainRayTracingChunkRuntime
{
	public TerrainRayTracingChunkRuntime(
		int chunkX,
		int chunkY,
		int resolutionInQuads,
		TerrainChunkInstanceData instanceData,
		int geometryRevision)
	{
		ChunkX = chunkX;
		ChunkY = chunkY;
		ResolutionInQuads = resolutionInQuads;
		InstanceData = instanceData;
		GeometryRevision = Math.Max(geometryRevision, 1);
	}

	public int ChunkX { get; }
	public int ChunkY { get; }
	public int ResolutionInQuads { get; }
	public TerrainChunkInstanceData InstanceData { get; }
	public int GeometryRevision { get; private set; }

	public void IncrementGeometryRevision()
	{
		GeometryRevision = GeometryRevision == int.MaxValue ? 1 : GeometryRevision + 1;
	}

	public TerrainRayTracingChunkData CreateData(int chunkIndex)
	{
		return new TerrainRayTracingChunkData(
			chunkIndex,
			ResolutionInQuads,
			GeometryRevision,
			InstanceData.ChunkOriginSize,
			InstanceData.HeightmapUvScaleOffset);
	}
}

public readonly struct TerrainHeightmapDirtyRegion
{
	public TerrainHeightmapDirtyRegion(int x, int y, int width, int height, int textureWidth, int textureHeight)
	{
		TextureWidth = Math.Max(textureWidth, 0);
		TextureHeight = Math.Max(textureHeight, 0);
		if (TextureWidth == 0 || TextureHeight == 0 || width <= 0 || height <= 0)
		{
			X = 0;
			Y = 0;
			Width = 0;
			Height = 0;
			return;
		}

		var minX = Math.Clamp(x, 0, TextureWidth - 1);
		var minY = Math.Clamp(y, 0, TextureHeight - 1);
		var maxX = Math.Clamp(x + width - 1, 0, TextureWidth - 1);
		var maxY = Math.Clamp(y + height - 1, 0, TextureHeight - 1);
		X = Math.Min(minX, maxX);
		Y = Math.Min(minY, maxY);
		Width = Math.Max(0, Math.Max(minX, maxX) - X + 1);
		Height = Math.Max(0, Math.Max(minY, maxY) - Y + 1);
	}

	public int X { get; }
	public int Y { get; }
	public int Width { get; }
	public int Height { get; }
	public int TextureWidth { get; }
	public int TextureHeight { get; }
	public bool IsEmpty => Width <= 0 || Height <= 0 || TextureWidth <= 0 || TextureHeight <= 0;

	public static TerrainHeightmapDirtyRegion Union(in TerrainHeightmapDirtyRegion left, in TerrainHeightmapDirtyRegion right)
	{
		if (left.IsEmpty)
		{
			return right;
		}

		if (right.IsEmpty)
		{
			return left;
		}

		var textureWidth = Math.Max(left.TextureWidth, right.TextureWidth);
		var textureHeight = Math.Max(left.TextureHeight, right.TextureHeight);
		var minX = Math.Min(left.X, right.X);
		var minY = Math.Min(left.Y, right.Y);
		var maxX = Math.Max(left.X + left.Width - 1, right.X + right.Width - 1);
		var maxY = Math.Max(left.Y + left.Height - 1, right.Y + right.Height - 1);
		return new TerrainHeightmapDirtyRegion(minX, minY, maxX - minX + 1, maxY - minY + 1, textureWidth, textureHeight);
	}
}
