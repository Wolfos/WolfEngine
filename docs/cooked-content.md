# Cooked content

The editor saves scene cells, prefabs and data assets as JSON. A game build cooks them
to a compact binary format, and a built game only ever reads that binary format. It
never parses JSON or uses reflection to load components, which is what lets the
runtime run under NativeAOT, as it must on iOS.

## Generated serializers

`WolfEngine.SourceGenerators` generates a binary reader and writer for every
`IEntityComponent` struct and every `IDataAsset` class in an assembly. It registers them
with `SerializedTypeRegistry` from a module initializer. Nothing has to be written per
type. A component is cookable as soon as it compiles.

Every assembly that declares components or data assets must reference the generator as
an analyzer. Gameplay projects created by the editor already do:

```xml
<ProjectReference Include="../../WolfEngine/WolfEngine.SourceGenerators/WolfEngine.SourceGenerators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

A cooked value holds exactly the members the editor's JSON options read back: public
fields and settable public properties, plus non-public members marked `[JsonInclude]`.
`[JsonIgnore]` and `[NotSerialized]` members are left out. Types read through a
`[JsonConstructor]`, or a class's only public constructor, are cooked through that
constructor, so readonly structs with get-only properties round-trip as they do in JSON.
Components marked `[NotSerialized]` are never saved, so they get no serializer.

## Diagnostics

The generator reports, at compile time, anything a game could not load:

- `WOLF1001` (error): a member's type cannot be cooked. Examples are `object`, interfaces,
  delegates, abstract classes, most framework types, and classes with no constructor
  to read them through. Mark the member `[NotSerialized]` or `[JsonIgnore]` if it is
  runtime state, or change its type.
- `WOLF1002` (error): a type cannot be cooked at all, such as a `[JsonConstructor]`
  parameter with no matching member.
- `WOLF1003` (warning): a component is not accessible to generated code, for example a
  private nested struct. Cooking content that uses it fails the build.

## Building and loading

`GameBuilder` cooks through `ContentCooker`: it reads each saved value with the editor's
JSON options, then writes it with the generated serializer. Saved types that cannot be
resolved fail the build rather than being dropped. Deserialization callbacks such as
`IJsonOnDeserialized` do not run while cooking. They build runtime state, like a mesh
renderer resolving its mesh, and run when the game reads the cooked value instead.

Each cooked file records the layout hash of every type it stores. Loading content cooked
by a different build of the code fails with a message to rebuild the game content rather
than misreading it. Entity references are stored as entity ids. Scene cells resolve them
across every cell of the scene, and prefabs resolve them to the instance being spawned.

The editor does not use the cooked format. It loads and saves JSON as before, and hot
reloaded gameplay assemblies are never registered, so they can still unload.
