using System.Numerics;
using System.Text.Json;

namespace WolfEngine;

/// <summary>
/// An entity as the editor saves it in scene cells and prefabs. Both files store entities in this shape.
/// Authoring-only members of the saved entity (icons, prefab links and overrides) are not part of it and are ignored.
/// </summary>
public sealed class EntityDocument
{
	public Guid EntityId { get; set; }
	public Guid? ParentEntityId { get; set; }
	public bool HasName { get; set; }
	public string Name { get; set; } = string.Empty;
	public bool Enabled { get; set; } = true;
	public Matrix4x4? LocalTransform { get; set; }
	public List<ComponentDocument> Components { get; set; } = [];
}

/// <summary>A saved component: its type, and its value as the editor's JSON options wrote it.</summary>
public sealed class ComponentDocument
{
	/// <summary>The assembly-qualified name of the component type when it was saved.</summary>
	public string Type { get; set; } = string.Empty;

	/// <summary>
	/// The stable type id. Engine types use their assembly-qualified name, and gameplay types use
	/// <c>gameplay:</c> followed by their full name, so they survive gameplay assembly renames and versions.
	/// </summary>
	public string TypeId { get; set; } = string.Empty;

	public JsonElement Data { get; set; }
}
