using System.Numerics;
using System.Text.Json;

namespace WolfEngine;

/// <summary>
/// The serialized form of a prefab, as the build cooks it: the editor's <c>.prefab.json</c> file is written to
/// the pack verbatim, so this reads the same shape. Authoring-only members of that file (icons, prefab links and
/// overrides) are not part of the runtime contract and are ignored.
/// </summary>
public sealed class PrefabDocument
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public Guid RootEntityId { get; set; }
	public List<PrefabDocumentEntity> Entities { get; set; } = [];
}

public sealed class PrefabDocumentEntity
{
	public Guid EntityId { get; set; }
	public Guid? ParentEntityId { get; set; }
	public bool HasName { get; set; }
	public string Name { get; set; } = string.Empty;
	public bool Enabled { get; set; } = true;
	public Matrix4x4? LocalTransform { get; set; }
	public List<PrefabDocumentComponent> Components { get; set; } = [];
}

public sealed class PrefabDocumentComponent
{
	public string Type { get; set; } = string.Empty;
	public string TypeId { get; set; } = string.Empty;
	public JsonElement Data { get; set; }
}
