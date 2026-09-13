# Built-in content

`Assets` contains the authoritative engine assets and their committed `.meta` files. The metadata owns the permanent GUIDs used by serialized asset references.

Do not commit a `Library` directory, SQLite database, or imported artifacts here. `EngineAssetMountProvider` runs these sources through the normal importer for the current artifact target and stores the result in the user's WolfEngine asset cache. The generated database is mounted read-only by the editor.

Importer versions and `ReadOnlyAssetMountLoader.CurrentContentVersion` are part of the cache key. Bump the relevant version whenever an importer or persisted artifact format changes.
