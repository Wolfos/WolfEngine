using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.Features;

/// <summary>
/// What a render feature can see and do while recording one view's passes. Valid only during
/// <see cref="RenderFeature{TFrame}.Record"/>.
/// </summary>
public sealed class RenderFeatureContext
{
	private readonly RenderFeatureHost _host;
	private string _featureName = string.Empty;

	internal RenderFeatureContext(RenderFeatureHost host)
	{
		_host = host;
	}

	public RenderViewId View { get; private set; }

	/// <summary>Size of <see cref="SceneColor"/> and <see cref="Depth"/>, in pixels.</summary>
	public Int2 RenderSize { get; private set; }

	/// <summary>Linear HDR scene colour. Passes can read it, or modify it in place with <see cref="RenderFeatureComputePass.ReadWrite"/>.</summary>
	public RenderFeatureTexture SceneColor { get; } = new(isDepth: false);

	/// <summary>Scene depth. Read-only; reconstruct positions with <c>WolfReconstructWorldPosition</c>.</summary>
	public RenderFeatureTexture Depth { get; } = new(isDepth: true);

	internal string FeatureName => _featureName;

	internal void Begin(string featureName, RenderViewId view, Int2 renderSize, RenderGraphResourceHandle sceneColor,
		TextureFormat sceneColorFormat, RenderGraphResourceHandle depth)
	{
		_featureName = featureName;
		View = view;
		RenderSize = renderSize;
		SceneColor.BindFrameTexture(sceneColor, renderSize, sceneColorFormat);
		Depth.BindFrameTexture(depth, renderSize, TextureFormat.D32Float);
	}

	/// <summary>
	/// Replaces the contents of one of the feature's own textures with tightly packed rows of texels. The data is
	/// copied straight away. The copy runs on the GPU in frame order, so frames already in flight keep the old
	/// contents. Uploading once when the data changes is enough: the texture keeps its contents across frames.
	/// </summary>
	public void Upload(RenderFeatureTexture texture, ReadOnlySpan<byte> texels)
	{
		ArgumentNullException.ThrowIfNull(texture);
		_host.AddUpload(this, texture, texels);
	}

	/// <summary>Adds a dispatch of one kernel to this view's frame, at the feature's injection point.</summary>
	public RenderFeatureComputePass AddComputePass(string name, ComputeShader shader, string entryPoint)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(shader);
		return _host.AddComputePass(this, name, shader.GetKernel(entryPoint));
	}
}
