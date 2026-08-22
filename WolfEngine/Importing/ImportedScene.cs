using System.Collections.Generic;
using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.Rendering;

namespace WolfEngine.Importing;

public enum TextureSemantic
{
	Unknown = 0,
	BaseColor,
	Normal,
	MetallicRoughness,
	Occlusion,
	Emissive,
	BaseColorTransparent
}

public record ImportedScene(
	string Name,
	List<ImportedMaterial> Materials,
	List<ImportedTexture> Textures,
	List<ImportedNode> Nodes,
	List<ImportedSkeleton> Skeletons,
	List<ImportedAnimation> Animations
);

public record struct ImportedMaterial(
	ColorRGBA BaseColor,
	float MetallicFactor,
	float RoughnessFactor,
	float NormalScale,
	Vector3 EmissiveFactor,
	float EmissiveIntensity,
	int? BaseColorTextureIndex,
	int? NormalTextureIndex,
	int? MetallicRoughnessTextureIndex,
	int? OcclusionTextureIndex,
	int? EmissiveTextureIndex,
	AlphaMode AlphaMode,
	float AlphaCutoff
);

public record struct ImportedTexture(
	string NameOrPath,
	int Width,
	int Height,
	bool IsSrgb,
	TextureSemantic Semantic,
	TextureMipData[] MipLevels)
{
	public int Channels => 4;
	public int MipCount => MipLevels?.Length ?? 0;
	public byte[] PixelData => MipLevels is { Length: > 0 } ? MipLevels[0].Data : [];
}

public record ImportedNode(
	string Name,
	Matrix4x4 LocalTransform,
	List<ImportedNodeMesh> Meshes,
	int ParentIndex
);

public record struct ImportedNodeMesh(
	string Name,
	Mesh Mesh,
	int MaterialIndex,
	int SkeletonIndex = -1
);

/// <summary>
/// A bone hierarchy lifted out of the source node graph. Bones deliberately do not appear in
/// <see cref="ImportedScene.Nodes"/>, so they never become entities.
/// </summary>
public record ImportedSkeleton(
	string Name,
	string[] BoneNames,
	int[] ParentIndices,
	BoneTransform[] BindPoseLocal,
	Matrix4x4[] InverseBindMatrices
);

public record ImportedAnimation(
	string Name,
	float Duration,
	float FramesPerSecond,
	int SkeletonIndex,
	TransformTrack[] TransformTracks,
	PropertyTrack[] PropertyTracks
);
