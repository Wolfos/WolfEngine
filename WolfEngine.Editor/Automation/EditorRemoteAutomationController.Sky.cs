using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor.Automation;

public sealed partial class EditorRemoteAutomationController
{
	/// <summary>
	/// Switches the active scene's sky in memory, without saving its render config. Settings left null keep their
	/// current values.
	/// </summary>
	public Task<string> SetSkyAsync(
		string mode,
		string? hdriAssetPath,
		bool clearHdri,
		float? hdriRotationDegrees,
		float? hdriIntensity,
		float? gradientIntensity,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (Enum.TryParse<SkyConfigMode>(mode, true, out var selectedMode) == false ||
			    Enum.IsDefined(selectedMode) == false)
			{
				throw new ArgumentException("Sky mode must be Procedural, Hdri or Gradient.", nameof(mode));
			}

			var config = GetActiveRenderConfig();
			var sky = config.Sky;
			sky.Mode = selectedMode;

			var hdri = sky.Hdri;
			if (clearHdri)
			{
				hdri.Texture = default;
			}
			else if (string.IsNullOrWhiteSpace(hdriAssetPath) == false)
			{
				hdri.Texture = new AssetRef<Texture> { NodeId = ResolveTextureAsset(hdriAssetPath).Id };
			}

			if (hdriRotationDegrees.HasValue)
			{
				hdri.RotationDegrees = hdriRotationDegrees.Value;
			}

			if (hdriIntensity.HasValue)
			{
				hdri.Intensity = hdriIntensity.Value;
			}

			sky.Hdri = hdri;

			if (gradientIntensity.HasValue)
			{
				var gradient = sky.Gradient;
				gradient.Intensity = gradientIntensity.Value;
				sky.Gradient = gradient;
			}

			config.Sky = sky;

			return $"Sky: {sky.Mode}, HDRI: {(sky.Hdri.Texture.IsValid ? sky.Hdri.Texture.NodeId.ToString("D") : "none")}, " +
			       $"rotation: {sky.Hdri.RotationDegrees}, HDRI intensity: {sky.Hdri.Intensity}, " +
			       $"gradient intensity: {sky.Gradient.Intensity}. Changed in memory only.";
		}, cancellationToken);

	private RenderConfig GetActiveRenderConfig()
	{
		var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
		RenderConfig? config = null;
		foreach (var entry in scene.World.View<WorldSettings>())
		{
			config = entry.First.RenderConfigAsset.Asset;
		}

		return config ?? throw new InvalidOperationException("The active scene has no resolved render config asset.");
	}
}
