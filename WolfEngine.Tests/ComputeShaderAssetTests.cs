using WolfEngine.Editor.Tooling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class ComputeShaderAssetTests
{
	private const string DarkenShader = """
		import wolf;

		cbuffer DarkenSettings : register(b0)
		{
		    uint sceneColorHandle;
		    uint depthHandle;
		    float strength;
		    uint padding;
		};

		[shader("compute")]
		[numthreads(8, 8, 1)]
		void DarkenCS(uint3 id : SV_DispatchThreadID)
		{
		    if (any(id.xy >= WolfRenderSize())) return;
		    float depth = GetTexture(depthHandle).Load(int3(id.xy, 0)).r;
		    if (WolfIsBackgroundDepth(depth)) return;
		    float3 position = WolfReconstructWorldPosition(id.xy, depth);
		    RWTexture2D<float4> color = GetRWTexture(sceneColorHandle);
		    color[id.xy] = color[id.xy] * (position.y > 0 ? strength : 1.0f);
		}

		// [shader("compute")] void CommentedOutCS(uint3 id : SV_DispatchThreadID) {}

		[shader("compute")] [numthreads(64, 1, 1)]
		void ClearCS(uint3 id : SV_DispatchThreadID)
		{
		}
		""";

	private string _directory = null!;

	[SetUp]
	public void SetUp()
	{
		_directory = Path.Combine(Path.GetTempPath(), "wolf-compute-shader-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_directory);
	}

	[TearDown]
	public void TearDown() => Directory.Delete(_directory, recursive: true);

	[Test]
	public void FindsMarkedEntryPointsInSourceOrderAndIgnoresComments()
	{
		Assert.That(ComputeShaderCompiler.FindEntryPoints(DarkenShader), Is.EqualTo(new[] { "DarkenCS", "ClearCS" }));
	}

	[Test]
	public void CompilesEveryKernelAgainstThePublicModule()
	{
		if (OperatingSystem.IsWindows() == false)
			Assert.Ignore("D3D12 kernels are compiled on Windows.");

		var programId = ShaderProgramId.ForAsset(Guid.NewGuid());
		var kernels = CreateCompiler().Compile(WriteSource("darken.compute.slang", DarkenShader), programId, GraphicsBackendKind.D3D12);

		Assert.That(kernels.Select(kernel => kernel.Request.ComputeEntryPoint), Is.EqualTo(new[] { "DarkenCS", "ClearCS" }));
		var darken = kernels[0];
		Assert.Multiple(() =>
		{
			Assert.That(darken.Request.ProgramId, Is.EqualTo(programId));
			Assert.That(darken.ThreadGroupSize, Is.EqualTo(new ComputeThreadGroupSize(8, 8, 1)));
			Assert.That(darken.ReflectionLayout.GetConstantBuffer("DarkenSettings").RegisterIndex, Is.EqualTo(0u));
			var view = darken.ReflectionLayout.GetConstantBuffer("WolfView");
			Assert.That(view.RegisterIndex, Is.EqualTo(12u));
			// RenderFeatureComputePass fills these by path; a renamed field would leave them zero.
			Assert.That(view.Fields.Keys, Is.SupersetOf(new[]
			{
				"wolfView.invViewProjection", "wolfView.cameraOrigin", "wolfView.renderSizeX", "wolfView.renderSizeY"
			}));
			Assert.That(darken.ContentKey, Is.Not.EqualTo(kernels[1].ContentKey));
		});
	}

	[Test]
	public void TheManualsExampleShaderCompiles()
	{
		if (OperatingSystem.IsWindows() == false)
			Assert.Ignore("D3D12 kernels are compiled on Windows.");

		var manual = File.ReadAllText(Path.Combine(EngineSolutionRoot(), "Manual", "RenderFeatures.md"));
		var start = manual.IndexOf("```slang", StringComparison.Ordinal) + "```slang".Length;
		var source = manual[start..manual.IndexOf("```", start, StringComparison.Ordinal)];

		var kernels = CreateCompiler().Compile(WriteSource("fog.compute.slang", source),
			ShaderProgramId.ForAsset(Guid.NewGuid()), GraphicsBackendKind.D3D12);

		Assert.That(kernels.Single().ReflectionLayout.GetConstantBuffer("FogSettings").Fields.Keys,
			Is.SupersetOf(new[] { "sceneColorHandle", "maskSamplerHandle", "gridOrigin", "darkness" }));
	}

	[Test]
	public void RejectsConstantBuffersTheComputeRootSignatureDoesNotBind()
	{
		if (OperatingSystem.IsWindows() == false)
			Assert.Ignore("Register validation applies to D3D12 kernels.");

		var source = WriteSource("bad.compute.slang", """
			cbuffer Settings : register(b5) { float strength; };
			[shader("compute")] [numthreads(1, 1, 1)]
			void MainCS(uint3 id : SV_DispatchThreadID) { if (strength > 2) return; }
			""");

		Assert.That(() => CreateCompiler().Compile(source, ShaderProgramId.ForAsset(Guid.NewGuid()), GraphicsBackendKind.D3D12),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("b5"));
	}

	[Test]
	public void ReportsSourcesWithoutKernelsAndCompileErrors()
	{
		var compiler = CreateCompiler();
		var empty = WriteSource("empty.compute.slang", "float Helper(float x) { return x; }");
		Assert.That(() => compiler.Compile(empty, ShaderProgramId.ForAsset(Guid.NewGuid()), GraphicsBackendKind.D3D12),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("[shader(\"compute\")]"));

		if (OperatingSystem.IsWindows() == false)
			return;
		var broken = WriteSource("broken.compute.slang", """
			[shader("compute")] [numthreads(1, 1, 1)]
			void MainCS(uint3 id : SV_DispatchThreadID) { undefinedFunction(); }
			""");
		Assert.That(() => compiler.Compile(broken, ShaderProgramId.ForAsset(Guid.NewGuid()), GraphicsBackendKind.D3D12),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("MainCS"));
	}

	[Test]
	public void ArtifactRoundTripsEveryKernel()
	{
		var programId = ShaderProgramId.ForAsset(Guid.NewGuid());
		var kernels = new[] { CreateKernel(programId, "FirstCS"), CreateKernel(programId, "SecondCS") };
		var path = Path.Combine(_directory, ComputeShaderArtifactSerializer.FileName);

		ComputeShaderArtifactSerializer.Write(path, kernels);
		var shader = new ComputeShader("Fog", ComputeShaderArtifactSerializer.Read(path));

		Assert.That(shader.EntryPoints, Is.EquivalentTo(new[] { "FirstCS", "SecondCS" }));
		Assert.That(shader.GetKernel("SecondCS").ContentKey, Is.EqualTo("SecondCS-key"));
		Assert.That(shader.GetKernel("SecondCS").Bytecode.Compute!.Value.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
		Assert.That(() => shader.GetKernel("Missing"), Throws.TypeOf<KeyNotFoundException>().With.Message.Contains("FirstCS"));
	}

	[Test]
	public void AssetShaderIdsLiveInTheirOwnNamespace()
	{
		var id = Guid.NewGuid();
		Assert.That(ShaderProgramId.ForAsset(id).Value, Is.EqualTo($"asset://shaders/{id:D}"));
		Assert.That(() => new ShaderProgramId("project://shaders/fog"), Throws.ArgumentException);
	}

	private static CompiledShaderArtifact CreateKernel(ShaderProgramId programId, string entryPoint)
	{
		var threadGroup = new ComputeThreadGroupSize(8, 8, 1);
		return new CompiledShaderArtifact(
			ShaderRequest.Compute(programId, entryPoint, GraphicsBackendKind.D3D12),
			entryPoint + "-key",
			new ShaderBytecodeSet(compute: new byte[] { 1, 2, 3 }, computeThreadGroupSize: threadGroup),
			new ShaderReflectionLayout([], []),
			threadGroup);
	}

	private string WriteSource(string fileName, string source)
	{
		var path = Path.Combine(_directory, fileName);
		File.WriteAllText(path, source);
		return path;
	}

	private static string EngineSolutionRoot() =>
		Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

	private static ComputeShaderCompiler CreateCompiler() => new(new EngineShaderOptions
	{
		EngineContentRoot = Path.Combine(EngineSolutionRoot(), "WolfEngine")
	});
}
