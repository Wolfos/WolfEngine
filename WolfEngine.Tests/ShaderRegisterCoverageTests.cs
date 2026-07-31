using System.Text.RegularExpressions;
using WolfEngine.Rendering.Backend.D3D12;
using WolfEngine.Rendering.Backend.Metal;

namespace WolfEngine.Tests;

/// <summary>
/// Guards the agreement between what the shaders declare and what the backends can bind.
///
/// D3D12's graphics and compute root signatures are hand-maintained tables in
/// <see cref="D3D12RootBindings"/>. A shader register with no root parameter behind it is rejected at
/// pipeline creation as a bare E_INVALIDARG, and only the first time that pass actually runs - the
/// screen-space decal pass sat broken for months on exactly this, because no scene contained a decal
/// so the pipeline was never created.
///
/// Metal has no root signature and cannot fail this way, which is why the constraint is invisible
/// while developing there. Its own limits are asserted here for the same reason, in reverse.
/// </summary>
[TestFixture]
public class ShaderRegisterCoverageTests
{
	/// <summary>
	/// Matches an explicit register binding with no register space, i.e. space 0. Bindless declarations
	/// carry ", space1" and live in descriptor tables, so they are deliberately excluded.
	/// </summary>
	private static readonly Regex SpaceZeroRegister = new(
		@"register\s*\(\s*([btus])(\d+)\s*\)",
		RegexOptions.Compiled);

	/// <summary>
	/// ImGui is bound through its own root signature on D3D12 (<c>D3D12ImGuiRenderer</c>) and its own
	/// vertex descriptor on Metal, so it is not subject to the shared limits.
	/// </summary>
	private static readonly string[] ExcludedShaders = ["imgui.slang"];

	/// <summary>
	/// The bindless header declares the argument buffers themselves, so its registers are expected to sit
	/// exactly on Metal's reserved indices rather than below them.
	/// </summary>
	private static readonly string[] MetalRangeExemptShaders = ["common_bindless.slang"];

	private static IEnumerable<TestCaseData> ShaderFiles()
	{
		foreach (var path in Directory.EnumerateFiles(ResolveShaderDirectory(), "*.slang", SearchOption.AllDirectories))
		{
			if (ExcludedShaders.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
			{
				continue;
			}

			yield return new TestCaseData(path).SetName($"{{m}}({Path.GetFileName(path)})");
		}
	}

	[TestCaseSource(nameof(ShaderFiles))]
	public void EveryDeclaredRegister_IsBindableByADirect3D12RootSignature(string shaderPath)
	{
		var isCompute = Path.GetFileName(shaderPath).Contains(".compute.", StringComparison.OrdinalIgnoreCase);
		foreach (var (kind, register, line) in ReadSpaceZeroRegisters(shaderPath))
		{
			// Include files are pulled into both graphics and compute shaders, and the two root
			// signatures expose different registers, so accept coverage by either. Entry-point shaders
			// are held to the signature they will actually be compiled against.
			var coveredByGraphics = IsGraphicsBindable(kind, register);
			var coveredByCompute = IsComputeBindable(kind, register);
			var covered = HasEntryPoint(shaderPath)
				? (isCompute ? coveredByCompute : coveredByGraphics)
				: coveredByGraphics || coveredByCompute;

			Assert.That(covered, Is.True,
				$"{Path.GetFileName(shaderPath)} line {line} declares register({kind}{register}), which no " +
				$"{(isCompute ? "compute" : "graphics")} root parameter provides. Add it to D3D12RootBindings and " +
				"to the matching root signature in D3D12Device, or move the binding to a mapped register.");
		}
	}

	[TestCaseSource(nameof(ShaderFiles))]
	public void EveryDeclaredRegister_FitsMetalsBufferIndexRange(string shaderPath)
	{
		if (MetalRangeExemptShaders.Contains(Path.GetFileName(shaderPath), StringComparer.OrdinalIgnoreCase))
		{
			Assert.Pass("Declares the bindless argument buffers themselves.");
		}

		foreach (var (kind, register, line) in ReadSpaceZeroRegisters(shaderPath))
		{
			if (kind == 's')
			{
				continue;
			}

			// Metal maps a register index straight onto a buffer index, and the bindless argument
			// buffers occupy the top of the range. A shader register at or above the lowest of those
			// silently collides with the descriptor heaps.
			Assert.That(register, Is.LessThan(MetalDescriptorTable.BindlessArgumentBufferIndexCounts),
				$"{Path.GetFileName(shaderPath)} line {line} declares register({kind}{register}), which collides " +
				$"with Metal's bindless argument buffers at indices " +
				$"{MetalDescriptorTable.BindlessArgumentBufferIndexCounts}-" +
				$"{MetalDescriptorTable.BindlessArgumentBufferIndexSamplers}.");
		}
	}

	/// <summary>
	/// Buffer index 0 is the vertex stream for the Default and Material vertex descriptors on Metal, so a
	/// graphics pass that binds its own vertex buffer cannot also use register 0. Passes that draw from
	/// the packed geometry buffers through an indirect command buffer are unaffected, which is why this
	/// reports rather than fails - the distinction is not visible from the shader source.
	/// </summary>
	[Test]
	public void GraphicsRegisterZeroUsage_IsReportedForMetalVertexStreamReview()
	{
		var offenders = new List<string>();
		foreach (var testCase in ShaderFiles())
		{
			var shaderPath = (string)testCase.Arguments[0]!;
			if (Path.GetFileName(shaderPath).Contains(".compute.", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			foreach (var (kind, register, line) in ReadSpaceZeroRegisters(shaderPath))
			{
				if (kind == 'b' && register == 0)
				{
					offenders.Add($"{Path.GetFileName(shaderPath)}:{line}");
				}
			}
		}

		TestContext.Out.WriteLine(offenders.Count == 0
			? "No graphics shader binds register(b0)."
			: "Graphics shaders binding register(b0), which collides with Metal's vertex stream if the pass " +
			  $"binds its own vertex buffer: {string.Join(", ", offenders)}");
		Assert.Pass();
	}

	private static IEnumerable<(char Kind, int Register, int Line)> ReadSpaceZeroRegisters(string shaderPath)
	{
		var lines = File.ReadAllLines(shaderPath);
		for (var i = 0; i < lines.Length; i++)
		{
			var match = SpaceZeroRegister.Match(lines[i]);
			if (match.Success == false)
			{
				continue;
			}

			yield return (match.Groups[1].Value[0], int.Parse(match.Groups[2].Value), i + 1);
		}
	}

	private static bool HasEntryPoint(string shaderPath)
	{
		var source = File.ReadAllText(shaderPath);
		return source.Contains("vertexShader(", StringComparison.Ordinal) ||
		       source.Contains("fragmentShader(", StringComparison.Ordinal) ||
		       source.Contains("[numthreads", StringComparison.Ordinal);
	}

	private static bool IsGraphicsBindable(char kind, int register) => kind switch
	{
		// Root 32-bit constants are a distinct root parameter type, so they are deliberately absent from
		// the CBV descriptor map - nothing binds them with SetGraphicsRootConstantBufferView. The draw
		// index is supplied per command by the execute-indirect command signature.
		'b' => D3D12RootBindings.TryGetGraphicsCbvIndex((uint)register, out _) ||
		       register == D3D12RootBindings.Graphics.DrawIndexConstantsRegister,
		't' => D3D12RootBindings.TryGetGraphicsSrvIndex((uint)register, out _),
		's' => true,
		_ => false
	};

	private static bool IsComputeBindable(char kind, int register) => kind switch
	{
		'b' => D3D12RootBindings.TryGetComputeCbvIndex((uint)register, out _),
		't' => D3D12RootBindings.TryGetComputeSrvIndex((uint)register, out _),
		'u' => D3D12RootBindings.TryGetComputeUavIndex((uint)register, out _),
		's' => true,
		_ => false
	};

	private static string ResolveShaderDirectory()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "WolfEngine", "Shaders");
			if (Directory.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException(
			$"Could not locate WolfEngine/Shaders by walking up from '{AppContext.BaseDirectory}'.");
	}
}
