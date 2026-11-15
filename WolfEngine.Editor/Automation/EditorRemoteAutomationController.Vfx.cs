using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.Vfx;

namespace WolfEngine.Editor.Automation;

public sealed record VfxPlayResult(string AssetPath, Guid AssetId, bool Playing, VfxStateResult State);

public sealed record VfxPreviewStateResult(
	bool Open,
	float Time,
	bool Playing,
	bool EffectPlaying,
	string Motion,
	int ReservedParticles,
	int SelectedEmitter);

public sealed record VfxStateResult(
	bool PlayMode,
	int ActiveEffects,
	int ReservedParticles,
	int ParticleCapacity,
	long EditorFrameSequence,
	long RenderFrameSequence);

/// <summary>Effect automation. Effects advance with the gameplay world, so they play in Play mode.</summary>
public sealed partial class EditorRemoteAutomationController
{
	public Task<VfxPlayResult> PlayVfxAsync(
		string assetPath,
		Vector3 position,
		string? attachToEntityId,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.RuntimeScene
				?? throw new InvalidOperationException("Effects play in Play mode. Call enter_play_mode first.");
			var asset = ResolveVfxAsset(assetPath);
			var effect = new AssetRef<VfxAsset>
			{
				NodeId = asset.Id
			};

			VfxHandle handle;
			if (string.IsNullOrWhiteSpace(attachToEntityId))
			{
				handle = _vfx.Play(scene.World, effect, position, Quaternion.Identity);
			}
			else
			{
				if (Guid.TryParse(attachToEntityId, out var persistentId) == false)
				{
					throw new InvalidOperationException("attach_to_entity_id must be a GUID.");
				}

				var entity = scene.EntityIds.FirstOrDefault(entry => entry.Value == persistentId).Key;
				if (entity.IsValid == false || scene.World.IsAlive(entity) == false)
				{
					throw new InvalidOperationException($"Entity '{persistentId:D}' is not in the Play-mode scene.");
				}

				handle = _vfx.Play(
					scene.World,
					effect,
					VfxPlacement.AttachedTo(entity, Matrix4x4.CreateTranslation(position)));
			}

			return new VfxPlayResult(asset.RelativeAssetPath, asset.Id, _vfx.IsPlaying(handle), CreateVfxState());
		}, cancellationToken);

	public Task<VfxStateResult> GetVfxStateAsync(CancellationToken cancellationToken) =>
		Enqueue(CreateVfxState, cancellationToken);

	public Task<VfxStateResult> StopAllVfxAsync(bool immediate, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (_playSession.RuntimeScene is { } scene)
			{
				_vfx.StopAll(scene.World, immediate);
			}

			return CreateVfxState();
		}, cancellationToken);

	/// <summary>
	/// Configures the VFX editor's preview. Select the asset with select_asset and wait for render frames first, so
	/// the Asset Editor has drawn it and created the preview.
	/// </summary>
	public Task<VfxPreviewStateResult> ConfigureVfxPreviewAsync(
		string? motion,
		bool? playing,
		float? advanceSeconds,
		Vector3? orbit,
		Guid? modelId,
		string? attachTo,
		bool restart,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var preview = _vfxEditor.Preview
				?? throw new InvalidOperationException("No VFX preview is open. Select a VFX asset and wait for render frames.");
			if (motion is not null)
			{
				preview.Motion = Enum.Parse<VfxPreviewMotion>(motion, ignoreCase: true);
			}

			if (orbit is { } angles)
			{
				_vfxEditor.SetOrbit(angles.X, angles.Y, angles.Z);
			}

			if (modelId is { } model)
			{
				_vfxEditor.SetPreviewModel(model, attachTo);
			}

			if (restart)
			{
				preview.Restart(_vfxEditor.PreviewedAsset);
			}

			if (playing is { } play)
			{
				preview.Playing = play;
			}

			if (advanceSeconds is > 0.0f)
			{
				// Fixed steps, as the game would take them, rather than one long jump.
				var remaining = advanceSeconds.Value;
				while (remaining > 1e-4f)
				{
					var step = MathF.Min(remaining, 1.0f / 60.0f);
					preview.Advance(step);
					remaining -= step;
				}
			}

			return CreateVfxPreviewState();
		}, cancellationToken);

	public async Task<FrameCaptureResult> CaptureVfxPreviewAsync(string path, CancellationToken cancellationToken)
	{
		var view = await Enqueue(
			() => _vfxEditor.Preview?.View ?? throw new InvalidOperationException("No VFX preview is open."),
			cancellationToken).ConfigureAwait(false);
		var previous = _renderGraph.SceneCaptureView;
		_renderGraph.SetSceneCaptureView(view);
		try
		{
			return await CaptureFrameAsync(path, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_renderGraph.SetSceneCaptureView(previous);
		}
	}

	private VfxPreviewStateResult CreateVfxPreviewState()
	{
		var preview = _vfxEditor.Preview;
		if (preview is null)
		{
			return new VfxPreviewStateResult(false, 0.0f, false, false, string.Empty, 0, -1);
		}

		return new VfxPreviewStateResult(
			true,
			preview.Time,
			preview.Playing,
			preview.IsEffectPlaying,
			preview.Motion.ToString(),
			preview.Statistics.ReservedParticles,
			_vfxEditor.SelectedEmitter);
	}

	private VfxStateResult CreateVfxState()
	{
		var scene = _playSession.RuntimeScene;
		var statistics = scene is null
			? new VfxStatistics(0, 0, VfxLimits.ParticlePoolCapacity)
			: _vfx.GetStatistics(scene.World);

		return new VfxStateResult(
			scene is not null,
			statistics.ActiveEffects,
			statistics.ReservedParticles,
			statistics.ParticleCapacity,
			_editorFrameCoordinator.CompletedSequence,
			_renderFrameCoordinator.CompletedSequence);
	}

	private AssetDatabaseEntry ResolveVfxAsset(string assetPath)
	{
		if (string.IsNullOrWhiteSpace(assetPath))
		{
			throw new InvalidOperationException("asset_path is required.");
		}

		var fullProjectPath = Path.GetFullPath(_projectPath);
		var fullAssetPath = Path.GetFullPath(Path.IsPathRooted(assetPath)
			? assetPath
			: Path.Combine(fullProjectPath, assetPath));
		var relativePath = Normalize(Path.GetRelativePath(fullProjectPath, fullAssetPath));
		var asset = _projectService.CurrentAssetDatabase.Assets.FirstOrDefault(candidate =>
			candidate.Type == AssetType.DataAsset &&
			(string.Equals(Normalize(candidate.RelativeAssetPath), relativePath, StringComparison.OrdinalIgnoreCase) ||
			 string.Equals(Normalize(candidate.RelativeSourcePath), relativePath, StringComparison.OrdinalIgnoreCase)));

		return asset ?? throw new InvalidOperationException(
			$"VFX asset '{assetPath}' was not found in the open project's asset database.");
	}
}
