using Microsoft.CodeAnalysis;

namespace WolfEngine.SourceGenerators.Serialization;

internal static class SerializationDiagnostics
{
	private const string Category = "WolfEngine.Serialization";

	public static readonly DiagnosticDescriptor UnsupportedMember = new(
		id: "WOLF1001",
		title: "Member cannot be cooked to binary",
		messageFormat: "'{0}' on '{1}' has type '{2}', which cooked binary data cannot store: {3}. Mark the member [NotSerialized] or [JsonIgnore], or change its type.",
		category: Category,
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	public static readonly DiagnosticDescriptor UnsupportedType = new(
		id: "WOLF1002",
		title: "Type cannot be cooked to binary",
		messageFormat: "'{0}' cannot be cooked to binary: {1}",
		category: Category,
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	public static readonly DiagnosticDescriptor InaccessibleType = new(
		id: "WOLF1003",
		title: "Type cannot be cooked because generated code cannot reach it",
		messageFormat: "'{0}' is not accessible to generated code, so it has no binary serializer and cooking content that uses it will fail. Make it public or internal and not nested in a private type.",
		category: Category,
		defaultSeverity: DiagnosticSeverity.Warning,
		isEnabledByDefault: true);
}
