using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Tests;

/// <summary>
/// Guards the hand-written Metal compaction kernel against the C# and Slang declarations it has to
/// agree with.
///
/// gpu_draw_compact_icb.metal is the one shader in the engine that does not go through Slang: a Metal
/// indirect command buffer is an opaque object, so its surviving commands are moved with the GPU-side
/// copy_command intrinsic rather than copied as records. That means it re-declares the draw structs and
/// the visibility flags by hand, and nothing in the build would notice them drifting - the kernel would
/// simply read the wrong fields and compact the wrong draws, on Metal only, at runtime.
/// </summary>
[TestFixture]
public class MetalIndirectCompactionKernelTests
{
	private const string SourceResourceName = "WolfEngine.Shaders.GpuDraw.Metal.gpu_draw_compact_icb.metal";

	[Test]
	public void TheKernelSource_ShipsInsideTheAssembly()
	{
		Assert.That(ReadKernelSource(), Does.Contain("kernel void CSCompactIndirectCommands"));
	}

	[Test]
	public void GpuDrawBatchData_MatchesTheKernelsDeclaration()
	{
		AssertUintFieldsMatch<GpuDrawBatchData>("GpuDrawBatchData");
	}

	[Test]
	public void CompactionDrawGeometry_MatchesTheKernelsDeclaration()
	{
		AssertUintFieldsMatch<Rendering.Backend.Metal.MetalIndirectCommandBuffer.CompactionDrawGeometry>("IndirectDrawGeometry");
	}

	[Test]
	public void GpuDrawArgs_MatchesTheKernelsDeclaration()
	{
		AssertUintFieldsMatch<GpuDrawArgs>("GpuDrawArgs");
	}

	/// <summary>
	/// The visibility rules are shared with the record-copy kernel, so a change to one that misses the
	/// other silently gives Metal and Direct3D12 different sets of visible draws, or different instance counts.
	/// </summary>
	[Test]
	public void TheVisibilityRules_StayInStepWithTheSharedRecordCopyKernel()
	{
		var slang = File.ReadAllText(Path.Combine(ResolveShaderDirectory(), "GpuDraw", "gpu_draw_compact.compute.slang"));
		var metal = ReadKernelSource();
		Assert.Multiple(() =>
		{
			Assert.That(slang, Does.Contain("batch.capacity == 0"), "The record-copy kernel no longer skips empty batches.");
			Assert.That(metal, Does.Contain("batch.capacity == 0u"), "The Metal kernel no longer skips empty batches.");
			Assert.That(slang, Does.Contain("batch.laneIndex != laneIndex"), "The record-copy kernel no longer emits from the batch's lane only.");
			Assert.That(metal, Does.Contain("batch.laneIndex != params.laneIndex"), "The Metal kernel no longer emits from the batch's lane only.");
			Assert.That(slang, Does.Contain("min(g_DrawArgs[drawId].instanceCount, batch.capacity)"), "The record-copy kernel no longer clamps the visible count.");
			Assert.That(metal, Does.Contain("min(drawArgs[drawId].instanceCount, batch.capacity)"), "The Metal kernel no longer clamps the visible count.");
		});
	}

	/// <summary>
	/// Both kernels accumulate into the length half of a two-uint execution range, so the entry stride
	/// the engine allocates and offsets by has to be the stride they index with.
	/// </summary>
	[Test]
	public void BothKernels_IndexTheExecutionRangeTableWithTheSharedStride()
	{
		var entryUints = IndirectCompactionExecutionRange.StrideInBytes / sizeof(uint);
		var lengthUintOffset = IndirectCompactionExecutionRange.LengthOffsetInBytes / sizeof(uint);

		var slang = File.ReadAllText(Path.Combine(ResolveShaderDirectory(), "GpuDraw", "gpu_draw_compact.compute.slang"));
		Assert.Multiple(() =>
		{
			Assert.That(
				slang,
				Does.Contain($"g_ExecutionRanges[(executionRangeIndex * {entryUints}) + {lengthUintOffset}]"),
				"The record-copy kernel no longer indexes the execution range table with the shared stride.");
			Assert.That(
				ReadKernelSource(),
				Does.Contain($"executionRanges[(params.executionRangeIndex * {entryUints}u) + {lengthUintOffset}u]"),
				"The Metal kernel no longer indexes the execution range table with the shared stride.");
		});
	}

	/// <summary>
	/// Asserts that a struct of nothing but four-byte scalars is declared with the same fields, in the
	/// same order, on both sides. Comparing sizes alone would let two fields swap unnoticed.
	/// </summary>
	private static void AssertUintFieldsMatch<T>(string kernelStructName) where T : struct
	{
		var kernelFields = ReadStructFieldNames(ReadKernelSource(), kernelStructName);
		var managedFields = typeof(T)
			.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			.Select(field => field.Name.TrimStart('_'))
			.ToArray();

		Assert.Multiple(() =>
		{
			Assert.That(
				kernelFields.Select(name => name.ToLowerInvariant()),
				Is.EqualTo(managedFields.Select(name => name.ToLowerInvariant())),
				$"'{kernelStructName}' in the Metal kernel no longer declares the same fields as {typeof(T).Name}.");
			Assert.That(
				Marshal.SizeOf<T>(),
				Is.EqualTo(kernelFields.Length * sizeof(uint)),
				$"{typeof(T).Name} is no longer laid out as {kernelFields.Length} four-byte scalars.");
		});
	}

	private static string[] ReadStructFieldNames(string source, string structName)
	{
		var body = Regex.Match(source, @"struct\s+" + Regex.Escape(structName) + @"\s*\{(?<body>[^}]*)\}");
		Assert.That(body.Success, Is.True, $"'{structName}' is not declared in the kernel source.");

		return Regex.Matches(body.Groups["body"].Value, @"(?:uint|int|float)\s+(?<name>\w+)\s*;")
			.Select(match => match.Groups["name"].Value)
			.ToArray();
	}

	internal static uint ReadUintConstant(string source, string name)
	{
		var match = Regex.Match(source, Regex.Escape(name) + @"\s*=\s*(?<value>0[xX][0-9a-fA-F]+|\d+)u?\s*;");
		Assert.That(match.Success, Is.True, $"'{name}' is not declared in the shader source.");

		var value = match.Groups["value"].Value;
		return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
			? Convert.ToUInt32(value[2..], 16)
			: uint.Parse(value);
	}

	private static string ReadKernelSource()
	{
		using var stream = typeof(GpuDrawFlags).Assembly.GetManifestResourceStream(SourceResourceName);
		Assert.That(stream, Is.Not.Null, $"'{SourceResourceName}' is not embedded in WolfEngine.");

		using var reader = new StreamReader(stream!);
		return reader.ReadToEnd();
	}

	internal static string ResolveShaderDirectory()
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
