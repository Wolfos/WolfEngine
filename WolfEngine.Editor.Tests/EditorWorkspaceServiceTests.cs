using System.Text.Json;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EditorWorkspaceServiceTests
{
	[Test]
	public void MissingSettingsSeedSceneAndAssets()
	{
		var service = new EditorWorkspaceService((EditorWorkspacePreferences?)null);

		Assert.That(service.Workspaces.Select(workspace => workspace.Name), Is.EqualTo(new[] { "Scene", "Assets", "Animation Graph", "Animate" }));
		Assert.That(service.ActiveWorkspace.Name, Is.EqualTo("Scene"));
		Assert.That(service.Workspaces[0].OpenWindows, Does.Contain(EditorWindowIds.Scene));
		Assert.That(service.Workspaces[1].OpenWindows, Does.Contain(EditorWindowIds.Assets));
		Assert.That(service.Workspaces[2].OpenWindows, Is.EquivalentTo(new[] { EditorWindowIds.Animation }));
		Assert.That(service.Workspaces[3].OpenWindows, Is.EquivalentTo(EditorWindowIds.AnimationClipEditor));
	}

	[Test]
	public void CreateRenameAndReorderKeepStableIdentity()
	{
		var service = new EditorWorkspaceService((EditorWorkspacePreferences?)null);
		Assert.That(service.TryCreate("  Lighting  ", out var created, out _), Is.True);
		var id = created!.Id;

		Assert.That(service.TryRename(id, "World Building", out _), Is.True);
		Assert.That(service.Move(id, 0), Is.True);

		Assert.That(service.Workspaces[0].Id, Is.EqualTo(id));
		Assert.That(service.Workspaces[0].Name, Is.EqualTo("World Building"));
		Assert.That(service.ActiveWorkspace.Id, Is.EqualTo(id));
	}

	[TestCase("")]
	[TestCase("   ")]
	[TestCase("scene")]
	public void InvalidOrDuplicateNamesAreRejected(string name)
	{
		var service = new EditorWorkspaceService((EditorWorkspacePreferences?)null);

		Assert.That(service.TryCreate(name, out _, out var error), Is.False);
		Assert.That(error, Is.Not.Empty);
	}

	[Test]
	public void WindowVisibilityIsIndependentPerWorkspace()
	{
		var service = new EditorWorkspaceService((EditorWorkspacePreferences?)null);
		service.CloseWindow(EditorWindowIds.Log);
		service.Activate(EditorWorkspaceService.AssetsWorkspaceId);

		Assert.That(service.IsWindowOpen(EditorWindowIds.Log), Is.True);
		service.OpenWindow(EditorWindowIds.Profiler);
		service.Activate(EditorWorkspaceService.SceneWorkspaceId);
		Assert.That(service.IsWindowOpen(EditorWindowIds.Profiler), Is.False);
	}

	[Test]
	public void DeletingActiveWorkspaceSelectsPrecedingWorkspace()
	{
		var service = new EditorWorkspaceService((EditorWorkspacePreferences?)null);
		service.TryCreate("Third", out var third, out _);

		Assert.That(service.Delete(third!.Id), Is.True);
		Assert.That(service.ActiveWorkspace.Name, Is.EqualTo("Animate"));
	}

	[Test]
	public void LastWorkspaceCannotBeDeleted()
	{
		var settings = new EditorWorkspacePreferences
		{
			Version = 1,
            AnimationWorkspaceSeeded = true,
			AnimateWorkspaceSeeded = true,
			ActiveWorkspaceId = Guid.Parse("118ed69b-4212-4cbf-a7bb-415ed4c9dd31"),
			Workspaces =
			[
				new EditorWorkspacePreference
				{
					Id = Guid.Parse("118ed69b-4212-4cbf-a7bb-415ed4c9dd31"),
					Name = "Only"
				}
			]
		};
		var service = new EditorWorkspaceService(settings);

		Assert.That(service.Delete(service.ActiveWorkspace.Id), Is.False);
		Assert.That(service.Workspaces, Has.Count.EqualTo(1));
	}

	[Test]
	public void RestoreDropsInvalidRecordsAndUnknownWindows()
	{
		var id = Guid.NewGuid();
		var settings = new EditorWorkspacePreferences
		{
			Version = 1,
            AnimationWorkspaceSeeded = true,
			AnimateWorkspaceSeeded = true,
			ActiveWorkspaceId = id,
			Workspaces =
			[
				new EditorWorkspacePreference { Id = Guid.Empty, Name = "Broken" },
				new EditorWorkspacePreference { Id = id, Name = "Valid", OpenWindowIds = [EditorWindowIds.Scene, "old-window"] }
			]
		};

		var service = new EditorWorkspaceService(settings);

		Assert.That(service.Workspaces, Has.Count.EqualTo(1));
		Assert.That(service.ActiveWorkspace.OpenWindows, Is.EquivalentTo(new[] { EditorWindowIds.Scene }));
	}

	[Test]
	public void PreferencesDtoRoundTripsWorkspaceIdentityVisibilityAndLayout()
	{
		var id = Guid.NewGuid();
		var settings = new EditorWorkspacePreferences
		{
			Version = 1,
            AnimationWorkspaceSeeded = true,
			AnimateWorkspaceSeeded = true,
			ActiveWorkspaceId = id,
			ImGuiSettings = "[Docking][Data]",
			Workspaces = [new EditorWorkspacePreference { Id = id, Name = "Lighting", OpenWindowIds = [EditorWindowIds.Scene] }]
		};

		var restored = JsonSerializer.Deserialize<EditorWorkspacePreferences>(JsonSerializer.Serialize(settings));

		Assert.That(restored, Is.Not.Null);
		Assert.That(restored!.ActiveWorkspaceId, Is.EqualTo(id));
		Assert.That(restored.ImGuiSettings, Is.EqualTo("[Docking][Data]"));
		Assert.That(restored.Workspaces.Single().OpenWindowIds, Is.EqualTo(new[] { EditorWindowIds.Scene }));
	}

	[Test]
	public void ExistingSettingsGainTheAnimateWorkspaceOnce()
	{
		var settings = new EditorWorkspacePreferences
		{
			Version = 1,
			AnimationWorkspaceSeeded = true,
			ActiveWorkspaceId = EditorWorkspaceService.SceneWorkspaceId,
			Workspaces =
			[
				new EditorWorkspacePreference { Id = EditorWorkspaceService.SceneWorkspaceId, Name = "Scene" }
			]
		};

		var service = new EditorWorkspaceService(settings);

		var animate = service.Workspaces.Single(workspace => workspace.Id == EditorWorkspaceService.AnimateWorkspaceId);
		Assert.That(animate.Name, Is.EqualTo("Animate"));
		Assert.That(animate.OpenWindows, Is.EquivalentTo(EditorWindowIds.AnimationClipEditor));
		Assert.That(service.ActiveWorkspace.Id, Is.EqualTo(EditorWorkspaceService.SceneWorkspaceId));

		service.Delete(EditorWorkspaceService.AnimateWorkspaceId);
		var restored = new EditorWorkspaceService(EditorPreferences.GetWorkspaceSettings());

		Assert.That(restored.Workspaces.Any(workspace => workspace.Id == EditorWorkspaceService.AnimateWorkspaceId), Is.False);
	}

	[Test]
	public void LegacyAnimationWorkspaceIsRenamedToAnimationGraph()
	{
		var settings = new EditorWorkspacePreferences
		{
			Version = 1,
			AnimationWorkspaceSeeded = true,
			AnimateWorkspaceSeeded = true,
			ActiveWorkspaceId = EditorWorkspaceService.AnimationWorkspaceId,
			Workspaces =
			[
				new EditorWorkspacePreference
				{
					Id = EditorWorkspaceService.AnimationWorkspaceId,
					Name = "Animation",
					OpenWindowIds = [EditorWindowIds.Animation]
				}
			]
		};

		var service = new EditorWorkspaceService(settings);

		Assert.That(service.ActiveWorkspace.Name, Is.EqualTo("Animation Graph"));
	}

	[Test]
	public void CustomGraphWorkspaceNameIsKept()
	{
		var settings = new EditorWorkspacePreferences
		{
			Version = 1,
			AnimationWorkspaceSeeded = true,
			AnimateWorkspaceSeeded = true,
			ActiveWorkspaceId = EditorWorkspaceService.AnimationWorkspaceId,
			Workspaces =
			[
				new EditorWorkspacePreference
				{
					Id = EditorWorkspaceService.AnimationWorkspaceId,
					Name = "State Machines",
					OpenWindowIds = [EditorWindowIds.Animation]
				}
			]
		};

		var service = new EditorWorkspaceService(settings);

		Assert.That(service.ActiveWorkspace.Name, Is.EqualTo("State Machines"));
	}

	[Test]
	public void RenamedGraphWorkspaceIsNotRenamedAgain()
	{
		var settings = new EditorWorkspacePreferences
		{
			Version = 1,
			AnimationWorkspaceSeeded = true,
			AnimationGraphWorkspaceRenamed = true,
			AnimateWorkspaceSeeded = true,
			ActiveWorkspaceId = EditorWorkspaceService.AnimationWorkspaceId,
			Workspaces =
			[
				new EditorWorkspacePreference
				{
					Id = EditorWorkspaceService.AnimationWorkspaceId,
					Name = "Animation",
					OpenWindowIds = [EditorWindowIds.Animation]
				}
			]
		};

		var service = new EditorWorkspaceService(settings);

		Assert.That(service.ActiveWorkspace.Name, Is.EqualTo("Animation"));
	}
}
