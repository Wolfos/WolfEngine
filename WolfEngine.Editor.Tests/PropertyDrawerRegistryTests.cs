using System;
using System.Collections.Generic;
using System.Linq;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor.Tests;

public sealed class PropertyDrawerRegistryTests
{
	[Test]
	public void GetPreviewLabelReturnsNoneForEmptyReference()
	{
		var descriptor = RuntimeAssetDescriptor.Get(typeof(Mesh));

		var result = AssetLinkPickerLogic.GetPreviewLabel(
			hasOpenProject: true,
			Guid.Empty,
			asset: null,
			descriptor,
			descriptor.AuthoringType.AssemblyQualifiedName ?? string.Empty,
			authoringTypeId: string.Empty);

		Assert.That(result, Is.EqualTo("None"));
	}

	[Test]
	public void GetPreviewLabelReturnsMissingWhenProjectIsClosed()
	{
		var descriptor = RuntimeAssetDescriptor.Get(typeof(Mesh));
		var asset = CreateAsset("Mesh A", AssetType.Mesh);

		var result = AssetLinkPickerLogic.GetPreviewLabel(
			hasOpenProject: false,
			asset.Id,
			asset,
			descriptor,
			descriptor.AuthoringType.AssemblyQualifiedName ?? string.Empty,
			authoringTypeId: string.Empty);

		Assert.That(result, Is.EqualTo("Missing"));
	}

	[Test]
	public void GetPreviewLabelReturnsInvalidForIncompatibleAsset()
	{
		var descriptor = RuntimeAssetDescriptor.Get(typeof(Mesh));
		var asset = CreateAsset("Texture A", AssetType.Texture2D);

		var result = AssetLinkPickerLogic.GetPreviewLabel(
			hasOpenProject: true,
			asset.Id,
			asset,
			descriptor,
			descriptor.AuthoringType.AssemblyQualifiedName ?? string.Empty,
			authoringTypeId: string.Empty);

		Assert.That(result, Is.EqualTo("Invalid"));
	}

	[Test]
	public void GetPreviewLabelReturnsAssignedAssetNameForCompatibleAsset()
	{
		var descriptor = RuntimeAssetDescriptor.Get(typeof(Mesh));
		var asset = CreateAsset("Mesh A", AssetType.Mesh);

		var result = AssetLinkPickerLogic.GetPreviewLabel(
			hasOpenProject: true,
			asset.Id,
			asset,
			descriptor,
			descriptor.AuthoringType.AssemblyQualifiedName ?? string.Empty,
			authoringTypeId: string.Empty);

		Assert.That(result, Is.EqualTo("Mesh A"));
	}

	[Test]
	public void GetCandidatesFiltersToCompatibleDataAssetSubtype()
	{
		var descriptor = RuntimeAssetDescriptor.Get(typeof(RenderConfig));
		var authoringTypeName = descriptor.AuthoringType.AssemblyQualifiedName ?? string.Empty;
		const string authoringTypeId = "render-config";
		var candidates = AssetLinkPickerLogic.GetCandidates(
			new List<AssetDatabaseEntry>
			{
				CreateDataAsset("Render Config", authoringTypeName, authoringTypeId),
				CreateDataAsset("Other Data", "Some.Other.Type", "other-type"),
				CreateAsset("Texture A", AssetType.Texture2D)
			},
			descriptor,
			authoringTypeName,
			authoringTypeId);

		Assert.That(candidates.Select(asset => asset.Name), Is.EqualTo(new[] { "Render Config" }));
	}

	[Test]
	public void GetCandidatesSortsByNameIgnoringCase()
	{
		var descriptor = RuntimeAssetDescriptor.Get(typeof(Mesh));
		var candidates = AssetLinkPickerLogic.GetCandidates(
			new List<AssetDatabaseEntry>
			{
				CreateAsset("zeta", AssetType.Mesh),
				CreateAsset("Alpha", AssetType.Mesh),
				CreateAsset("beta", AssetType.Mesh)
			},
			descriptor,
			descriptor.AuthoringType.AssemblyQualifiedName ?? string.Empty,
			authoringTypeId: string.Empty);

		Assert.That(candidates.Select(asset => asset.Name), Is.EqualTo(new[] { "Alpha", "beta", "zeta" }));
	}

	[Test]
	public void MatchesSearchUsesCaseInsensitiveNameMatchingOnly()
	{
		Assert.Multiple(() =>
		{
			Assert.That(AssetLinkPickerLogic.MatchesSearch("Main Camera", "camera"), Is.True);
			Assert.That(AssetLinkPickerLogic.MatchesSearch("Main Camera", "Assets/Cameras"), Is.False);
		});
	}

	[Test]
	public void CreateAssetLinkValueAssignsSelectedCandidateId()
	{
		var assetId = Guid.NewGuid();

		var value = PropertyDrawerRegistry.CreateAssetLinkValue(typeof(AssetRef<Mesh>), assetId);

		Assert.That(value, Is.TypeOf<AssetRef<Mesh>>());
		Assert.That(PropertyDrawerRegistry.GetAssetLinkId(typeof(AssetRef<Mesh>), value), Is.EqualTo(assetId));
	}

	[Test]
	public void CreateAssetLinkValueAssignsEmptyGuidWhenClearing()
	{
		var value = PropertyDrawerRegistry.CreateAssetLinkValue(typeof(AssetRef<Mesh>), Guid.Empty);

		Assert.That(value, Is.TypeOf<AssetRef<Mesh>>());
		Assert.That(PropertyDrawerRegistry.GetAssetLinkId(typeof(AssetRef<Mesh>), value), Is.EqualTo(Guid.Empty));
	}

	[Test]
	public void EntityPreviewLabelReturnsNoneForDefaultEntity()
	{
		var scene = new EditorScene();

		var label = EntityLinkPickerLogic.GetPreviewLabel(scene, default, entityId: null);

		Assert.That(label, Is.EqualTo("None"));
	}

	[Test]
	public void EntityPreviewLabelReturnsMissingForUntrackedEntity()
	{
		var scene = new EditorScene { World = new World(WorldTag.Authoring) };
		var entity = scene.World.CreateEntity("Target");

		var label = EntityLinkPickerLogic.GetPreviewLabel(scene, entity, entityId: null);

		Assert.That(label, Is.EqualTo("Missing"));
	}

	[Test]
	public void EntityCandidatesIncludeOwnerAndSortByDisplayName()
	{
		var scene = new EditorScene { World = new World(WorldTag.Authoring) };
		var owner = scene.World.CreateEntity("Owner");
		var beta = scene.World.CreateEntity("beta");
		var unnamed = scene.World.CreateEntity();
		var alpha = scene.World.CreateEntity("Alpha");
		scene.EntityIds[owner] = Guid.NewGuid();
		scene.EntityIds[beta] = Guid.Parse("11111111-1111-1111-1111-111111111111");
		scene.EntityIds[unnamed] = Guid.Parse("22222222-2222-2222-2222-222222222222");
		scene.EntityIds[alpha] = Guid.Parse("33333333-3333-3333-3333-333333333333");

		var candidates = EntityLinkPickerLogic.GetCandidates(scene, owner);

		Assert.That(candidates.Select(candidate => candidate.Entity), Does.Contain(owner));
		Assert.That(candidates.Select(candidate => candidate.DisplayName), Is.EqualTo(new[] { "Alpha", "beta", "Entity 22222222", "Owner" }));
	}

	[Test]
	public void EntityCandidatesFilterByRequiredComponentTypeWhenProvided()
	{
		var scene = new EditorScene { World = new World(WorldTag.Authoring) };
		var lightEntity = scene.World.CreateEntity("Light");
		scene.World.AddComponent(lightEntity, new Light());
		var plainEntity = scene.World.CreateEntity("Plain");
		scene.EntityIds[lightEntity] = Guid.NewGuid();
		scene.EntityIds[plainEntity] = Guid.NewGuid();

		var candidates = EntityLinkPickerLogic.GetCandidates(scene, ownerEntity: null, typeof(Light));

		Assert.That(candidates.Select(candidate => candidate.Entity), Is.EqualTo(new[] { lightEntity }));
	}

	private static AssetDatabaseEntry CreateAsset(string name, AssetType assetType)
	{
		return new AssetDatabaseEntry
		{
			Id = Guid.NewGuid(),
			SourceId = Guid.NewGuid(),
			Name = name,
			Type = assetType
		};
	}

	private static AssetDatabaseEntry CreateDataAsset(string name, string dataAssetType, string dataAssetTypeId)
	{
		return new AssetDatabaseEntry
		{
			Id = Guid.NewGuid(),
			SourceId = Guid.NewGuid(),
			Name = name,
			Type = AssetType.DataAsset,
			DataAssetSummary = new DataAssetSummary
			{
				DataAssetType = dataAssetType,
				DataAssetTypeId = dataAssetTypeId
			}
		};
	}
}
