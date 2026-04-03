using System;
using ImGuiNET;
using WolfEngine.Editor.Projects;
using WolfEngine.Input;

namespace WolfEngine.Editor.UI;

public interface IEditorEntityDeletionHandler
{
	bool DeleteSelectedEntity(EditorScene scene);
}

public interface IEditorAssetDeletionHandler
{
	bool RequestDeleteSelectedItem();
}

public interface IEditorCommandService
{
	void BindDeletionHandlers(IEditorEntityDeletionHandler? entityDeletionHandler, IEditorAssetDeletionHandler? assetDeletionHandler);
	bool RequestNewScene();
	bool RequestLoadScene(Guid assetId);
	bool SaveScene();
	bool RefreshAssetDatabase();
	bool Undo();
	bool Redo();
	bool DeleteFocusedSelection();
	void ProcessShortcuts();
	void DrawPendingDialogs();
}

internal enum PendingSceneReplacementKind
{
	NewScene,
	LoadScene
}

internal enum PendingSceneReplacementDecision
{
	Save,
	Discard,
	Cancel
}

internal readonly record struct PendingSceneReplacement(PendingSceneReplacementKind Kind, Guid AssetId);

public sealed class EditorCommandService : IEditorCommandService
{
	private const string UnsavedScenePopupId = "UnsavedSceneChanges";

	private readonly IEditorSceneWorkspace _sceneWorkspace;
	private readonly IEditorProjectService _projectService;
	private readonly IEditorPlaySession _playSession;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorNotificationService _notificationService;
	private readonly IEditorUndoRedoService _undoRedoService;
	private bool _leftCtrlDown;
	private bool _rightCtrlDown;
	private bool _leftShiftDown;
	private bool _rightShiftDown;
	private bool _leftSuperDown;
	private bool _rightSuperDown;
	private bool _undoPressedThisFrame;
	private bool _redoPressedThisFrame;
	private bool _newPressedThisFrame;
	private bool _savePressedThisFrame;
	private bool _refreshPressedThisFrame;
	private bool _deletePressedThisFrame;
	private bool _backspacePressedThisFrame;

	private IEditorEntityDeletionHandler? _entityDeletionHandler;
	private IEditorAssetDeletionHandler? _assetDeletionHandler;
	private PendingSceneReplacement? _pendingSceneReplacement;
	private bool _openUnsavedScenePopup;

	public EditorCommandService(
		IEditorSceneWorkspace sceneWorkspace,
		IEditorProjectService projectService,
		IEditorPlaySession playSession,
		IEditorInteractionState interactionState,
		IEditorNotificationService notificationService,
		IEditorUndoRedoService undoRedoService,
		IInputSystem inputSystem)
	{
		_sceneWorkspace = sceneWorkspace ?? throw new ArgumentNullException(nameof(sceneWorkspace));
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_playSession = playSession ?? throw new ArgumentNullException(nameof(playSession));
		_interactionState = interactionState ?? throw new ArgumentNullException(nameof(interactionState));
		_notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
		_undoRedoService = undoRedoService ?? throw new ArgumentNullException(nameof(undoRedoService));
		ArgumentNullException.ThrowIfNull(inputSystem);

		RegisterTrackedButton(inputSystem, "ShortcutUndo", InputActionBinding.KeyZ, callback => _undoPressedThisFrame |= callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutRedo", InputActionBinding.KeyY, callback => _redoPressedThisFrame |= callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutNew", InputActionBinding.KeyN, callback => _newPressedThisFrame |= callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutSave", InputActionBinding.KeyS, callback => _savePressedThisFrame |= callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutRefresh", InputActionBinding.KeyR, callback => _refreshPressedThisFrame |= callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutDelete", InputActionBinding.KeyDelete, callback => _deletePressedThisFrame |= callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutBackspace", InputActionBinding.KeyBackspace, callback => _backspacePressedThisFrame |= callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutLeftCtrl", InputActionBinding.KeyLeftControl, callback => _leftCtrlDown = callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutRightCtrl", InputActionBinding.KeyRightControl, callback => _rightCtrlDown = callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutLeftShift", InputActionBinding.KeyLeftShift, callback => _leftShiftDown = callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutRightShift", InputActionBinding.KeyRightShift, callback => _rightShiftDown = callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutLeftSuper", InputActionBinding.KeyLeftSuper, callback => _leftSuperDown = callback.Value);
		RegisterTrackedButton(inputSystem, "ShortcutRightSuper", InputActionBinding.KeyRightSuper, callback => _rightSuperDown = callback.Value);
	}

	internal bool HasPendingSceneReplacement => _pendingSceneReplacement.HasValue;
	internal PendingSceneReplacementKind? PendingSceneReplacementType => _pendingSceneReplacement?.Kind;

	public void BindDeletionHandlers(IEditorEntityDeletionHandler? entityDeletionHandler, IEditorAssetDeletionHandler? assetDeletionHandler)
	{
		_entityDeletionHandler = entityDeletionHandler;
		_assetDeletionHandler = assetDeletionHandler;
	}

	public bool RequestNewScene()
	{
		if (_playSession.IsActive)
		{
			return false;
		}

		return QueueSceneReplacement(new PendingSceneReplacement(PendingSceneReplacementKind.NewScene, Guid.Empty));
	}

	public bool RequestLoadScene(Guid assetId)
	{
		if (_playSession.IsActive)
		{
			_notificationService.ReportError("Stop play mode before loading another scene.");
			return false;
		}

		if (assetId == Guid.Empty)
		{
			return false;
		}

		return QueueSceneReplacement(new PendingSceneReplacement(PendingSceneReplacementKind.LoadScene, assetId));
	}

	public bool SaveScene()
	{
		if (CanSaveScene() == false)
		{
			return false;
		}

		try
		{
			_sceneWorkspace.SaveCurrentScene();
			_interactionState.ClearSceneDirty();
			return true;
		}
		catch (Exception ex)
		{
			_notificationService.ReportError($"Failed to save scene: {ex.Message}");
			return false;
		}
	}

	public bool RefreshAssetDatabase()
	{
		if (_projectService.HasOpenProject == false || _playSession.IsActive)
		{
			return false;
		}

		try
		{
			_projectService.ReloadAssetDatabase();
			return true;
		}
		catch (Exception ex)
		{
			_notificationService.ReportError($"Failed to refresh asset database: {ex.Message}");
			return false;
		}
	}

	public bool Undo()
	{
		return _undoRedoService.Undo();
	}

	public bool Redo()
	{
		return _undoRedoService.Redo();
	}

	public bool DeleteFocusedSelection()
	{
		return _interactionState.FocusedWindow switch
		{
			EditorFocusedWindow.Entities => _entityDeletionHandler?.DeleteSelectedEntity(_sceneWorkspace.CurrentScene) ?? false,
			EditorFocusedWindow.Assets => _assetDeletionHandler?.RequestDeleteSelectedItem() ?? false,
			_ => false
		};
	}

	public void ProcessShortcuts()
	{
		var io = ImGui.GetIO();
		var snapshot = new EditorShortcutSnapshot(
			IsPrimaryModifierDown(),
			IsShiftDown(),
			_undoPressedThisFrame,
			_redoPressedThisFrame,
			_newPressedThisFrame,
			_savePressedThisFrame,
			_refreshPressedThisFrame,
			_deletePressedThisFrame,
			_backspacePressedThisFrame,
			io.WantTextInput,
			OperatingSystem.IsMacOS());
		var shortcut = EditorShortcutCommandResolver.Resolve(snapshot, _interactionState.FocusedWindow);
		_lastShortcutCommand = shortcut;

		switch (shortcut)
		{
			case EditorShortcutCommand.Undo:
				Undo();
				break;
			case EditorShortcutCommand.Redo:
				Redo();
				break;
			case EditorShortcutCommand.NewScene:
				RequestNewScene();
				break;
			case EditorShortcutCommand.SaveScene:
				SaveScene();
				break;
			case EditorShortcutCommand.RefreshAssetDatabase:
				RefreshAssetDatabase();
				break;
			case EditorShortcutCommand.DeleteFocusedSelection:
				DeleteFocusedSelection();
				break;
		}

		_undoPressedThisFrame = false;
		_redoPressedThisFrame = false;
		_newPressedThisFrame = false;
		_savePressedThisFrame = false;
		_refreshPressedThisFrame = false;
		_deletePressedThisFrame = false;
		_backspacePressedThisFrame = false;
	}

	public void DrawPendingDialogs()
	{
		if (_openUnsavedScenePopup)
		{
			ImGui.OpenPopup(UnsavedScenePopupId);
			_openUnsavedScenePopup = false;
		}

		var isOpen = true;
		ImGui.SetNextWindowSize(new System.Numerics.Vector2(460.0f, 0.0f), ImGuiCond.Appearing);
		if (ImGui.BeginPopupModal(UnsavedScenePopupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize) == false)
		{
			return;
		}

		ImGui.TextWrapped("The current scene has unsaved changes.");
		ImGui.Spacing();

		var canSave = CanSaveScene();
		if (canSave == false)
		{
			ImGui.BeginDisabled();
		}

		if (ImGui.Button("Save", new System.Numerics.Vector2(100.0f, 0.0f)))
		{
			if (ResolvePendingSceneReplacement(PendingSceneReplacementDecision.Save))
			{
				ImGui.CloseCurrentPopup();
			}
		}

		if (canSave == false)
		{
			ImGui.EndDisabled();
		}

		ImGui.SameLine();
		if (ImGui.Button("Don't Save", new System.Numerics.Vector2(100.0f, 0.0f)))
		{
			ResolvePendingSceneReplacement(PendingSceneReplacementDecision.Discard);
			ImGui.CloseCurrentPopup();
		}

		ImGui.SameLine();
		if (ImGui.Button("Cancel", new System.Numerics.Vector2(100.0f, 0.0f)))
		{
			ResolvePendingSceneReplacement(PendingSceneReplacementDecision.Cancel);
			ImGui.CloseCurrentPopup();
		}

		if (canSave == false)
		{
			ImGui.Spacing();
			ImGui.TextDisabled("Saving is unavailable without an open project.");
		}

		ImGui.EndPopup();
	}

	internal bool ResolvePendingSceneReplacement(PendingSceneReplacementDecision decision)
	{
		if (_pendingSceneReplacement is not { } replacement)
		{
			return false;
		}

		switch (decision)
		{
			case PendingSceneReplacementDecision.Cancel:
				_pendingSceneReplacement = null;
				return false;
			case PendingSceneReplacementDecision.Save:
				if (SaveScene() == false)
				{
					return false;
				}

				break;
		}

		ExecuteSceneReplacement(replacement);
		_pendingSceneReplacement = null;
		return true;
	}

	private bool QueueSceneReplacement(PendingSceneReplacement replacement)
	{
		if (_interactionState.IsSceneDirty == false)
		{
			ExecuteSceneReplacement(replacement);
			return true;
		}

		_pendingSceneReplacement = replacement;
		_openUnsavedScenePopup = true;
		return false;
	}

	private void ExecuteSceneReplacement(PendingSceneReplacement replacement)
	{
		try
		{
			_undoRedoService.Clear();
			switch (replacement.Kind)
			{
				case PendingSceneReplacementKind.NewScene:
					_sceneWorkspace.ResetToNewScene();
					EditorGui.ClearEntitySelection();
					break;
				case PendingSceneReplacementKind.LoadScene:
					_sceneWorkspace.LoadScene(replacement.AssetId);
					EditorGui.ClearEntitySelection();
					break;
			}

			_interactionState.ClearSceneDirty();
		}
		catch (Exception ex)
		{
			_notificationService.ReportError(replacement.Kind switch
			{
				PendingSceneReplacementKind.NewScene => $"Failed to create scene: {ex.Message}",
				_ => $"Failed to load scene: {ex.Message}"
			});
		}
	}

	private bool CanSaveScene()
	{
		return _projectService.HasOpenProject && _playSession.IsActive == false;
	}

	private bool IsPrimaryModifierDown()
	{
		return _leftCtrlDown || _rightCtrlDown || _leftSuperDown || _rightSuperDown;
	}

	private bool IsShiftDown()
	{
		return _leftShiftDown || _rightShiftDown;
	}

	private static void RegisterTrackedButton(IInputSystem inputSystem, string name, InputActionBinding binding, Action<InputActionCallback<bool>> handler)
	{
		inputSystem.RegisterButton(new InputAction
		{
			Name = name,
			Type = InputActionType.Button,
			Bindings = [binding]
		}, handler);
	}
}
