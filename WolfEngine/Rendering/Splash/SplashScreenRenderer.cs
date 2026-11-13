using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Splash;

/// <summary>
/// Draws the splash screen into the window image, in a submission of its own like the UI compositor: alone while
/// the game loads, and over the game's frames while it fades out.
/// </summary>
/// <remarks>
/// Owns the splash's two textures for as long as the same content is showing. The window image is a render target
/// outside the graph (the graph's last pass and presentation both leave it in that state), so drawing into it
/// needs no barriers.
/// </remarks>
internal sealed class SplashScreenRenderer
{
	private const string PassName = "Splash Screen";

	// The layout is designed for a 1080-pixel-tall 16:9 screen and scales with the window, limited by whichever of
	// its width and height is the tighter fit.
	private const float ReferenceHeight = 1080.0f;
	private const float ReferenceAspect = 16.0f / 9.0f;

	// Style, in display-encoded colour and reference units.
	internal static readonly ColorRGBA BackgroundColor = new(0.063f, 0.071f, 0.082f, 1.0f);
	private static readonly ColorRGBA StrokeColor = new(0.55f, 0.84f, 0.98f, 1.0f);
	private const float StrokeGlowStrength = 0.45f;
	private const float StrokeHalfWidth = 1.1f;
	private const float GlowRadius = 14.0f;
	private const float TipLength = 0.06f;
	private const float PoweredByStrength = 0.75f;

	private static readonly ushort[] TriangleIndices = [0, 1, 2];

	private readonly IShaderProvider _shaderProvider;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly IRenderer _renderer;
	private IGfxPipeline? _pipeline;
	private TextureFormat _pipelineTargetFormat;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _paramsWriter;
	private IGfxBuffer? _indexBuffer;
	private int _contentRevision = -1;
	private ITextureResources? _logoTexture;
	private ITextureResources? _textTexture;

	public SplashScreenRenderer(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry, IRenderer renderer)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
	}

	/// <summary>
	/// Records and submits <paramref name="frame"/> into <paramref name="target"/>. Releases the splash's textures
	/// instead when nothing is showing.
	/// </summary>
	/// <param name="clearTarget">True when the splash owns the window and nothing else was drawn this frame.</param>
	public void Compose(IGfxDevice device, IGfxTexture target, in SplashScreenFrame frame, bool clearTarget)
	{
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(target);

		if (frame.Content is not { } content)
		{
			ReleaseTextures(device);
			return;
		}

		var width = target.Descriptor.Width;
		var height = target.Descriptor.Height;
		if (width <= 0 || height <= 0)
		{
			return;
		}

		EnsureTextures(device, content, frame.ContentRevision);
		var pipeline = EnsurePipeline(device, target.Descriptor.Format);
		var writer = _paramsWriter ?? throw new InvalidOperationException("The splash screen writer was not initialized.");
		WriteParams(writer, content, frame.Pose, width, height);

		var commandList = device.BeginGraphics();
		commandList.SetBindlessTable(device.GlobalTable);
		commandList.BeginEvent(PassName);
		commandList.BeginPass(
			new PassTargets(new[] { new ColorTargetBinding(target) }),
			new Viewport(0.0f, 0.0f, width, height));
		if (clearTarget)
		{
			commandList.ClearColorAttachment(0, BackgroundColor);
		}

		commandList.SetScissorRect(new RectInt(0, 0, width, height));
		commandList.BindPipeline(pipeline);
		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);
		commandList.SetIndexBuffer(new IndexBufferView(_indexBuffer!, IndexFormat.UInt16, 0));
		commandList.SetGraphicsConstants(writer.RegisterIndex, writer.AsBytes());
		commandList.Draw(new DrawArguments((uint)TriangleIndices.Length));
		commandList.EndPass();
		commandList.EndEvent();
		device.Submit(commandList);
	}

	public void InvalidateShaderPipeline()
	{
		_pipeline = null;
		_paramsWriter = null;
		_compiledBackendKind = null;
	}

	private void WriteParams(ShaderPropertyWriter writer, SplashScreenContent content, SplashScreenPose pose, int width, int height)
	{
		var pixelsPerUnit = MathF.Min(width / ReferenceAspect, height) / ReferenceHeight;
		var screenCenter = new Vector2(width, height) * 0.5f;

		writer.Clear();
		writer.SetVector4("logoRect", ToPixelRect(content.Logo, screenCenter, pixelsPerUnit));
		writer.SetVector4("textRect", ToPixelRect(content.Text, screenCenter, pixelsPerUnit));
		writer.SetVector4("distanceScale", new Vector4(
			pixelsPerUnit / content.Logo.TexelsPerUnit,
			pixelsPerUnit / content.Text.TexelsPerUnit,
			StrokeHalfWidth * pixelsPerUnit,
			GlowRadius * pixelsPerUnit));
		writer.SetColorRGBA("backgroundColor", BackgroundColor);
		writer.SetColorRGBA("silhouetteColor", content.SilhouetteColor);
		writer.SetColorRGBA("highlightColor", content.HighlightColor);
		writer.SetVector4("textColor", new Vector4(
			content.TextColor.R,
			content.TextColor.G,
			content.TextColor.B,
			PoweredByStrength));
		writer.SetVector4("strokeColor", new Vector4(StrokeColor.R, StrokeColor.G, StrokeColor.B, StrokeGlowStrength));
		writer.SetVector4("animation", new Vector4(pose.TraceProgress, pose.StrokeOpacity, pose.FillOpacity, TipLength));
		writer.SetVector4("textAnimation", new Vector4(pose.PoweredByOpacity, pose.WordmarkOpacity, pose.Opacity, 0.0f));
		writer.SetUInt("logoTextureHandle", _logoTexture!.ShaderResourceView.Value);
		writer.SetUInt("textTextureHandle", _textTexture!.ShaderResourceView.Value);
		writer.SetUInt("samplerHandle", _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
			FilterMode.Bilinear,
			AddressMode.Clamp,
			AddressMode.Clamp,
			AddressMode.Clamp)).Value);
	}

	private static Vector4 ToPixelRect(SplashScreenLayer layer, Vector2 screenCenter, float pixelsPerUnit)
	{
		var size = layer.Size * pixelsPerUnit;
		var topLeft = screenCenter + layer.Center * pixelsPerUnit - size * 0.5f;

		return new Vector4(topLeft.X, topLeft.Y, size.X, size.Y);
	}

	private void EnsureTextures(IGfxDevice device, SplashScreenContent content, int revision)
	{
		if (_contentRevision == revision &&
		    _logoTexture is not null &&
		    _textTexture is not null)
		{
			return;
		}

		ReleaseTextures(device);
		_bindlessRegistry.EnsureInitialized(device);
		_logoTexture = CreateTexture("Splash Screen Logo", content.Logo);
		_textTexture = CreateTexture("Splash Screen Text", content.Text);
		_contentRevision = revision;
	}

	private ITextureResources CreateTexture(string name, SplashScreenLayer layer)
	{
		var texture = new Texture(
			name,
			layer.Width,
			layer.Height,
			isSrgb: false,
			layer.Format,
			[new TextureMipData(layer.Width, layer.Height, layer.Texels)]);

		return _renderer.CreateTextureResources(texture);
	}

	private void ReleaseTextures(IGfxDevice device)
	{
		Retire(device, _logoTexture);
		Retire(device, _textTexture);
		_logoTexture = null;
		_textTexture = null;
		_contentRevision = -1;
	}

	private static void Retire(IGfxDevice device, ITextureResources? resources)
	{
		if (resources is null)
		{
			return;
		}

		// A frame in flight may still sample it, so it goes once that frame's submission completes.
		var texture = resources.Texture;
		device.Retire(
			() =>
			{
				(texture as IDisposable)?.Dispose();
				if (ReferenceEquals(resources, texture) == false)
				{
					(resources as IDisposable)?.Dispose();
				}
			},
			"Splash screen texture");
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device, TextureFormat targetFormat)
	{
		if (_pipeline is not null &&
		    _pipelineTargetFormat == targetFormat &&
		    _compiledBackendKind == device.BackendKind)
		{
			return _pipeline;
		}

		var compiled = GraphicsShaderCompiler.CompileWithReflection(
			_shaderProvider,
			device.BackendKind,
			EngineShaderPrograms.SplashScreen,
			"vertexShader",
			"fragmentShader");
		var renderState = new RenderStateDescriptor(
			FillMode.Solid,
			CullMode.None,
			depthTestEnabled: false,
			depthWriteEnabled: false,
			BlendMode.AlphaBlend);
		var key = new PipelineKey(
			PassKind.Graphics,
			vertexEntryPoint: "vertexShader",
			pixelEntryPoint: "fragmentShader",
			computeEntryPoint: null,
			renderTargets: new(new[] { targetFormat }),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: renderState,
			shaderVariant: "SplashScreen");

		_pipeline = device.GetOrCreatePipeline(key, compiled.Bytecode);
		_pipelineTargetFormat = targetFormat;
		_compiledBackendKind = device.BackendKind;
		_paramsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("SplashParams"));
		if (_indexBuffer is null)
		{
			var indexBuffer = device.CreateBuffer(new BufferDescriptor(
				(ulong)(TriangleIndices.Length * sizeof(ushort)),
				BufferUsage.Index,
				BufferFlags.None,
				"SplashScreenIndices"));
			((IWritableGpuBuffer)indexBuffer).Write<ushort>(TriangleIndices);
			_indexBuffer = indexBuffer;
		}

		return _pipeline;
	}
}
