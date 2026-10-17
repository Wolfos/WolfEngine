using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WolfEngine.ECS;
using WolfEngine.SourceGenerators.Serialization;

namespace WolfEngine.SourceGenerators.Tests;

[TestFixture]
public sealed class BinarySerializationGeneratorTests
{
	[Test]
	public void SupportedComponent_GeneratesCodeThatCompilesWithoutDiagnostics()
	{
		var result = Run("""
			using System.Collections.Generic;
			using System.Numerics;
			using System.Text.Json.Serialization;
			using WolfEngine.ECS;

			public enum Mode { A, B }

			public readonly struct Size
			{
				[JsonConstructor]
				public Size(int width, int height)
				{
					Width = width;
					Height = height;
				}

				public int Width { get; }
				public int Height { get; }
			}

			public struct Supported : IEntityComponent
			{
				public int Count;
				public string Name;
				public Vector3 Position;
				public Mode Mode;
				public Entity Target;
				public Size Size;
				public List<int> Values;
				public Dictionary<string, float> Weights;
				public int? Optional;
				public float Scale { get; set; }
				[JsonInclude] private int _hidden;
			}
			""");

		Assert.That(result.GeneratorDiagnostics, Is.Empty);
		Assert.That(result.CompilationErrors, Is.Empty, "The generated code must compile.");
		Assert.That(result.GeneratedSource, Does.Contain("\"TestAssembly:Supported\""));
	}

	[TestCase("object Anything;", "'object' could hold any type")]
	[TestCase("System.IDisposable Resource;", "interfaces could hold any implementation")]
	[TestCase("System.Action Callback;", "delegates are code, not data")]
	[TestCase("System.Type Kind;", "framework types other than")]
	[TestCase("NoConstructor Value;", "no parameterless constructor")]
	[TestCase("System.Collections.Generic.List<object> Items;", "its element type 'object' is not supported")]
	public void UnsupportedMember_ReportsWolf1001AtTheMember(string member, string reason)
	{
		var result = Run($$"""
			using WolfEngine.ECS;

			public sealed class NoConstructor
			{
				public NoConstructor(int a) { }
				public NoConstructor(string b) { }
			}

			public struct Broken : IEntityComponent
			{
				public int Fine;
				public {{member}}
			}
			""");

		var diagnostic = result.GeneratorDiagnostics.Single();
		Assert.That(diagnostic.Id, Is.EqualTo("WOLF1001"));
		Assert.That(diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Error));
		Assert.That(diagnostic.GetMessage(), Does.Contain(reason));
		Assert.That(diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan), Is.EqualTo(member.Split(' ')[^1].TrimEnd(';')));
		Assert.That(result.CompilationErrors, Is.Empty, "A component with an error is left out rather than generated broken.");
		Assert.That(result.GeneratedSource, Does.Not.Contain("Broken"));
	}

	[Test]
	public void UnsupportedMembers_ThatAreNotCooked_AreIgnored()
	{
		var result = Run("""
			using System.Text.Json.Serialization;
			using WolfEngine.ECS;

			public struct RuntimeState : IEntityComponent
			{
				public int Saved;
				[NotSerialized] public object Cache;
				[JsonIgnore] public System.Action Callback;
				public object Computed => null;
				private object _private;
			}
			""");

		Assert.That(result.GeneratorDiagnostics, Is.Empty);
		Assert.That(result.CompilationErrors, Is.Empty);
	}

	[Test]
	public void InaccessibleComponent_WarnsWithWolf1003()
	{
		var result = Run("""
			using WolfEngine.ECS;

			public class Outer
			{
				private struct Hidden : IEntityComponent
				{
					public int Value;
				}
			}
			""");

		var diagnostic = result.GeneratorDiagnostics.Single();
		Assert.That(diagnostic.Id, Is.EqualTo("WOLF1003"));
		Assert.That(diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Warning));
		Assert.That(result.CompilationErrors, Is.Empty);
	}

	[Test]
	public void ConstructorParameterWithoutAMember_ReportsWolf1002()
	{
		var result = Run("""
			using System.Text.Json.Serialization;
			using WolfEngine.ECS;

			public readonly struct Pair
			{
				[JsonConstructor]
				public Pair(int first, int unmatched)
				{
					First = first;
				}

				public int First { get; }
			}

			public struct UsesPair : IEntityComponent
			{
				public Pair Pair;
			}
			""");

		Assert.That(result.GeneratorDiagnostics.Select(diagnostic => diagnostic.Id), Does.Contain("WOLF1002"));
		Assert.That(result.GeneratorDiagnostics.Single(diagnostic => diagnostic.Id == "WOLF1002").GetMessage(), Does.Contain("'unmatched'"));
		Assert.That(result.CompilationErrors, Is.Empty);
	}

	[Test]
	public void NotSerializedComponent_IsNotRegistered()
	{
		var result = Run("""
			using WolfEngine.ECS;

			[NotSerialized]
			public struct Transient : IEntityComponent
			{
				public object Anything;
			}

			public struct Saved : IEntityComponent
			{
				public int Value;
			}
			""");

		Assert.That(result.GeneratorDiagnostics, Is.Empty);
		Assert.That(result.GeneratedSource, Does.Contain("Saved"));
		Assert.That(result.GeneratedSource, Does.Not.Contain("Transient"));
	}

	[Test]
	public void AssemblyWithoutComponents_GeneratesNothing()
	{
		var result = Run("public sealed class Plain { public int Value; }");

		Assert.That(result.GeneratedSource, Is.Empty);
		Assert.That(result.GeneratorDiagnostics, Is.Empty);
	}

	private static GeneratorRun Run(string source)
	{
		var compilation = CSharpCompilation.Create(
			"TestAssembly",
			[CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
			References.Value,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));
		var setupErrors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
		Assert.That(setupErrors, Is.Empty, "The test snippet itself must compile.");

		GeneratorDriver driver = CSharpGeneratorDriver.Create(new BinarySerializationGenerator());
		driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var generatorDiagnostics);
		var generated = driver.GetRunResult().GeneratedTrees.Select(tree => tree.GetText().ToString());

		return new GeneratorRun(
			generatorDiagnostics,
			updated.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray(),
			string.Concat(generated));
	}

	private static readonly Lazy<MetadataReference[]> References = new(() =>
	{
		var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

		return platform.Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location)).ToArray();
	});

	private sealed record GeneratorRun(
		ImmutableArray<Diagnostic> GeneratorDiagnostics,
		Diagnostic[] CompilationErrors,
		string GeneratedSource);
}
