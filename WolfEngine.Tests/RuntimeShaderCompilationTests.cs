using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class RuntimeShaderCompilationTests
{
	[Test]
	public void DeclaredRuntimeShaderVariantsCompileForHostBackend()
	{
		if (OperatingSystem.IsWindows() == false && OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Runtime shader compilation is supported only for the D3D12 and Metal host backends.");
		}

		var backend = OperatingSystem.IsMacOS()
			? GraphicsBackendKind.Metal
			: GraphicsBackendKind.D3D12;
		var catalog = new EngineShaderCatalog();
		var compiler = new ShaderCompiler();
		var failures = new List<string>();

		foreach (var request in catalog.GetDeclaredRuntimeRequests(backend))
		{
			try
			{
				var artifact = compiler.GetArtifact(request);
				if (request.ProgramId == EngineShaderPrograms.Fsr3PrepareReactivity)
				{
					ValidatePrepareReactivityInterface(artifact);
				}
			}
			catch (Exception exception)
			{
				failures.Add($"{request}: {exception.Message}");
			}
		}

		Assert.That(failures, Is.Empty,
			"One or more declared runtime shader variants failed to compile:\n" + string.Join("\n\n", failures));
	}

	private static void ValidatePrepareReactivityInterface(CompiledShaderArtifact artifact)
	{
		Assert.That(artifact.ThreadGroupSize, Is.EqualTo(new ComputeThreadGroupSize(8, 8, 1)));
		var bindless = artifact.ReflectionLayout.GetConstantBuffer("Fsr3BindlessHandles");
		string[] fields =
		[
			"reconstructedPrevNearestDepthHandle",
			"dilatedMotionVectorsReadHandle",
			"dilatedDepthHandle",
			"reactiveMaskHandle",
			"transparencyAndCompositionMaskHandle",
			"accumulationReadHandle",
			"shadingChangeHandle",
			"currentLumaReadHandle",
			"exposureHandle",
			"dilatedReactiveMasksHandle",
			"newLocksHandle",
			"accumulationWriteHandle",
			"pointSamplerHandle",
			"linearSamplerHandle"
		];

		foreach (var fieldName in fields)
		{
			Assert.That(bindless.GetFieldOrThrow(fieldName).ValueKind,
				Is.EqualTo(ShaderConstantFieldValueKind.UInt), fieldName);
		}
	}
}
