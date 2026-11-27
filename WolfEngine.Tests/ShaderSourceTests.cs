using Moq;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Tests;

/// <summary>
/// Compute kernels, graphics shaders and shader variant sets rebuild from new shaders after the render graph applies
/// a shader reload, and only then.
/// </summary>
[TestFixture]
public sealed class ShaderSourceTests
{
	private const string ComputeEntryPoint = "TestKernelCS";

	[Test]
	public void ComputeKernel_FirstUse_CompilesOnceAndCreatesOnePipeline()
	{
		var provider = new FakeShaderProvider();
		var device = new FakeDevice();
		var kernel = new ComputeKernel(new ShaderSource(provider), EngineShaderPrograms.Bloom, ComputeEntryPoint);

		var first = kernel.GetPipeline(device.Object);
		var second = kernel.GetPipeline(device.Object);

		Assert.That(second, Is.SameAs(first));
		Assert.That(provider.RequestCount, Is.EqualTo(1));
		Assert.That(device.Created, Has.Count.EqualTo(1));
	}

	[Test]
	public void ComputeKernel_AfterAppliedReload_RebuildsPipelineAndWritersFromTheNewShader()
	{
		var provider = new FakeShaderProvider();
		var source = new ShaderSource(provider);
		var device = new FakeDevice();
		var kernel = new ComputeKernel(source, EngineShaderPrograms.Bloom, ComputeEntryPoint);
		var oldPipeline = kernel.GetPipeline(device.Object);
		var oldRegister = kernel.Writer(FakeShaderProvider.ConstantBufferName).RegisterIndex;

		provider.Reload();
		source.ApplyRevision(provider.Revision);
		var newPipeline = kernel.GetPipeline(device.Object);

		Assert.That(newPipeline, Is.Not.SameAs(oldPipeline));
		Assert.That(device.Created[^1].Shaders.Compute!.Value.ToArray(), Is.EqualTo(FakeShaderProvider.BytecodeFor(provider.Revision)));
		Assert.That(kernel.Writer(FakeShaderProvider.ConstantBufferName).RegisterIndex, Is.Not.EqualTo(oldRegister));
		Assert.That(kernel.ThreadGroupSize, Is.EqualTo(FakeShaderProvider.ThreadGroupSizeFor(provider.Revision)));
	}

	[Test]
	public void ComputeKernel_ProviderReloadNotYetApplied_KeepsTheCurrentPipeline()
	{
		// The provider's revision moves before the render graph has cleared the pipeline cache. A pipeline built in
		// that window would be disposed by the clear while the kernel believed it current.
		var provider = new FakeShaderProvider();
		var device = new FakeDevice();
		var kernel = new ComputeKernel(new ShaderSource(provider), EngineShaderPrograms.Bloom, ComputeEntryPoint);
		var pipeline = kernel.GetPipeline(device.Object);

		provider.Reload();

		Assert.That(kernel.GetPipeline(device.Object), Is.SameAs(pipeline));
		Assert.That(provider.RequestCount, Is.EqualTo(1));
	}

	[Test]
	public void ComputeKernel_ReadsBeforeItIsCurrent_Throw()
	{
		var provider = new FakeShaderProvider();
		var source = new ShaderSource(provider);
		var device = new FakeDevice();
		var kernel = new ComputeKernel(source, EngineShaderPrograms.Bloom, ComputeEntryPoint);

		Assert.That(() => kernel.Writer(FakeShaderProvider.ConstantBufferName), Throws.InvalidOperationException);

		kernel.GetPipeline(device.Object);
		provider.Reload();
		source.ApplyRevision(provider.Revision);

		Assert.That(() => kernel.Writer(FakeShaderProvider.ConstantBufferName), Throws.InvalidOperationException);
		Assert.That(() => kernel.ThreadGroupSize, Throws.InvalidOperationException);
		Assert.That(() => kernel.Pipeline, Throws.InvalidOperationException);
	}

	[Test]
	public void ComputeKernel_Defines_GiveTheirOwnPipelineAndProfileName()
	{
		var provider = new FakeShaderProvider();
		var source = new ShaderSource(provider);
		var device = new FakeDevice();
		var plain = new ComputeKernel(source, EngineShaderPrograms.Bloom, ComputeEntryPoint);
		var defined = new ComputeKernel(source, EngineShaderPrograms.Bloom, ComputeEntryPoint, " B=2 ", "A=1", "");

		plain.GetPipeline(device.Object);
		defined.GetPipeline(device.Object);

		Assert.That(device.Created[0].Key.ShaderVariant, Is.Null);
		Assert.That(device.Created[1].Key.ShaderVariant, Is.EqualTo($"{ComputeEntryPoint}:A=1;B=2"));
		Assert.That(device.Created[0].Key, Is.Not.EqualTo(device.Created[1].Key));
		Assert.That(plain.ProfileName, Is.EqualTo(ComputeEntryPoint));
		Assert.That(provider.Requests[1].Defines, Is.EqualTo("A=1;B=2"));
	}

	[Test]
	public void GraphicsShader_CompilesOnceAndCreatesAPipelinePerState()
	{
		var provider = new FakeShaderProvider();
		var device = new FakeDevice();
		var shader = new GraphicsShader(new ShaderSource(provider), EngineShaderPrograms.GBuffer, "vertexShader", "fragmentShader");
		var opaque = CreateState(BlendMode.Opaque);
		var blended = CreateState(BlendMode.AlphaBlend);

		var first = shader.GetPipeline(device.Object, opaque);
		var again = shader.GetPipeline(device.Object, opaque);
		var other = shader.GetPipeline(device.Object, blended);

		Assert.That(again, Is.SameAs(first));
		Assert.That(other, Is.Not.SameAs(first));
		Assert.That(provider.RequestCount, Is.EqualTo(1));
		Assert.That(device.Created, Has.Count.EqualTo(2));
		Assert.That(device.Created[0].Key.ShaderVariant, Is.EqualTo("gbuffer"));
	}

	[Test]
	public void GraphicsShader_AfterAppliedReload_RebuildsEveryStateFromTheNewShader()
	{
		var provider = new FakeShaderProvider();
		var source = new ShaderSource(provider);
		var device = new FakeDevice();
		var shader = new GraphicsShader(source, EngineShaderPrograms.GBuffer, "vertexShader", "fragmentShader");
		var state = CreateState(BlendMode.Opaque);
		var oldPipeline = shader.GetPipeline(device.Object, state);

		provider.Reload();
		source.ApplyRevision(provider.Revision);
		var newPipeline = shader.GetPipeline(device.Object, state);

		Assert.That(newPipeline, Is.Not.SameAs(oldPipeline));
		Assert.That(provider.RequestCount, Is.EqualTo(2));
		Assert.That(device.Created[^1].Shaders.Vertex!.Value.ToArray(), Is.EqualTo(FakeShaderProvider.BytecodeFor(provider.Revision)));
	}

	[Test]
	public void ShaderVariantSet_GenerationIncreasesWhenAVariantIsAddedOrAReloadDropsTheSet()
	{
		var provider = new FakeShaderProvider();
		var source = new ShaderSource(provider);
		var device = new FakeDevice();
		var builds = 0;
		var set = new ShaderVariantSet<int, object>(
			source,
			(_, _) =>
			{
				builds++;
				return new object();
			});

		var first = set.Get(1, device.Object);
		var afterFirst = set.Generation;
		Assert.That(set.Get(1, device.Object), Is.SameAs(first));
		Assert.That(set.Generation, Is.EqualTo(afterFirst));

		set.Get(2, device.Object);
		var afterSecond = set.Generation;
		Assert.That(afterSecond, Is.GreaterThan(afterFirst));

		provider.Reload();
		Assert.That(set.TryGet(1, out _), Is.True, "A reload the render graph has not applied yet drops nothing.");

		source.ApplyRevision(provider.Revision);
		Assert.That(set.Generation, Is.GreaterThan(afterSecond));
		Assert.That(set.TryGet(1, out _), Is.False);
		Assert.That(set.Get(1, device.Object), Is.Not.SameAs(first));
		Assert.That(builds, Is.EqualTo(3));
	}

	private static GraphicsPipelineState CreateState(BlendMode blendMode)
	{
		return new GraphicsPipelineState(
			new RenderTargetFormats(new[] { TextureFormat.Rgba16Float }),
			new DepthStencilFormat(TextureFormat.Unknown),
			new RenderStateDescriptor(
				FillMode.Solid,
				CullMode.None,
				depthTestEnabled: false,
				depthWriteEnabled: false,
				blendMode));
	}

	/// <summary>Serves shaders whose bytecode, constant buffer register and thread group size follow the revision.</summary>
	private sealed class FakeShaderProvider : IShaderProvider
	{
		public const string ConstantBufferName = "Params";

		public long Revision { get; private set; } = 1;

		public event Action<long>? RevisionChanged;

		public List<ShaderRequest> Requests { get; } = new();

		public int RequestCount => Requests.Count;

		public static byte[] BytecodeFor(long revision)
		{
			return [(byte)revision, 0xC5];
		}

		public static ComputeThreadGroupSize ThreadGroupSizeFor(long revision)
		{
			return new ComputeThreadGroupSize((uint)(revision * 8), 1, 1);
		}

		public void Reload()
		{
			Revision++;
			RevisionChanged?.Invoke(Revision);
		}

		public CompiledShaderArtifact GetArtifact(ShaderRequest request)
		{
			Requests.Add(request);

			var bytecode = new ReadOnlyMemory<byte>(BytecodeFor(Revision));
			var reflection = new ShaderReflectionLayout(
			[
				new ShaderConstantBufferLayout(
					ConstantBufferName,
					(uint)Revision,
					16,
					new Dictionary<string, ShaderConstantFieldLayout>
					{
						["value"] = new ShaderConstantFieldLayout("value", 0, 4, ShaderConstantFieldValueKind.UInt)
					})
			]);
			if (request.Kind == ShaderRequestKind.Compute)
			{
				var threadGroupSize = ThreadGroupSizeFor(Revision);

				return new CompiledShaderArtifact(
					request,
					$"content-{Revision}",
					new ShaderBytecodeSet(compute: bytecode, computeThreadGroupSize: threadGroupSize),
					reflection,
					threadGroupSize);
			}

			return new CompiledShaderArtifact(
				request,
				$"content-{Revision}",
				new ShaderBytecodeSet(bytecode, bytecode),
				reflection);
		}

		public void SetProjectRoot(string? projectRootPath)
		{
		}

		public ShaderReloadResult Reload(GraphicsBackendKind backendKind)
		{
			Reload();

			return new ShaderReloadResult(1, []);
		}
	}

	private delegate IGfxPipeline CreatePipelineCallback(PipelineKey key, in ShaderBytecodeSet shaders);

	/// <summary>A device that hands out a new pipeline for every creation and records what it was created from.</summary>
	private sealed class FakeDevice
	{
		public FakeDevice()
		{
			Object = Mock.Of<IGfxDevice>(device => device.BackendKind == GraphicsBackendKind.Metal);
			Mock.Get(Object)
				.Setup(device => device.GetOrCreatePipeline(It.IsAny<PipelineKey>(), in It.Ref<ShaderBytecodeSet>.IsAny))
				.Returns(new CreatePipelineCallback((PipelineKey key, in ShaderBytecodeSet shaders) =>
				{
					var pipeline = Mock.Of<IGfxPipeline>();
					Created.Add((key, shaders, pipeline));

					return pipeline;
				}));
		}

		public IGfxDevice Object { get; }

		public List<(PipelineKey Key, ShaderBytecodeSet Shaders, IGfxPipeline Pipeline)> Created { get; } = new();
	}
}
