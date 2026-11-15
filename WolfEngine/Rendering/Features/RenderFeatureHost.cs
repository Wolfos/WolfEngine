using System.Runtime.CompilerServices;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Features;

/// <summary>A feature and the frame object its <c>Extract</c> filled for one view snapshot.</summary>
internal readonly record struct RenderFeatureFrame(RenderFeature Feature, object Data);

/// <summary>
/// Render-thread side of render features: initializes them, records their passes into each view's graph, owns
/// their resources and releases them once the game thread unregisters a feature.
/// </summary>
/// <remarks>
/// Game code runs here on the render thread. A feature that throws is disabled and reported rather than allowed
/// to stop rendering; its half-recorded passes do nothing.
/// </remarks>
internal sealed class RenderFeatureHost
{
	private readonly RenderGraphResourceRegistry _resources;
	private readonly IRenderer _renderer;
	private readonly Dictionary<RenderFeature, FeatureState> _states = new(ReferenceEqualityComparer.Instance);
	private readonly ConditionalWeakTable<CompiledShaderArtifact, IGfxPipeline> _pipelines = new();
	private readonly List<RenderFeatureComputePass> _computePasses = [];
	private readonly List<UploadPass> _uploads = [];
	private readonly List<RenderFeatureTexture> _frameTextures = [];
	private readonly RenderFeatureContext _context;
	private RenderGraph? _graph;
	private RenderFeatureRegistry? _registry;
	private int _computePassesUsed;
	private int _uploadsUsed;

	public RenderFeatureHost(RenderGraphResourceRegistry resources, IRenderer renderer, BindlessResourceRegistry bindless)
	{
		_resources = resources;
		_renderer = renderer;
		Bindless = bindless;
		_context = new RenderFeatureContext(this);
	}

	internal BindlessResourceRegistry Bindless { get; }

	/// <summary>The bindless registry, set up for the device; it needs the device's error resources first.</summary>
	internal BindlessResourceRegistry GetInitializedBindless()
	{
		Bindless.EnsureInitialized(_renderer.GetGfxDevice());
		return Bindless;
	}

	/// <summary>Once per frame, before any view records: releases what unregistered features owned.</summary>
	public void BeginFrame(RenderFeatureRegistry registry)
	{
		// Only a frame that stopped part-way leaves handles behind; they belong to that frame's graph.
		foreach (var texture in _frameTextures)
		{
			texture.Handle = default;
		}

		_frameTextures.Clear();
		_registry = registry;
		while (registry.TryTakeRetired(out var feature))
		{
			if (_states.Remove(feature, out var state))
			{
				state.Resources.Release();
			}
		}

		_computePassesUsed = 0;
		_uploadsUsed = 0;
	}

	/// <summary>Records every feature of <paramref name="frames"/> that runs at <paramref name="point"/>.</summary>
	public void Record(
		RenderGraph graph,
		RenderInjectionPoint point,
		IReadOnlyList<RenderFeatureFrame> frames,
		RenderViewId view,
		RenderGraphResourceHandle sceneColor,
		RenderGraphResourceHandle depth)
	{
		if (frames.Count == 0)
		{
			return;
		}

		var sceneColorDescriptor = _resources.GetTextureDescriptor(sceneColor);
		var renderSize = new Int2(sceneColorDescriptor.Width, sceneColorDescriptor.Height);
		_graph = graph;
		try
		{
			for (var i = 0; i < frames.Count; i++)
			{
				var (feature, data) = frames[i];
				if (feature.InjectionPoint != point || feature.IsRetired || feature.IsFaulted)
				{
					continue;
				}

				var name = feature.GetType().Name;
				var firstPass = _computePassesUsed;
				try
				{
					var state = GetOrInitialize(feature, name);
					_context.Begin(name, view, renderSize, sceneColor, sceneColorDescriptor.Format, depth);
					feature.RecordFrame(_context, data);
					for (var pass = firstPass; pass < _computePassesUsed; pass++)
					{
						_computePasses[pass].ThrowIfNotDispatched();
					}
				}
				catch (Exception exception)
				{
					_registry?.Fault(feature, exception);
				}
			}
		}
		finally
		{
			_graph = null;
		}
	}

	/// <summary>Once per frame, after the graph executed: keeps each owned texture's state for the next frame.</summary>
	public void CompleteFrame()
	{
		foreach (var texture in _frameTextures)
		{
			texture.State = _resources.GetResourceState(texture.Handle);
			texture.Handle = default;
		}

		_frameTextures.Clear();
	}

	internal RenderFeatureComputePass AddComputePass(RenderFeatureContext context, string name, CompiledShaderArtifact kernel)
	{
		var graph = _graph ?? throw new InvalidOperationException("Passes can only be added while a feature records.");
		var backend = _renderer.GetGfxDevice().BackendKind;
		if (kernel.Request.BackendKind != backend)
		{
			throw new InvalidOperationException(
				$"Kernel '{kernel.Request.ComputeEntryPoint}' was compiled for {kernel.Request.BackendKind}, but the renderer is {backend}.");
		}

		if (_computePassesUsed == _computePasses.Count)
		{
			_computePasses.Add(new RenderFeatureComputePass(this));
		}

		var pass = _computePasses[_computePassesUsed++];
		pass.Begin(name, kernel, graph.AddPass($"{context.FeatureName}: {name}", PassKind.Compute), context.RenderSize);
		return pass;
	}

	internal void AddUpload(RenderFeatureContext context, RenderFeatureTexture texture, ReadOnlySpan<byte> texels)
	{
		var graph = _graph ?? throw new InvalidOperationException("Uploads can only be added while a feature records.");
		var owned = texture.Owned
		            ?? throw new InvalidOperationException("Only textures the feature created can be uploaded to.");
		var expected = TextureFormatUtilities.GetBytesPerRow(texture.Format, texture.Size.X) * texture.Size.Y;
		if (texels.Length != expected)
		{
			throw new ArgumentException(
				$"A {texture.Size.X}x{texture.Size.Y} {texture.Format} texture takes {expected} bytes, not {texels.Length}.",
				nameof(texels));
		}

		if (_uploadsUsed == _uploads.Count)
		{
			_uploads.Add(new UploadPass());
		}

		var upload = _uploads[_uploadsUsed++];
		upload.Texture = owned;
		upload.Texels = texture.StageUpload(texels);
		graph.AddPass($"{context.FeatureName}: Upload", PassKind.Compute)
			.WriteTexture(GetHandle(texture), ResourceState.CopyDestination)
			.SetExecute(upload.Execute);
	}

	/// <summary>
	/// The texture's handle in this frame's graph. An owned texture is imported once per frame, so every view
	/// shares one handle and the graph tracks its state across all of them.
	/// </summary>
	internal RenderGraphResourceHandle GetHandle(RenderFeatureTexture texture)
	{
		if (texture.Owned is not { } owned)
		{
			return texture.Handle.IsValid
				? texture.Handle
				: throw new InvalidOperationException("The view's frame textures are only valid while a feature records.");
		}

		if (texture.Handle.IsValid == false)
		{
			texture.Handle = _resources.ImportTexture(owned, takeOwnership: false, initialState: texture.State);
			_frameTextures.Add(texture);
		}

		return texture.Handle;
	}

	internal IGfxPipeline GetPipeline(CompiledShaderArtifact kernel)
	{
		if (_pipelines.TryGetValue(kernel, out var pipeline))
		{
			return pipeline;
		}

		// The content key is in the variant, so a reimported kernel never reuses a pipeline built from older code.
		var key = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: kernel.Request.ComputeEntryPoint,
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: $"{kernel.Request.ProgramId}:{kernel.ContentKey}");
		pipeline = _renderer.GetGfxDevice().GetOrCreatePipeline(key, kernel.Bytecode);
		_pipelines.AddOrUpdate(kernel, pipeline);
		return pipeline;
	}

	private FeatureState GetOrInitialize(RenderFeature feature, string name)
	{
		if (_states.TryGetValue(feature, out var state))
		{
			return state;
		}

		state = new FeatureState(new RenderFeatureResources(_renderer.GetGfxDevice(), name));
		_states[feature] = state;
		feature.InitializeOnRenderThread(state.Resources);
		return state;
	}

	private sealed record FeatureState(RenderFeatureResources Resources);

	private sealed class UploadPass
	{
		public UploadPass()
		{
			Execute = context => context.CommandList.UploadTexture(Texture!, Texels.Span);
		}

		public IGfxTexture? Texture { get; set; }

		public ReadOnlyMemory<byte> Texels { get; set; }

		public Action<RenderGraphContext> Execute { get; }
	}
}
