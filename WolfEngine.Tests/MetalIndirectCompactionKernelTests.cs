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
	private const string SourceResourceName = "WolfEngine.Shaders.Metal.gpu_draw_compact_icb.metal";

	[Test]
	public void TheKernelSource_ShipsInsideTheAssembly()
	{
		Assert.That(ReadKernelSource(), Does.Contain("kernel void CSCompactIndirectCommands"));
	}

	[Test]
	public void GpuDrawCommand_MatchesTheKernelsDeclaration()
	{
		AssertUintFieldsMatch<GpuDrawCommand>("GpuDrawCommand");
	}

	[Test]
	public void GpuDrawArgs_MatchesTheKernelsDeclaration()
	{
		AssertUintFieldsMatch<GpuDrawArgs>("GpuDrawArgs");
	}

	[Test]
	public void TheDrawFlagsTheKernelTests_MatchTheOnesTheEngineEncodes()
	{
		var source = ReadKernelSource();
		Assert.Multiple(() =>
		{
			Assert.That(ReadUintConstant(source, "kDrawFlagActive"), Is.EqualTo(GpuDrawFlags.Active));
			Assert.That(ReadUintConstant(source, "kDrawFlagBucketShift"), Is.EqualTo((uint)GpuDrawFlags.BucketShift));
			Assert.That(ReadUintConstant(source, "kDrawFlagBucketMask"), Is.EqualTo(GpuDrawFlags.BucketMask));
		});
	}

	/// <summary>
	/// The visibility rules are shared with the record-copy kernel, so a change to one that misses the
	/// other silently gives Metal and Direct3D12 different sets of visible draws.
	/// </summary>
	[Test]
	public void TheVisibilityRules_StayInStepWithTheSharedRecordCopyKernel()
	{
		var slang = File.ReadAllText(Path.Combine(ResolveShaderDirectory(), "gpu_draw_compact.compute.slang"));
		Assert.Multiple(() =>
		{
			Assert.That(ReadUintConstant(slang, "DRAW_FLAG_ACTIVE"), Is.EqualTo(GpuDrawFlags.Active));
			Assert.That(ReadUintConstant(slang, "DRAW_FLAG_BUCKET_SHIFT"), Is.EqualTo((uint)GpuDrawFlags.BucketShift));
			Assert.That(ReadUintConstant(slang, "DRAW_FLAG_BUCKET_MASK"), Is.EqualTo(GpuDrawFlags.BucketMask));
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

		var slang = File.ReadAllText(Path.Combine(ResolveShaderDirectory(), "gpu_draw_compact.compute.slang"));
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

	private static uint ReadUintConstant(string source, string name)
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
