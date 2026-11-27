using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// FSR3 temporal upscaling: reconstructs the display-size scene colour from this frame's jittered lighting and
/// FSR3's history. Adds every FSR3 graph node, owns the intermediates only those nodes use, and drives the
/// kernels behind them. The history, the frame info and the transparency mask are view resources, because the
/// view's history bookkeeping and the transparent pass use them too.
/// </summary>
internal sealed class Fsr3Pass
{
	private readonly Fsr3ClearPass _clearPass;
	private readonly Fsr3PrepareInputsPass _prepareInputsPass;
	private readonly Fsr3LumaPyramidPass _lumaPyramidPass;
	private readonly Fsr3ShadingChangePyramidPass _shadingChangePyramidPass;
	private readonly Fsr3ShadingChangePass _shadingChangePass;
	private readonly Fsr3PrepareReactivityPass _prepareReactivityPass;
	private readonly Fsr3LumaInstabilityPass _lumaInstabilityPass;
	private readonly Fsr3AccumulatePass _accumulatePass;
	private readonly Fsr3RcasPass _rcasPass;
	private readonly Fsr3DebugViewPass _debugViewPass;
	private readonly Action<RenderGraphContext> _prepareInputsExecute;
	private readonly Action<RenderGraphContext> _lumaPyramidExecute;
	private readonly Action<RenderGraphContext> _shadingChangePyramidExecute;
	private readonly Action<RenderGraphContext> _shadingChangeExecute;
	private readonly Action<RenderGraphContext> _prepareReactivityExecute;
	private readonly Action<RenderGraphContext> _lumaInstabilityExecute;
	private readonly Action<RenderGraphContext> _accumulateExecute;
	private readonly Action<RenderGraphContext> _rcasExecute;
	private readonly Action<RenderGraphContext> _debugViewExecute;

	public Fsr3Pass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);
		ArgumentNullException.ThrowIfNull(bindlessRegistry);

		_clearPass = new Fsr3ClearPass(shaderSource, bindlessRegistry);
		_prepareInputsPass = new Fsr3PrepareInputsPass(shaderSource, bindlessRegistry);
		_lumaPyramidPass = new Fsr3LumaPyramidPass(shaderSource, bindlessRegistry);
		_shadingChangePyramidPass = new Fsr3ShadingChangePyramidPass(shaderSource, bindlessRegistry);
		_shadingChangePass = new Fsr3ShadingChangePass(shaderSource, bindlessRegistry);
		_prepareReactivityPass = new Fsr3PrepareReactivityPass(shaderSource, bindlessRegistry);
		_lumaInstabilityPass = new Fsr3LumaInstabilityPass(shaderSource, bindlessRegistry);
		_accumulatePass = new Fsr3AccumulatePass(shaderSource, bindlessRegistry);
		_rcasPass = new Fsr3RcasPass(shaderSource, bindlessRegistry);
		_debugViewPass = new Fsr3DebugViewPass(shaderSource, bindlessRegistry);
		_prepareInputsExecute = ExecutePrepareInputs;
		_lumaPyramidExecute = ExecuteLumaPyramid;
		_shadingChangePyramidExecute = ExecuteShadingChangePyramid;
		_shadingChangeExecute = ExecuteShadingChange;
		_prepareReactivityExecute = ExecutePrepareReactivity;
		_lumaInstabilityExecute = ExecuteLumaInstability;
		_accumulateExecute = ExecuteAccumulate;
		_rcasExecute = ExecuteRcas;
		_debugViewExecute = ExecuteDebugView;
	}

	/// <summary>The intermediates only FSR3's nodes read or write, created per view each frame FSR3 runs.</summary>
	private sealed class ViewHandles
	{
		public RenderGraphResourceHandle DilatedMotionVectors;
		public RenderGraphResourceHandle DilatedDepth;
		public RenderGraphResourceHandle FarthestDepth;
		public RenderGraphResourceHandle ReconstructedPrevNearestDepth;
		public RenderGraphResourceHandle FarthestDepthMip1;
		public RenderGraphResourceHandle LumaSpdAtomic;
		public RenderGraphResourceHandle[] LumaSpdMips = [];
		public RenderGraphResourceHandle ShadingSpdAtomic;
		public RenderGraphResourceHandle[] ShadingSpdMips = [];

		/// <summary>
		/// Physically half resolution. Allocating it at full resolution makes FSR3's half-resolution UV clamp sample
		/// the unwritten portion of the resource.
		/// </summary>
		public RenderGraphResourceHandle ShadingChange;
		public RenderGraphResourceHandle DilatedReactiveMasks;

		/// <summary>Display size: Prepare Reactivity writes new locks at the display pixel each jittered sample lands on.</summary>
		public RenderGraphResourceHandle NewLocks;
		public RenderGraphResourceHandle LumaInstability;
	}

	/// <summary>The size of FSR3's shading-change target: half the render size.</summary>
	internal static Int2 GetShadingChangeSize(Int2 renderSize) => new(
		Math.Max(renderSize.X / 2, 1),
		Math.Max(renderSize.Y / 2, 1));

	/// <summary>
	/// Creates this frame's intermediates and adds FSR3's nodes: the clears its kernels rely on, input preparation,
	/// the luma and shading-change pyramids, reactivity, luma instability, accumulation into the history, and RCAS
	/// into the resolved scene colour.
	/// </summary>
	internal void AddNodes(RenderGraph graph, RenderViewState view)
	{
		var resources = view.FrameResources;
		var history = resources.Fsr3;
		var size = resources.SceneFramebufferSize;
		var upscaleSize = resources.FramebufferSize;
		var fsr = CreateViewHandles(graph.Resources, view, size, upscaleSize);

		AddClear(graph, "FSR3 Clear Reconstructed Depth", fsr.ReconstructedPrevNearestDepth,
			size, BitConverter.SingleToUInt32Bits(1.0f), true);
		AddClear(graph, "FSR3 Clear Luma SPD Counter", fsr.LumaSpdAtomic, new Int2(1, 1), 0u, true);
		AddClear(graph, "FSR3 Clear Shading SPD Counter", fsr.ShadingSpdAtomic, new Int2(1, 1), 0u, true);
		for (var i = 0; i < fsr.LumaSpdMips.Length; i++)
		{
			var mipSize = GetMipSize(size, i);
			AddClear(graph, $"FSR3 Clear Luma Mip {i}", fsr.LumaSpdMips[i], mipSize, 0u, false);
		}

		var shadingSize = GetShadingChangeSize(size);
		for (var i = 0; i < fsr.ShadingSpdMips.Length; i++)
		{
			AddClear(graph, $"FSR3 Clear Shading Mip {i}", fsr.ShadingSpdMips[i],
				GetMipSize(shadingSize, i), 0u, false);
		}

		if (history.HistoryValid == false || view.ResetTaaHistoryThisFrame)
		{
			AddClear(graph, "FSR3 Clear Frame Info", history.FrameInfo, new Int2(1, 1), 0u, false);
			AddClear(graph, "FSR3 Clear Internal History", history.InternalHistoryRead, upscaleSize, 0u, false);
			AddClear(graph, "FSR3 Clear Luma History", history.LumaHistoryRead, size, 0u, false);
			AddClear(graph, "FSR3 Clear Previous Luma", history.CurrentLumaRead, size, 0u, false);
			AddClear(graph, "FSR3 Clear Accumulation", history.AccumulationRead, size, 0u, false);
		}

		graph.AddPass("FSR3 Prepare Inputs", PassKind.Compute)
			.ReadTexture(resources.LightingBuffer, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferVelocity, ResourceState.ShaderResource)
			.WriteTexture(fsr.DilatedMotionVectors, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.DilatedDepth, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.FarthestDepth, ResourceState.UnorderedAccess)
			.WriteTexture(history.CurrentLumaWrite, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.ReconstructedPrevNearestDepth, ResourceState.UnorderedAccess)
			.SetExecute(_prepareInputsExecute);

		var lumaPyramid = graph.AddPass("FSR3 Luma Pyramid", PassKind.Compute)
			.ReadTexture(history.CurrentLumaWrite, ResourceState.ShaderResource)
			.ReadTexture(fsr.FarthestDepth, ResourceState.ShaderResource)
			.WriteTexture(fsr.FarthestDepthMip1, ResourceState.UnorderedAccess)
			.WriteTexture(history.FrameInfo, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.LumaSpdAtomic, ResourceState.UnorderedAccess);
		foreach (var mip in fsr.LumaSpdMips)
		{
			lumaPyramid.WriteTexture(mip, ResourceState.UnorderedAccess);
		}

		lumaPyramid.SetExecute(_lumaPyramidExecute);

		var shadingPyramid = graph.AddPass("FSR3 Shading Change Pyramid", PassKind.Compute)
			.ReadTexture(history.CurrentLumaWrite, ResourceState.ShaderResource)
			.ReadTexture(history.CurrentLumaRead, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedMotionVectors, ResourceState.ShaderResource)
			.ReadTexture(history.FrameInfo, ResourceState.ShaderResource)
			.WriteTexture(fsr.ShadingSpdAtomic, ResourceState.UnorderedAccess);
		foreach (var mip in fsr.ShadingSpdMips)
		{
			shadingPyramid.WriteTexture(mip, ResourceState.UnorderedAccess);
		}

		shadingPyramid.SetExecute(_shadingChangePyramidExecute);

		var shadingChange = graph.AddPass("FSR3 Shading Change", PassKind.Compute)
			.WriteTexture(fsr.ShadingChange, ResourceState.UnorderedAccess);
		foreach (var mip in fsr.ShadingSpdMips)
		{
			shadingChange.ReadTexture(mip, ResourceState.ShaderResource);
		}

		shadingChange.SetExecute(_shadingChangeExecute);

		// Prepare Reactivity only writes pixels that acquire a new thin-feature lock. NewLocks is
		// transient and may alias an earlier full-resolution FSR3 intermediate, so its untouched
		// pixels are not implicitly zero even though its descriptor has a zero clear colour. Clear
		// at the lifetime boundary, after those earlier intermediates have finished using the alias.
		// Otherwise their depth/luma payload is read as a positive lock and stale colour history is
		// protected from rectification, producing bright trails behind moving opaque geometry.
		AddClear(graph, "FSR3 Clear New Locks", fsr.NewLocks, upscaleSize, 0u, false);

		graph.AddPass("FSR3 Prepare Reactivity", PassKind.Compute)
			.ReadTexture(fsr.ReconstructedPrevNearestDepth, ResourceState.UnorderedAccess)
			.ReadTexture(fsr.DilatedMotionVectors, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedDepth, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferVelocity, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferMaterial, ResourceState.ShaderResource)
			.ReadTexture(history.TransparencyMask, ResourceState.ShaderResource)
			.ReadTexture(history.AccumulationRead, ResourceState.ShaderResource)
			.ReadTexture(fsr.ShadingChange, ResourceState.ShaderResource)
			.ReadTexture(history.CurrentLumaWrite, ResourceState.ShaderResource)
			.ReadTexture(history.FrameInfo, ResourceState.ShaderResource)
			.WriteTexture(history.AccumulationWrite, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.DilatedReactiveMasks, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.NewLocks, ResourceState.UnorderedAccess)
			.SetExecute(_prepareReactivityExecute);

		graph.AddPass("FSR3 Luma Instability", PassKind.Compute)
			.ReadTexture(history.FrameInfo, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedReactiveMasks, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedMotionVectors, ResourceState.ShaderResource)
			.ReadTexture(history.LumaHistoryRead, ResourceState.ShaderResource)
			.ReadTexture(fsr.FarthestDepthMip1, ResourceState.ShaderResource)
			.ReadTexture(history.CurrentLumaWrite, ResourceState.ShaderResource)
			.WriteTexture(history.LumaHistoryWrite, ResourceState.UnorderedAccess)
			.WriteTexture(fsr.LumaInstability, ResourceState.UnorderedAccess)
			.SetExecute(_lumaInstabilityExecute);

		graph.AddPass("FSR3 Accumulate", PassKind.Compute)
			.ReadTexture(history.FrameInfo, ResourceState.ShaderResource)
			.ReadTexture(resources.LightingBuffer, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedMotionVectors, ResourceState.ShaderResource)
			.ReadTexture(fsr.DilatedReactiveMasks, ResourceState.ShaderResource)
			.ReadTexture(fsr.FarthestDepthMip1, ResourceState.ShaderResource)
			.ReadTexture(fsr.LumaInstability, ResourceState.ShaderResource)
			.ReadTexture(fsr.NewLocks, ResourceState.UnorderedAccess)
			.ReadTexture(history.InternalHistoryRead, ResourceState.ShaderResource)
			.WriteTexture(history.InternalHistoryWrite, ResourceState.UnorderedAccess)
			.SetExecute(_accumulateExecute);

		graph.AddPass("FSR3 RCAS", PassKind.Compute)
			.ReadTexture(history.InternalHistoryWrite, ResourceState.ShaderResource)
			.ReadTexture(history.FrameInfo, ResourceState.ShaderResource)
			.WriteTexture(resources.ResolvedSceneColor, ResourceState.UnorderedAccess)
			.SetExecute(_rcasExecute);

		if (GraphicsConfig.Fsr3DebugViewEnabled)
		{
			graph.AddPass("FSR3 Debug View", PassKind.Compute)
				.ReadTexture(fsr.DilatedReactiveMasks, ResourceState.ShaderResource)
				.ReadTexture(fsr.DilatedMotionVectors, ResourceState.ShaderResource)
				.ReadTexture(fsr.DilatedDepth, ResourceState.ShaderResource)
				.ReadTexture(history.InternalHistoryWrite, ResourceState.ShaderResource)
				.ReadTexture(history.CurrentLumaWrite, ResourceState.ShaderResource)
				.ReadTexture(history.CurrentLumaRead, ResourceState.ShaderResource)
				.ReadTexture(history.FrameInfo, ResourceState.ShaderResource)
				.WriteTexture(resources.ResolvedSceneColor, ResourceState.UnorderedAccess)
				.SetExecute(_debugViewExecute);
		}
	}

	private static ViewHandles CreateViewHandles(
		RenderGraphResourceRegistry resources,
		RenderViewState view,
		Int2 size,
		Int2 upscaleSize)
	{
		var halfSize = new Int2(Math.Max(size.X / 2, 1), Math.Max(size.Y / 2, 1));
		var handles = view.GetPassState<ViewHandles>();
		handles.DilatedMotionVectors = CreateTexture(resources, size);
		handles.DilatedDepth = CreateTexture(resources, size);
		handles.FarthestDepth = CreateTexture(resources, size);
		handles.ReconstructedPrevNearestDepth = CreateUintTexture(resources, size);
		handles.FarthestDepthMip1 = CreateTexture(resources, halfSize);
		handles.LumaSpdAtomic = CreateUintTexture(resources, new Int2(1, 1));
		handles.LumaSpdMips = CreateSpdMips(resources, size);
		handles.ShadingSpdAtomic = CreateUintTexture(resources, new Int2(1, 1));
		handles.ShadingSpdMips = CreateSpdMips(resources, halfSize);
		handles.ShadingChange = CreateTexture(resources, GetShadingChangeSize(size));
		handles.DilatedReactiveMasks = CreateTexture(resources, size);
		handles.NewLocks = CreateTexture(resources, upscaleSize);
		handles.LumaInstability = CreateTexture(resources, size);

		return handles;
	}

	private void AddClear(
		RenderGraph graph,
		string name,
		RenderGraphResourceHandle texture,
		Int2 size,
		uint valueBits,
		bool uintTexture)
	{
		graph.AddPass(name, PassKind.Compute)
			.WriteTexture(texture, ResourceState.UnorderedAccess)
			.SetExecute(context => _clearPass.Record(context, context.Device, texture, size, valueBits, uintTexture));
	}

	private static Int2 GetMipSize(Int2 sourceSize, int mipIndex)
	{
		var divisor = 1 << Math.Min(mipIndex + 1, 30);

		return new Int2(Math.Max(sourceSize.X / divisor, 1), Math.Max(sourceSize.Y / divisor, 1));
	}

	private static Fsr3ConstantValues BuildConstants(RenderGraphContext context)
	{
		var view = context.ViewState;
		var resources = view.FrameResources;
		var renderSize = resources.SceneFramebufferSize;
		var upscaleSize = resources.FramebufferSize;
		var camera = context.ViewSnapshot.Camera;
		var verticalFov = float.DegreesToRadians(camera.Fov > 0.0f ? camera.Fov : 70.0f);
		var depth = Fsr3Constants.BuildDeviceToViewDepth(
			context.SceneData.NearPlane,
			context.SceneData.FarPlane,
			verticalFov,
			(float)Math.Max(renderSize.X, 1) / Math.Max(renderSize.Y, 1));
		var reset = view.ResetTaaHistoryThisFrame ||
		            context.SceneData.ResetHistory ||
		            resources.Fsr3.HistoryValid == false;

		// Any change to either size reallocates the history and resets accumulation, so whenever last frame's
		// history is read it was produced at this frame's sizes.
		return Fsr3Constants.Build(
			renderSize,
			upscaleSize,
			renderSize,
			upscaleSize,
			depth,
			context.SceneData.JitterPixels,
			context.SceneData.PreviousJitterPixels,
			verticalFov,
			Math.Max(view.SceneFrameDeltaSeconds, 1.0f / 1000.0f),
			reset ? 0.0f : view.Fsr3FrameIndex);
	}

	private void ExecutePrepareInputs(RenderGraphContext context)
	{
		var resources = context.ViewState.FrameResources;
		var history = resources.Fsr3;
		var fsr = context.ViewState.GetPassState<ViewHandles>();
		var constants = BuildConstants(context);
		var config = _prepareInputsPass.BuildConfig(context, context.Device,
			resources.LightingBuffer, resources.GBufferDepth, resources.GBufferVelocity,
			fsr.DilatedMotionVectors, fsr.DilatedDepth, fsr.FarthestDepth, history.CurrentLumaWrite,
			fsr.ReconstructedPrevNearestDepth, in constants);
		_prepareInputsPass.Record(context, in config);
	}

	private void ExecuteLumaPyramid(RenderGraphContext context)
	{
		var history = context.ViewState.FrameResources.Fsr3;
		var fsr = context.ViewState.GetPassState<ViewHandles>();
		var constants = BuildConstants(context);
		var config = _lumaPyramidPass.BuildConfig(context, context.Device,
			history.CurrentLumaWrite, fsr.FarthestDepth, fsr.FarthestDepthMip1, history.FrameInfo,
			fsr.LumaSpdAtomic, fsr.LumaSpdMips, in constants);
		_lumaPyramidPass.Record(context, in config);
	}

	private void ExecuteShadingChangePyramid(RenderGraphContext context)
	{
		var history = context.ViewState.FrameResources.Fsr3;
		var fsr = context.ViewState.GetPassState<ViewHandles>();
		var constants = BuildConstants(context);
		var config = _shadingChangePyramidPass.BuildConfig(context, context.Device,
			history.CurrentLumaWrite, history.CurrentLumaRead, fsr.DilatedMotionVectors, history.FrameInfo,
			fsr.ShadingSpdAtomic, fsr.ShadingSpdMips, in constants);
		_shadingChangePyramidPass.Record(context, in config);
	}

	private void ExecuteShadingChange(RenderGraphContext context)
	{
		var fsr = context.ViewState.GetPassState<ViewHandles>();
		var constants = BuildConstants(context);
		var config = _shadingChangePass.BuildConfig(context, context.Device,
			fsr.ShadingSpdMips, fsr.ShadingChange, in constants);
		_shadingChangePass.Record(context, in config);
	}

	private void ExecutePrepareReactivity(RenderGraphContext context)
	{
		var resources = context.ViewState.FrameResources;
		var history = resources.Fsr3;
		var fsr = context.ViewState.GetPassState<ViewHandles>();
		var constants = BuildConstants(context);
		var settings = resources.Config.AntiAliasing;
		var config = _prepareReactivityPass.BuildConfig(context, context.Device,
			fsr.ReconstructedPrevNearestDepth, fsr.DilatedMotionVectors, fsr.DilatedDepth,
			resources.GBufferDepth,
			resources.GBufferVelocity,
			resources.GBufferMaterial, history.TransparencyMask, history.AccumulationRead, history.AccumulationWrite,
			fsr.ShadingChange, history.CurrentLumaWrite, history.FrameInfo, fsr.DilatedReactiveMasks,
			fsr.NewLocks, in constants,
			settings.AlphaTestReactiveScale,
			settings.TransparencyAndCompositionMaskScale);
		_prepareReactivityPass.Record(context, in config);
	}

	private void ExecuteLumaInstability(RenderGraphContext context)
	{
		var history = context.ViewState.FrameResources.Fsr3;
		var fsr = context.ViewState.GetPassState<ViewHandles>();
		var constants = BuildConstants(context);
		var config = _lumaInstabilityPass.BuildConfig(context, context.Device,
			history.FrameInfo, fsr.DilatedReactiveMasks, fsr.DilatedMotionVectors, history.LumaHistoryRead,
			history.LumaHistoryWrite, fsr.FarthestDepthMip1, history.CurrentLumaWrite, fsr.LumaInstability,
			in constants);
		_lumaInstabilityPass.Record(context, in config);
	}

	private void ExecuteAccumulate(RenderGraphContext context)
	{
		var resources = context.ViewState.FrameResources;
		var history = resources.Fsr3;
		var fsr = context.ViewState.GetPassState<ViewHandles>();
		var constants = BuildConstants(context);
		var config = _accumulatePass.BuildConfig(context, context.Device,
			history.FrameInfo, resources.LightingBuffer, fsr.DilatedMotionVectors,
			fsr.DilatedReactiveMasks, fsr.FarthestDepthMip1, fsr.LumaInstability, fsr.NewLocks,
			history.InternalHistoryRead, history.InternalHistoryWrite, in constants);
		_accumulatePass.Record(context, in config);
	}

	private void ExecuteRcas(RenderGraphContext context)
	{
		var resources = context.ViewState.FrameResources;
		var history = resources.Fsr3;
		var constants = BuildConstants(context);
		var settings = resources.Config.AntiAliasing;
		var config = _rcasPass.BuildConfig(context, context.Device,
			history.InternalHistoryWrite, resources.ResolvedSceneColor, history.FrameInfo,
			in constants, settings.Sharpness, settings.EnableSharpening);
		_rcasPass.Record(context, in config);
	}

	private void ExecuteDebugView(RenderGraphContext context)
	{
		var resources = context.ViewState.FrameResources;
		var history = resources.Fsr3;
		var fsr = context.ViewState.GetPassState<ViewHandles>();
		var constants = BuildConstants(context);
		var config = _debugViewPass.BuildConfig(context, context.Device,
			fsr.DilatedReactiveMasks, fsr.DilatedMotionVectors, fsr.DilatedDepth,
			history.InternalHistoryWrite, history.CurrentLumaWrite, history.CurrentLumaRead,
			resources.ResolvedSceneColor, history.FrameInfo, in constants);
		_debugViewPass.Record(context, in config);
	}

	private static RenderGraphResourceHandle CreateTexture(RenderGraphResourceRegistry resources, Int2 size)
	{
		return resources.CreateTransientTexture(new TextureDescriptor(
			Math.Max(size.X, 1),
			Math.Max(size.Y, 1),
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)));
	}

	private static RenderGraphResourceHandle CreateUintTexture(RenderGraphResourceRegistry resources, Int2 size)
	{
		return resources.CreateTransientTexture(new TextureDescriptor(
			Math.Max(size.X, 1),
			Math.Max(size.Y, 1),
			TextureFormat.R32Uint,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
	}

	private static RenderGraphResourceHandle[] CreateSpdMips(RenderGraphResourceRegistry resources, Int2 sourceSize)
	{
		var result = new RenderGraphResourceHandle[Fsr3LumaPyramidPass.SpdMipCount];
		var size = new Int2(Math.Max(sourceSize.X / 2, 1), Math.Max(sourceSize.Y / 2, 1));
		for (var i = 0; i < result.Length; i++)
		{
			result[i] = CreateTexture(resources, size);
			size = new Int2(Math.Max(size.X / 2, 1), Math.Max(size.Y / 2, 1));
		}

		return result;
	}
}
