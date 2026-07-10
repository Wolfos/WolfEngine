using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class ShaderArtifactTests
{
	[Test]
	public void Catalog_ContainsUniqueExistingEngineSources()
	{
		var catalog = new EngineShaderCatalog();
		var shaderRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
			"..", "..", "..", "..", "WolfEngine", "Shaders"));

		Assert.That(() => catalog.ValidateSourceTree(shaderRoot), Throws.Nothing);
		Assert.That(catalog.Programs.Select(program => program.Id).Distinct().Count(), Is.EqualTo(catalog.Programs.Count));
	}

	[Test]
	public void ShaderRequest_NormalizesDefinesDeterministically()
	{
		var first = ShaderRequest.Compute(EngineShaderPrograms.Bloom, "Downsample", GraphicsBackendKind.Metal,
			" Z=1 ", "A=2", "A=2");
		var second = ShaderRequest.Compute(EngineShaderPrograms.Bloom, "Downsample", GraphicsBackendKind.Metal,
			"A=2", "Z=1");

		Assert.That(first, Is.EqualTo(second));
		Assert.That(first.Defines, Is.EqualTo("A=2;Z=1"));
	}

	[Test]
	public void Catalog_RejectsUndeclaredStagesEntryPointsAndDefines()
	{
		var catalog = new EngineShaderCatalog();
		Assert.That(() => catalog.ValidateRequest(ShaderRequest.Compute(
			EngineShaderPrograms.Bloom, "BloomDownsampleCS", GraphicsBackendKind.Metal)), Throws.Nothing);
		Assert.That(() => catalog.ValidateRequest(ShaderRequest.Graphics(
			EngineShaderPrograms.GBuffer, "vertexShader", "fragmentShader", GraphicsBackendKind.Metal, "WOLF_ALPHA_CLIP")), Throws.Nothing);
		Assert.That(() => catalog.ValidateRequest(ShaderRequest.Compute(
			EngineShaderPrograms.GBuffer, "fragmentShader", GraphicsBackendKind.Metal)), Throws.InvalidOperationException);
		Assert.That(() => catalog.ValidateRequest(ShaderRequest.Compute(
			EngineShaderPrograms.Bloom, "NotDeclared", GraphicsBackendKind.Metal)), Throws.InvalidOperationException);
		Assert.That(() => catalog.ValidateRequest(ShaderRequest.Graphics(
			EngineShaderPrograms.GBuffer, "vertexShader", "fragmentShader", GraphicsBackendKind.Metal, "UNDECLARED")), Throws.InvalidOperationException);
	}

	[TestCase(ShaderRequestKind.Compute)]
	[TestCase(ShaderRequestKind.Graphics)]
	public void Artifact_RoundTripsBytecodeReflectionAndThreadGroup(ShaderRequestKind kind)
	{
		var fields = new Dictionary<string, ShaderConstantFieldLayout>
		{
			["Value"] = new("Value", 0, 4, ShaderConstantFieldValueKind.Float)
		};
		var reflection = new ShaderReflectionLayout(
			[new ShaderConstantBufferLayout("Params", 2, 16, fields)],
			[new ShaderResourceBindingLayout("Input", 4)]);
		var request = kind == ShaderRequestKind.Compute
			? ShaderRequest.Compute(EngineShaderPrograms.Bloom, "Downsample", GraphicsBackendKind.Metal)
			: ShaderRequest.Graphics(EngineShaderPrograms.GBuffer, "vertexShader", "fragmentShader", GraphicsBackendKind.D3D12);
		ComputeThreadGroupSize? threadGroup = kind == ShaderRequestKind.Compute ? new ComputeThreadGroupSize(8, 4, 1) : null;
		var bytecode = kind == ShaderRequestKind.Compute
			? new ShaderBytecodeSet(compute: new byte[] { 1, 2, 3 }, computeThreadGroupSize: threadGroup)
			: new ShaderBytecodeSet(new byte[] { 1, 2 }, new byte[] { 3, 4 });
		var artifact = new CompiledShaderArtifact(request, new string('A', 64), bytecode, reflection, threadGroup);

		using var stream = new MemoryStream();
		ShaderArtifactSerializer.Write(stream, artifact);
		stream.Position = 0;
		var restored = ShaderArtifactSerializer.Read(stream);

		Assert.That(restored.Request, Is.EqualTo(request));
		Assert.That(restored.ContentKey, Is.EqualTo(artifact.ContentKey));
		Assert.That(restored.Bytecode.Vertex?.ToArray(), Is.EqualTo(artifact.Bytecode.Vertex?.ToArray()));
		Assert.That(restored.Bytecode.Pixel?.ToArray(), Is.EqualTo(artifact.Bytecode.Pixel?.ToArray()));
		Assert.That(restored.Bytecode.Compute?.ToArray(), Is.EqualTo(artifact.Bytecode.Compute?.ToArray()));
		Assert.That(restored.ThreadGroupSize, Is.EqualTo(threadGroup));
		Assert.That(restored.ReflectionLayout.GetConstantBuffer("Params").GetFieldOrThrow("Value").Offset, Is.Zero);
		Assert.That(restored.ReflectionLayout.GetResource("Input").RegisterIndex, Is.EqualTo(4));
	}

	[Test]
	public void DevelopmentReload_RetainsPreviousArtifactAfterCompileFailure()
	{
		if (OperatingSystem.IsMacOS() == false)
			Assert.Ignore("Metal development reload validation only runs on macOS.");

		var sourceRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
			"..", "..", "..", "..", "WolfEngine", "Shaders"));
		var tempRoot = Path.Combine(Path.GetTempPath(), "WolfEngineShaderReloadTests", Guid.NewGuid().ToString("N"));
		var engineRoot = Path.Combine(tempRoot, "Engine");
		var copiedShaders = Path.Combine(engineRoot, "Shaders");
		var projectRoot = Path.Combine(tempRoot, "Project");
		try
		{
			foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
			{
				var destination = Path.Combine(copiedShaders, Path.GetRelativePath(sourceRoot, sourcePath));
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				File.Copy(sourcePath, destination);
			}
			Directory.CreateDirectory(projectRoot);
			var provider = new DevelopmentShaderProvider(
				new EngineShaderOptions { EngineContentRoot = engineRoot }, new EngineShaderCatalog());
			provider.SetProjectRoot(projectRoot);
			var request = ShaderRequest.Compute(
				EngineShaderPrograms.CopyToFinal, "CopyToFinalCS", GraphicsBackendKind.Metal);
			var previous = provider.GetArtifact(request);

			var editedPath = Path.Combine(copiedShaders, "copy_to_final.compute.slang");
			var originalSource = File.ReadAllText(editedPath);
			File.WriteAllText(editedPath, "this is deliberately invalid slang source");
			var failedReload = provider.Reload(GraphicsBackendKind.Metal);

			Assert.That(failedReload.AppliedArtifactCount, Is.Zero);
			Assert.That(failedReload.Failures, Has.Count.EqualTo(1));
			Assert.That(provider.GetArtifact(request), Is.SameAs(previous));

			File.WriteAllText(editedPath, originalSource + Environment.NewLine + "// reload validation edit");
			var successfulReload = provider.Reload(GraphicsBackendKind.Metal);
			Assert.That(successfulReload.Succeeded, Is.True);
			Assert.That(successfulReload.AppliedArtifactCount, Is.EqualTo(1));
			Assert.That(provider.GetArtifact(request).ContentKey, Is.Not.EqualTo(previous.ContentKey));
			Assert.That(Directory.EnumerateFiles(Path.Combine(projectRoot, "Library", "ShaderCache"),
				"*.wolfshader", SearchOption.AllDirectories), Is.Not.Empty);
		}
		finally
		{
			if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
		}
	}
}
