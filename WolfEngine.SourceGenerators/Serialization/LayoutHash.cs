using System.Collections.Generic;
using System.Text;

namespace WolfEngine.SourceGenerators.Serialization;

/// <summary>
/// Hashes the shape of a cooked type: its name, then each member's name and type, recursively. Data cooked with a
/// different hash cannot be read by this build's generated reader, so loading it fails with a clear error instead.
/// </summary>
internal static class LayoutHash
{
	private const ulong OffsetBasis = 14695981039346656037UL;
	private const ulong Prime = 1099511628211UL;

	public static ulong Compute(TypeModel model)
	{
		var schema = new StringBuilder();
		AppendSchema(model, schema, new HashSet<TypeModel>());

		var hash = OffsetBasis;
		foreach (var value in Encoding.UTF8.GetBytes(schema.ToString()))
		{
			hash ^= value;
			hash *= Prime;
		}

		return hash;
	}

	private static void AppendSchema(TypeModel model, StringBuilder schema, HashSet<TypeModel> visiting)
	{
		switch (model)
		{
			case ScalarModel scalar:
				schema.Append(scalar.Schema);
				break;

			case NullableModel nullable:
				schema.Append("N(");
				AppendSchema(nullable.Inner, schema, visiting);
				schema.Append(')');
				break;

			case CollectionModel collection:
				schema.Append(collection.Kind switch
				{
					CollectionKind.Array => "A(",
					CollectionKind.List => "L(",
					_ => "S("
				});
				AppendSchema(collection.Element, schema, visiting);
				schema.Append(')');
				break;

			case DictionaryModel dictionary:
				schema.Append("D(");
				AppendSchema(dictionary.Key, schema, visiting);
				schema.Append(',');
				AppendSchema(dictionary.Value, schema, visiting);
				schema.Append(')');
				break;

			case ObjectModel obj:
				var name = ModelBuilder.GetMetadataFullName(obj.Symbol);
				if (visiting.Add(obj) == false)
				{
					schema.Append("R(").Append(name).Append(')');
					break;
				}

				schema.Append(obj.IsClass ? "C(" : "O(").Append(name).Append('{');
				foreach (var member in obj.MembersInLayoutOrder)
				{
					schema.Append(member.Name).Append(':');
					AppendSchema(member.Type, schema, visiting);
					schema.Append(';');
				}

				schema.Append("})");
				visiting.Remove(obj);
				break;

			default:
				schema.Append('?');
				break;
		}
	}
}
