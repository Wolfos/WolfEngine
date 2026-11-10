using WolfEngine.Modeling;

namespace WolfEngine.Editor.UI;

/// <summary>
/// One open <c>.shape</c> file: the shape being edited, its undo history and its saved state.
/// <para>
/// An undo step is a snapshot of the tree, the settings and the compiled mesh together. Restoring the mesh with the
/// tree is what makes undo exact: the next compile starts from the previous mesh, so restoring only the tree would
/// leave a mesh compiled from the edit being undone. Meshes are immutable, so snapshots share them rather than
/// copying them.
/// </para>
/// </summary>
public sealed class ShapeDocument
{
	private readonly List<Snapshot> _undo = new();
	private readonly List<Snapshot> _redo = new();
	private long _nextStateId;
	private long _stateId;
	private long _savedStateId;
	private int _transactionDepth;
	private bool _transactionRecorded;
	private string? _transactionDescription;

	private ShapeDocument(string path, ShapeAsset asset, bool existsOnDisk)
	{
		Path = path;
		Tree = asset.Tree;
		Settings = asset.Settings;
		Mesh = asset.Mesh;
		MeshSourceHash = asset.MeshSourceHash;
		_stateId = NextStateId();

		// A document that has never been written starts unsaved.
		_savedStateId = existsOnDisk ? _stateId : -1;
	}

	public string Path { get; private set; }

	/// <summary>The tree being edited. Change it only inside <see cref="Edit"/>, so the change is recorded.</summary>
	public ShapeTree Tree { get; private set; }

	public ShapeCompileSettings Settings { get; private set; }

	/// <summary>The latest compiled mesh, or null when the shape has not been compiled.</summary>
	public ShapeMesh? Mesh { get; private set; }

	/// <summary>The source hash <see cref="Mesh"/> was compiled from.</summary>
	public ulong MeshSourceHash { get; private set; }

	/// <summary>Increments on every change, including undo and new compiled meshes.</summary>
	public long Revision { get; private set; }

	public bool IsDirty => _stateId != _savedStateId;
	public bool CanUndo => _undo.Count > 0;
	public bool CanRedo => _redo.Count > 0;
	public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
	public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

	/// <summary>The hash of the tree and settings as they are now.</summary>
	public ulong CurrentSourceHash => ShapeFile.ComputeSourceHash(Tree, Settings);

	/// <summary>True when the compiled mesh does not match the current tree and settings.</summary>
	public bool NeedsCompile => Mesh is null || MeshSourceHash != CurrentSourceHash;

	public static ShapeDocument Open(string path)
	{
		return new ShapeDocument(path, ShapeFile.Read(path), existsOnDisk: true);
	}

	public static ShapeDocument Create(string path, ShapeAsset asset)
	{
		return new ShapeDocument(path, asset, existsOnDisk: false);
	}

	/// <summary>Points the document at the file's new location after a rename or move.</summary>
	public void Relocate(string path)
	{
		Path = path;
	}

	/// <summary>
	/// Groups every edit until the scope is disposed into one undo step, such as a whole gizmo drag. Scopes nest;
	/// the outermost description wins.
	/// </summary>
	public IDisposable BeginTransaction(string description)
	{
		if (_transactionDepth == 0)
		{
			_transactionRecorded = false;
			_transactionDescription = description;
		}

		_transactionDepth++;
		return new TransactionScope(this);
	}

	public bool IsInTransaction => _transactionDepth > 0;

	/// <summary>Applies a change to the tree or settings as one undo step, or as part of the open transaction.</summary>
	public void Edit(string description, Action<ShapeTree, ShapeCompileSettings> change)
	{
		ArgumentNullException.ThrowIfNull(change);

		if (_transactionDepth == 0)
		{
			RecordUndo(description);
		}
		else if (_transactionRecorded == false)
		{
			RecordUndo(_transactionDescription ?? description);
			_transactionRecorded = true;
		}

		change(Tree, Settings);
		Touch();
	}

	/// <summary>
	/// Attaches a finished compile to the current state. Not an undo step of its own: it belongs to the edit it was
	/// compiled for, and undoing that edit restores the mesh from before it.
	/// </summary>
	public void SetCompiledMesh(ShapeMesh mesh, ulong sourceHash)
	{
		ArgumentNullException.ThrowIfNull(mesh);

		Mesh = mesh;
		MeshSourceHash = sourceHash;
		Touch();
	}

	public bool Undo()
	{
		if (_undo.Count == 0 || _transactionDepth > 0)
		{
			return false;
		}

		var snapshot = _undo[^1];
		_undo.RemoveAt(_undo.Count - 1);
		_redo.Add(TakeSnapshot(snapshot.Description));
		Restore(snapshot);
		return true;
	}

	public bool Redo()
	{
		if (_redo.Count == 0 || _transactionDepth > 0)
		{
			return false;
		}

		var snapshot = _redo[^1];
		_redo.RemoveAt(_redo.Count - 1);
		_undo.Add(TakeSnapshot(snapshot.Description));
		Restore(snapshot);
		return true;
	}

	/// <summary>Writes the shape, settings and compiled mesh to <see cref="Path"/>.</summary>
	public void Save()
	{
		ShapeFile.Write(Path, ToAsset());
		_savedStateId = _stateId;
	}

	/// <summary>A copy of the document's content, safe to hand to another thread.</summary>
	public ShapeAsset ToAsset()
	{
		return new ShapeAsset
		{
			Tree = Tree.Clone(),
			Settings = Settings.Clone(),
			Mesh = Mesh,
			MeshSourceHash = MeshSourceHash
		};
	}

	private void EndTransaction()
	{
		_transactionDepth--;
		if (_transactionDepth == 0)
		{
			_transactionDescription = null;
			_transactionRecorded = false;
		}
	}

	private void RecordUndo(string description)
	{
		_undo.Add(TakeSnapshot(description));
		_redo.Clear();
	}

	private Snapshot TakeSnapshot(string description)
	{
		return new Snapshot(description, Tree.Clone(), Settings.Clone(), Mesh, MeshSourceHash, _stateId);
	}

	private void Restore(Snapshot snapshot)
	{
		// The snapshot keeps its own copies, so the same step can be restored again after a redo.
		Tree = snapshot.Tree.Clone();
		Settings = snapshot.Settings.Clone();
		Mesh = snapshot.Mesh;
		MeshSourceHash = snapshot.MeshSourceHash;
		_stateId = snapshot.StateId;
		Revision++;
	}

	private void Touch()
	{
		_stateId = NextStateId();
		Revision++;
	}

	private long NextStateId()
	{
		_nextStateId++;
		return _nextStateId;
	}

	private sealed record Snapshot(
		string Description,
		ShapeTree Tree,
		ShapeCompileSettings Settings,
		ShapeMesh? Mesh,
		ulong MeshSourceHash,
		long StateId);

	private sealed class TransactionScope : IDisposable
	{
		private ShapeDocument? _document;

		public TransactionScope(ShapeDocument document)
		{
			_document = document;
		}

		public void Dispose()
		{
			var document = _document;
			_document = null;
			document?.EndTransaction();
		}
	}
}
