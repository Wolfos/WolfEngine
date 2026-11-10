using WolfEngine.Modeling;

namespace WolfEngine.Editor.UI;

/// <summary>
/// Keeps a document's compiled mesh up to date in the background. At most one compile runs at a time; edits made
/// while it runs are batched, and when it finishes the newest state is compiled next. A result that no longer matches
/// the document, because the user kept editing, is dropped rather than applied.
/// </summary>
internal sealed class ShapeCompileScheduler
{
	private readonly ShapeCompiler _compiler;
	private Task<CompileOutcome>? _running;
	private ulong _failedHash;
	private bool _hasFailedHash;

	public ShapeCompileScheduler(ShapeCompiler compiler)
	{
		_compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
	}

	public bool IsRunning => _running is { IsCompleted: false };
	public ShapeCompileStats? LastStats { get; private set; }
	public string? LastError { get; private set; }

	/// <summary>Counts every compile result applied to a document, for automation to wait on.</summary>
	public long AppliedCompileCount { get; private set; }

	/// <summary>Applies a finished compile and starts the next one if the document has changed. Call once per frame.</summary>
	public void Update(ShapeDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		if (_running is { IsCompleted: true } finished)
		{
			_running = null;
			Apply(document, finished);
		}

		if (_running is null &&
		    document.NeedsCompile &&
		    HasFailed(document.CurrentSourceHash) == false)
		{
			Start(document, cold: false);
		}
	}

	private bool HasFailed(ulong hash)
	{
		return _hasFailedHash && _failedHash == hash;
	}

	/// <summary>
	/// Blocks until the document's mesh matches its tree, for saving. Returns false when the shape cannot compile,
	/// in which case <see cref="LastError"/> says why.
	/// </summary>
	public bool CompileNow(ShapeDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		while (true)
		{
			if (_running is { } running)
			{
				running.Wait();
				_running = null;
				Apply(document, running);
			}

			if (document.NeedsCompile == false)
			{
				return true;
			}

			if (HasFailed(document.CurrentSourceHash))
			{
				return false;
			}

			Start(document, cold: false);
		}
	}

	/// <summary>Recompiles from scratch, discarding the warm-start history. The result replaces the mesh even if it is current.</summary>
	public void RecompileCold(ShapeDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		if (_running is { } running)
		{
			running.Wait();
			_running = null;
			Apply(document, running);
		}

		Start(document, cold: true);
	}

	/// <summary>Forgets a compile that belongs to a document that is closing. It finishes in the background unseen.</summary>
	public void Reset()
	{
		_running = null;
		_hasFailedHash = false;
		LastStats = null;
		LastError = null;
	}

	private void Start(ShapeDocument document, bool cold)
	{
		var hash = document.CurrentSourceHash;
		ShapeSdfProgram program;
		try
		{
			program = ShapeSdfProgram.Compile(document.Tree);
		}
		catch (Exception exception)
		{
			Fail(hash, exception);
			return;
		}

		var settings = document.Settings.Clone();
		var previous = cold ? null : document.Mesh;
		_running = Task.Run(() =>
		{
			try
			{
				return new CompileOutcome(hash, _compiler.Compile(program, settings, previous), null);
			}
			catch (Exception exception)
			{
				return new CompileOutcome(hash, null, exception);
			}
		});
	}

	private void Apply(ShapeDocument document, Task<CompileOutcome> finished)
	{
		var outcome = finished.Result;
		if (outcome.Error is { } error)
		{
			Fail(outcome.SourceHash, error);
			return;
		}

		LastStats = outcome.Result!.Stats;
		LastError = null;
		_hasFailedHash = false;
		if (outcome.SourceHash != document.CurrentSourceHash)
		{
			return;
		}

		document.SetCompiledMesh(outcome.Result.Mesh, outcome.SourceHash);
		AppliedCompileCount++;
	}

	/// <summary>Remembers a failure so the same broken state is not recompiled every frame.</summary>
	private void Fail(ulong hash, Exception exception)
	{
		LastError = exception.Message;
		_failedHash = hash;
		_hasFailedHash = true;
	}

	private sealed record CompileOutcome(ulong SourceHash, ShapeCompileResult? Result, Exception? Error);
}
