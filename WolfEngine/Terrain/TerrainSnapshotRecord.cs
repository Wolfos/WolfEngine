using System.Numerics;

namespace WolfEngine;

public sealed class TerrainSnapshotRecord
{
	public required Mesh Mesh { get; init; }
	public required Matrix4x4 WorldTransform { get; init; }
	public required Texture? ControlMap { get; init; }
	public required int LayerCount { get; init; }
	public required float HeightBlendSharpness { get; init; }
	public required TerrainResolvedLayer Layer0 { get; init; }
	public required TerrainResolvedLayer Layer1 { get; init; }
	public required TerrainResolvedLayer Layer2 { get; init; }
	public required TerrainResolvedLayer Layer3 { get; init; }
}

public readonly struct TerrainResolvedLayer
{
	public TerrainResolvedLayer(Texture? albedo, Texture? normal, Texture? metallicRoughness, Texture? occlusion, Texture? height, float scale)
	{
		Albedo = albedo;
		Normal = normal;
		MetallicRoughness = metallicRoughness;
		Occlusion = occlusion;
		Height = height;
		Scale = scale;
	}

	public Texture? Albedo { get; }
	public Texture? Normal { get; }
	public Texture? MetallicRoughness { get; }
	public Texture? Occlusion { get; }
	public Texture? Height { get; }
	public float Scale { get; }
}
