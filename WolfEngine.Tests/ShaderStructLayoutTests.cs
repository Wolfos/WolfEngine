using System.Text.RegularExpressions;

namespace WolfEngine.Tests;

/// <summary>
/// Guards struct layouts shared between CPU and GPU against a backend disagreement that no compiler
/// catches and no capture explains.
///
/// Metal sizes and aligns <c>float3</c> at 16 bytes where Direct3D packs it at 12. A struct that
/// declares one therefore has a different stride on the two backends, so a buffer the CPU fills to
/// one layout is read at the wrong offsets by the other. The failure is silent, backend-specific,
/// and looks like corrupt data rather than a binding mistake: a skinned mesh whose influences land
/// one member out explodes into spikes, with nothing in the pipeline reporting an error.
///
/// Only structs used as a structured buffer's element type are checked, because those are the ones
/// the CPU fills. Structs that never leave the GPU are free to use whatever is convenient.
/// </summary>
[TestFixture]
public class ShaderStructLayoutTests
{
	private static readonly Regex StructuredBufferElement = new(
		@"(?:RW)?StructuredBuffer\s*<\s*([A-Za-z_]\w*)\s*>",
		RegexOptions.Compiled);

	private static readonly Regex StructDeclaration = new(
		@"^\s*struct\s+([A-Za-z_]\w*)",
		RegexOptions.Compiled);

	private static readonly Regex ThreeComponentMember = new(
		@"^\s*(float3|int3|uint3|half3|double3)\s+(\w+)\s*;",
		RegexOptions.Compiled);

	[Test]
	public void NoBufferElementStruct_DeclaresAThreeComponentVector()
	{
		var shaderFiles = Directory
			.EnumerateFiles(ResolveShaderDirectory(), "*.slang", SearchOption.AllDirectories)
			// Third-party sources carry their own layout conventions and their own CPU-side mirrors.
			.Where(path => path.Contains(
				$"{Path.DirectorySeparatorChar}ThirdParty{Path.DirectorySeparatorChar}",
				StringComparison.Ordinal) == false)
			.ToArray();

		// A struct is declared in one file and used as a buffer element in another, so the element
		// types have to be gathered across the whole tree before any file can be judged.
		var bufferElementTypes = new HashSet<string>(StringComparer.Ordinal);
		foreach (var path in shaderFiles)
		{
			foreach (Match match in StructuredBufferElement.Matches(File.ReadAllText(path)))
			{
				bufferElementTypes.Add(match.Groups[1].Value);
			}
		}

		var offenders = new List<string>();
		foreach (var path in shaderFiles)
		{
			var lines = File.ReadAllLines(path);
			var currentStruct = (string?)null;

			for (var i = 0; i < lines.Length; i++)
			{
				var declaration = StructDeclaration.Match(lines[i]);
				if (declaration.Success)
				{
					var name = declaration.Groups[1].Value;
					currentStruct = bufferElementTypes.Contains(name)
						? name
						: null;
					continue;
				}

				if (currentStruct is null)
				{
					continue;
				}

				if (lines[i].Contains('}', StringComparison.Ordinal))
				{
					currentStruct = null;
					continue;
				}

				var member = ThreeComponentMember.Match(lines[i]);
				if (member.Success)
				{
					offenders.Add(
						$"{Path.GetFileName(path)} line {i + 1}: {currentStruct}.{member.Groups[2].Value} " +
						$"is a {member.Groups[1].Value}");
				}
			}
		}

		Assert.That(offenders, Is.Empty,
			"A struct used as a structured buffer element declares a three-component vector:\n" +
			string.Join("\n", offenders) +
			"\nMetal sizes these at 16 bytes and Direct3D at 12, so the struct's stride differs by " +
			"backend and the CPU-filled buffer is read at the wrong offsets. Use a float4 (or scalars) " +
			"instead, as every other shared struct in the engine does.");
	}

	private static string ResolveShaderDirectory() => Path.GetFullPath(Path.Combine(
		TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "WolfEngine", "Shaders"));
}
