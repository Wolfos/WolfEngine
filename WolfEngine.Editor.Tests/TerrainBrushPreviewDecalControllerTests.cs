#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using NSubstitute;
using WolfEngine;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class TerrainBrushPreviewDecalControllerTests
{
	[Test]
	public void ApplyPreview_ConfiguresTransientDecalFromBrushSettings()
	{
		var controller = new TerrainBrushPreviewDecalController(new TestTextureFactory());
		var terrain = new TerrainComponent
		{
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 8.0f
		};

		controller.ApplyPreview(
			ref terrain,
			new Vector3(2.0f, 1.0f, -3.0f),
			12.0f,
			1.5f);

		Assert.That(terrain.AuthoringBrushPreviewDecal.HasValue, Is.True);
		var decal = terrain.AuthoringBrushPreviewDecal!.Value;
		Assert.That(decal.ChannelMask, Is.EqualTo(DecalChannelMask.Albedo | DecalChannelMask.Emissive));
		Assert.That(decal.Size.X, Is.EqualTo(24.0f).Within(0.0001f));
		Assert.That(decal.Size.Y, Is.EqualTo(24.0f).Within(0.0001f));
		Assert.That(decal.Size.Z, Is.GreaterThanOrEqualTo(16.0f));
		Assert.That(decal.AlbedoTexture, Is.Not.Null);
		Assert.That(decal.EmissiveTexture, Is.Not.Null);
		Assert.That(terrain.AuthoringBrushPreviewLocalTransform.Translation.X, Is.EqualTo(2.0f).Within(0.0001f));
		Assert.That(terrain.AuthoringBrushPreviewLocalTransform.Translation.Y, Is.EqualTo(1.0f).Within(0.0001f));
		Assert.That(terrain.AuthoringBrushPreviewLocalTransform.Translation.Z, Is.EqualTo(-3.0f).Within(0.0001f));
	}

	[Test]
	public void PreviewMasks_MatchBrushFalloffAndBoundaryRim()
	{
		Assert.That(TerrainBrushPreviewDecalController.ComputeFillMask(0.5f, 2.0f), Is.EqualTo(0.25f).Within(0.0001f));
		Assert.That(TerrainBrushPreviewDecalController.ComputeFillMask(1.0f, 2.0f), Is.EqualTo(0.0f).Within(0.0001f));
		Assert.That(TerrainBrushPreviewDecalController.ComputeRimMask(0.5f), Is.EqualTo(0.0f).Within(0.0001f));
		Assert.That(TerrainBrushPreviewDecalController.ComputeRimMask(0.99f), Is.GreaterThan(0.5f));
	}

	[Test]
	public void CollectDecalProjectors_AddsTerrainBrushPreviewDecal()
	{
		var world = new World(WorldTag.Authoring);
		var terrainEntity = world.CreateEntity("Terrain");
		world.AddComponent(terrainEntity, new WorldTransform
		{
			LocalToWorld = Matrix4x4.CreateTranslation(10.0f, 0.0f, 4.0f),
			WorldToLocal = Matrix4x4.CreateTranslation(-10.0f, 0.0f, -4.0f)
		});
		world.AddComponent(terrainEntity, new TerrainComponent
		{
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 8.0f,
			AuthoringBrushPreviewDecal = new DecalProjector
			{
				Enabled = true,
				Size = new Vector3(8.0f, 8.0f, 16.0f),
				AlbedoTexture = CreateTexture("albedo"),
				EmissiveTexture = CreateTexture("emissive"),
				ChannelMask = DecalChannelMask.Albedo | DecalChannelMask.Emissive,
				EmissiveOpacity = 1.0f
			},
			AuthoringBrushPreviewLocalTransform = Matrix4x4.CreateTranslation(2.0f, 3.0f, -1.0f)
		});

		var snapshot = new FrameSnapshot();
		var resourceScheduler = Substitute.For<IRenderResourceScheduler>();
		RenderPipeline.CollectDecalProjectors(snapshot, world, resourceScheduler);

		Assert.That(snapshot.DecalPackets, Has.Count.EqualTo(1));
		var expected = Matrix4x4.CreateTranslation(2.0f, 3.0f, -1.0f) * Matrix4x4.CreateTranslation(10.0f, 0.0f, 4.0f);
		Assert.That(snapshot.DecalPackets[0].Transform.Translation.X, Is.EqualTo(expected.Translation.X).Within(0.0001f));
		Assert.That(snapshot.DecalPackets[0].Transform.Translation.Y, Is.EqualTo(expected.Translation.Y).Within(0.0001f));
		Assert.That(snapshot.DecalPackets[0].Transform.Translation.Z, Is.EqualTo(expected.Translation.Z).Within(0.0001f));
	}

	private static Texture CreateTexture(string name)
	{
		return new Texture(
			name,
			1,
			1,
			true,
			TextureFormat.Rgba8Unorm,
			[new TextureMipData(1, 1, [255, 255, 255, 255])]);
	}

	private sealed class TestTextureFactory : ITextureFactory
	{
		private readonly Dictionary<string, Texture> _textures = new(StringComparer.Ordinal);

		public Texture GetTexture(ImportedTexture importedTexture) => throw new NotSupportedException();

		public Texture GetTexture(Texture texture)
		{
			_textures[texture.Name] = texture;
			return texture;
		}

		public Texture GetWhiteTexture() => CreateTexture("white");
		public Texture GetBlackTexture() => CreateTexture("black");
		public Texture GetNeutralNormalTexture() => CreateTexture("normal");
		public Texture LoadFromFile(string path, bool isSrgb = false) => throw new NotSupportedException();
	}
}
