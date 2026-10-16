using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace WolfEngine.SourceGenerators.Serialization;

/// <summary>
/// Generates a binary reader and writer for every entity component struct and data asset class in the assembly,
/// and registers them with <c>SerializedTypeRegistry</c> from a module initializer.
/// </summary>
[Generator]
public sealed class BinarySerializationGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		// Only type names flow through the pipeline, so unrelated edits leave the cached candidate list untouched.
		var candidates = context.SyntaxProvider
			.CreateSyntaxProvider(
				static (node, _) => node is TypeDeclarationSyntax { BaseList: not null } &&
				                    node is StructDeclarationSyntax or ClassDeclarationSyntax or RecordDeclarationSyntax,
				static (syntaxContext, cancellationToken) => GetCandidateName(syntaxContext, cancellationToken))
			.Where(static name => name is not null)
			.Collect();

		context.RegisterSourceOutput(context.CompilationProvider.Combine(candidates), static (production, input) =>
		{
			Execute(production, input.Left, input.Right!);
		});
	}

	private static string? GetCandidateName(GeneratorSyntaxContext context, CancellationToken cancellationToken)
	{
		if (context.SemanticModel.GetDeclaredSymbol(context.Node, cancellationToken) is not INamedTypeSymbol symbol)
		{
			return null;
		}

		return ModelBuilder.GetMetadataFullName(symbol);
	}

	private static void Execute(SourceProductionContext context, Compilation compilation, ImmutableArray<string?> candidateNames)
	{
		var symbols = WellKnownSymbols.TryCreate(compilation);
		if (symbols is null)
		{
			return;
		}

		var diagnostics = new List<Diagnostic>();
		var builder = new ModelBuilder(compilation, symbols, diagnostics);
		var roots = new List<SerializationRoot>();
		var seen = new HashSet<string>();
		foreach (var name in candidateNames.OrderBy(static name => name, System.StringComparer.Ordinal))
		{
			context.CancellationToken.ThrowIfCancellationRequested();
			if (name is null || seen.Add(name) == false)
			{
				continue;
			}

			var type = compilation.Assembly.GetTypeByMetadataName(name);
			if (type is null)
			{
				continue;
			}

			var isComponent = IsComponentRoot(type, symbols);
			if (isComponent == false && IsDataAssetRoot(type, symbols) == false)
			{
				continue;
			}

			if (compilation.IsSymbolAccessibleWithin(type, compilation.Assembly) == false)
			{
				diagnostics.Add(Diagnostic.Create(
					SerializationDiagnostics.InaccessibleType,
					type.Locations.FirstOrDefault(),
					type.ToDisplayString()));
				continue;
			}

			var model = builder.Get(type);
			if (model is UnsupportedModel unsupported)
			{
				diagnostics.Add(Diagnostic.Create(
					SerializationDiagnostics.UnsupportedType,
					type.Locations.FirstOrDefault(),
					type.ToDisplayString(),
					unsupported.Reason));
				continue;
			}

			var key = $"{compilation.AssemblyName}:{name}";
			roots.Add(new SerializationRoot((ObjectModel)model, key, isComponent));
		}

		foreach (var diagnostic in diagnostics)
		{
			context.ReportDiagnostic(diagnostic);
		}

		if (roots.Count == 0)
		{
			return;
		}

		var source = new SerializerEmitter().Emit(compilation.AssemblyName ?? "Assembly", roots, builder.HelperModels);
		context.AddSource("WolfBinarySerializers.g.cs", source);
	}

	private static bool IsComponentRoot(INamedTypeSymbol type, WellKnownSymbols symbols)
	{
		if (type.TypeKind != TypeKind.Struct || type.IsGenericType || type.IsRefLikeType)
		{
			return false;
		}

		if (Implements(type, symbols.EntityComponent) == false)
		{
			return false;
		}

		// Components that are never persisted, such as transform bookkeeping, need no serializer.
		return HasAttribute(type, symbols.NotSerializedAttribute) == false;
	}

	/// <summary>Mirrors the editor's rule for what counts as a data asset type.</summary>
	private static bool IsDataAssetRoot(INamedTypeSymbol type, WellKnownSymbols symbols)
	{
		if (symbols.DataAsset is null)
		{
			return false;
		}

		if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType || type.IsStatic)
		{
			return false;
		}

		if (Implements(type, symbols.DataAsset) == false)
		{
			return false;
		}

		return type.InstanceConstructors.Any(static constructor =>
			constructor.Parameters.Length == 0 &&
			constructor.DeclaredAccessibility == Accessibility.Public);
	}

	private static bool Implements(INamedTypeSymbol type, INamedTypeSymbol interfaceType)
	{
		return type.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, interfaceType));
	}

	private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeType)
	{
		if (attributeType is null)
		{
			return false;
		}

		return symbol.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType));
	}
}
