using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Draws an inverted hull around every outlined mesh and keeps only the part
/// that falls outside the object's own silhouette.
///
/// The draw list comes straight from <see cref="SceneDrawData.Outlines"/> rather
/// than from the GPU-driven draw path: an outlined set is a handful of meshes,
/// so a direct draw per mesh is cheaper than teaching culling, bucketing and
/// indirect encoding about a selection, and it leaves that path untouched.
/// </summary>
public sealed class SelectionOutlinePass
{
	/// <summary>
	/// Pulls the hull toward the camera before the depth comparison. Without it a
	/// hull face that is coplanar with the surface it wraps -- which happens all
	/// along a silhouette -- flickers between kept and discarded.
	/// </summary>
	private const float DepthBias = 1e-5f;

	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _viewWriter;
	private ShaderPropertyWriter? _drawWriter;

	public SelectionOutlinePass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public SelectionOutlinePassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		return new SelectionOutlinePassConfig
		{
			TargetWidth = resources.FramebufferSize.X,
			TargetHeight = resources.FramebufferSize.Y,
			DepthWidth = resources.SceneFramebufferSize.X,
			DepthHeight = resources.SceneFramebufferSize.Y,
			TargetColor = context.GetTexture(resources.EncodedSceneColor),
			DepthTexture = context.GetTexture(resources.GBufferDepth),
			Pipeline = _pipeline ?? throw new InvalidOperationException("Selection outline pipeline was not initialized.")
		};
	}

	public void Record(RenderGraphContext context, in SelectionOutlinePassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var commandList = context.CommandList;
		var targets = new PassTargets(new[] { new ColorTargetBinding(config.TargetColor) });
		var viewport = new Viewport(0.0f, 0.0f, config.TargetWidth, config.TargetHeight);
		commandList.BeginPass(targets, viewport);
		commandList.SetScissorRect(new RectInt(0, 0, config.TargetWidth, config.TargetHeight));

		if (sceneData.Outlines.Count == 0)
		{
			commandList.EndPass();
			return;
		}

		commandList.BindPipeline(config.Pipeline);
		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);

		var viewWriter = _viewWriter
			?? throw new InvalidOperationException("Selection outline view writer was not initialized.");
		viewWriter.Clear();
		viewWriter.SetMatrix4x4("unjitteredViewProjection", sceneData.UnjitteredViewProjection);
		viewWriter.SetVector3("cameraPosition", sceneData.CameraOrigin);
		viewWriter.SetVector2("targetSize", new System.Numerics.Vector2(
			Math.Max(config.TargetWidth, 1),
			Math.Max(config.TargetHeight, 1)));
		viewWriter.SetVector2("depthSize", new System.Numerics.Vector2(
			Math.Max(config.DepthWidth, 1),
			Math.Max(config.DepthHeight, 1)));
		viewWriter.SetUInt("depthHandle", _bindlessRegistry.RegisterDepthTexture(config.DepthTexture).Value);
		viewWriter.SetFloat("depthBias", DepthBias);
		viewWriter.SetVector2("jitterPixels", sceneData.JitterPixels);
		commandList.SetGraphicsConstants(viewWriter.RegisterIndex, viewWriter.AsBytes());

		var drawWriter = _drawWriter
			?? throw new InvalidOperationException("Selection outline draw writer was not initialized.");
		for (var i = 0; i < sceneData.Outlines.Count; i++)
		{
			var outline = sceneData.Outlines[i];
			var mesh = outline.Mesh;
			if (mesh.VertexBuffer is not { } vertexBuffer ||
			    mesh.IndexBuffer is not { } indexBuffer ||
			    mesh.IndexCount == 0)
			{
				// The renderer has not backed this mesh with GPU geometry yet. It will
				// on a later frame; skipping is correct rather than fatal.
				continue;
			}

			drawWriter.Clear();
			drawWriter.SetMatrix4x4("world", outline.Transform);
			drawWriter.SetVector4("outlineColor", new System.Numerics.Vector4(
				outline.Color.R,
				outline.Color.G,
				outline.Color.B,
				outline.Color.A));
			drawWriter.SetFloat("thicknessPixels", outline.ThicknessPixels);
			commandList.SetGraphicsConstants(drawWriter.RegisterIndex, drawWriter.AsBytes());

			// Every mesh lives in one shared packed buffer, so the range is selected
			// through startIndex/baseVertex exactly as GpuDrawPass does.
			commandList.SetVertexBuffer(new VertexBufferView(vertexBuffer, mesh.StrideInBytes, 0));
			commandList.SetIndexBuffer(new IndexBufferView(indexBuffer, IndexFormat.UInt32, 0));
			commandList.Draw(new DrawArguments(
				mesh.IndexCount,
				1,
				checked((uint)(mesh.PackedIndexOffsetBytes / sizeof(uint))),
				mesh.PackedBaseVertex));
		}

		commandList.EndPass();
	}

	private void EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"SelectionOutlinePass is already compiled for backend '{_compiledBackendKind.Value}', " +
					$"but was requested for '{device.BackendKind}'.");
			}

			return;
		}

		var compiled = GraphicsShaderCompiler.CompileWithReflection(
			_shaderCompiler,
			device.BackendKind,
			EngineShaderPrograms.SelectionOutline,
			"vertexShader",
			"fragmentShader");

		// Front-face culling is what makes this an outline rather than a silhouette:
		// only the hull's back faces are drawn, and the fragment shader then rejects
		// the ones the object itself covers.
		var renderState = new RenderStateDescriptor(
			FillMode.Solid,
			CullMode.Front,
			depthTestEnabled: false,
			depthWriteEnabled: false,
			BlendMode.Opaque);
		var key = new PipelineKey(
			PassKind.Graphics,
			vertexEntryPoint: "vertexShader",
			pixelEntryPoint: "fragmentShader",
			computeEntryPoint: null,
			renderTargets: new(new[] { TextureFormat.Bgra8Unorm }),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: renderState,
			shaderVariant: "SelectionOutline");
		_pipeline = device.GetOrCreatePipeline(key, compiled.Bytecode);

		var reflection = compiled.ReflectionLayout;
		_viewWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("OutlineViewParams"));
		_drawWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("OutlineDrawParams"));
		_compiledBackendKind = device.BackendKind;
	}
}
