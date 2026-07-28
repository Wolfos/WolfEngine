#nullable enable

using System;
using System.Collections.Generic;

namespace WolfEngine.Rendering.Abstraction;

public readonly record struct GpuRetirementStats(
	int UnsealedCount,
	int PendingCount,
	ulong ReleasedCount);

internal readonly struct GpuRetirementBatch
{
	internal static GpuRetirementBatch Empty => default;

	internal GpuRetirementBatch(GpuRetirementEntry[] entries)
	{
		Entries = entries;
	}

	internal GpuRetirementEntry[]? Entries { get; }
	internal bool IsEmpty => Entries is null || Entries.Length == 0;
}

internal readonly record struct GpuRetirementEntry(Action Release, string Name);

internal readonly record struct PendingGpuRetirement(
	GpuRetirementEntry Entry,
	ulong SubmissionId);

/// <summary>
/// Owns GPU-visible destruction for a device. Retirements are first detached from the unsealed queue
/// immediately before submission, then sealed with the ID returned by that exact submission.
/// </summary>
internal sealed class GpuRetirementQueue
{
	private readonly object _sync = new();
	private readonly List<GpuRetirementEntry> _unsealed = new();
	private readonly List<PendingGpuRetirement> _pending = new();
	private ulong _releasedCount;

	internal GpuRetirementStats Stats
	{
		get
		{
			lock (_sync)
			{
				return new GpuRetirementStats(_unsealed.Count, _pending.Count, _releasedCount);
			}
		}
	}

	internal void Retire(Action release, string? name)
	{
		ArgumentNullException.ThrowIfNull(release);
		lock (_sync)
		{
			_unsealed.Add(new GpuRetirementEntry(release, NormalizeName(name)));
		}
	}

	internal GpuRetirementBatch PrepareSubmission(GpuSubmissionKind submissionKind)
	{
		if (submissionKind != GpuSubmissionKind.PrimaryFrame)
		{
			return GpuRetirementBatch.Empty;
		}

		lock (_sync)
		{
			if (_unsealed.Count == 0)
			{
				return GpuRetirementBatch.Empty;
			}

			var entries = _unsealed.ToArray();
			_unsealed.Clear();
			return new GpuRetirementBatch(entries);
		}
	}

	internal void SealSubmission(in GpuRetirementBatch batch, ulong submissionId)
	{
		if (batch.IsEmpty)
		{
			return;
		}

		if (submissionId == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(submissionId), "Submission IDs must be non-zero.");
		}

		lock (_sync)
		{
			var entries = batch.Entries!;
			for (var i = 0; i < entries.Length; i++)
			{
				_pending.Add(new PendingGpuRetirement(entries[i], submissionId));
			}
		}
	}

	internal void RetireAfterSubmission(Action release, string? name, ulong submissionId)
	{
		ArgumentNullException.ThrowIfNull(release);
		if (submissionId == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(submissionId), "Submission IDs must be non-zero.");
		}

		lock (_sync)
		{
			_pending.Add(new PendingGpuRetirement(
				new GpuRetirementEntry(release, NormalizeName(name)),
				submissionId));
		}
	}

	internal void CancelSubmission(in GpuRetirementBatch batch)
	{
		if (batch.IsEmpty)
		{
			return;
		}

		lock (_sync)
		{
			var laterEntries = _unsealed.Count == 0 ? null : _unsealed.ToArray();
			_unsealed.Clear();
			_unsealed.AddRange(batch.Entries!);
			if (laterEntries is not null)
			{
				_unsealed.AddRange(laterEntries);
			}
		}
	}

	internal void ReleaseCompleted(ulong completedSubmissionId)
	{
		List<GpuRetirementEntry>? ready = null;
		lock (_sync)
		{
			for (var i = _pending.Count - 1; i >= 0; i--)
			{
				if (_pending[i].SubmissionId > completedSubmissionId)
				{
					continue;
				}

				ready ??= new List<GpuRetirementEntry>();
				ready.Add(_pending[i].Entry);
				_pending.RemoveAt(i);
			}
		}

		ready?.Reverse();
		ReleaseEntries(ready);
	}

	internal void ReleaseAllAfterIdle()
	{
		List<GpuRetirementEntry>? ready = null;
		lock (_sync)
		{
			if (_pending.Count > 0 || _unsealed.Count > 0)
			{
				ready = new List<GpuRetirementEntry>(_pending.Count + _unsealed.Count);
				for (var i = 0; i < _pending.Count; i++)
				{
					ready.Add(_pending[i].Entry);
				}
				_pending.Clear();

				ready.AddRange(_unsealed);
				_unsealed.Clear();
			}
		}

		ReleaseEntries(ready);
	}

	private void ReleaseEntries(List<GpuRetirementEntry>? entries)
	{
		if (entries is null)
		{
			return;
		}

		List<Exception>? failures = null;
		for (var i = 0; i < entries.Count; i++)
		{
			try
			{
				entries[i].Release();
			}
			catch (Exception exception)
			{
				failures ??= new List<Exception>();
				failures.Add(new InvalidOperationException(
					$"GPU retirement '{entries[i].Name}' failed.",
					exception));
			}
			finally
			{
				lock (_sync)
				{
					_releasedCount++;
				}
			}
		}

		if (failures is not null)
		{
			throw new AggregateException("One or more GPU resource retirements failed.", failures);
		}
	}

	private static string NormalizeName(string? name) =>
		string.IsNullOrWhiteSpace(name) ? "<unnamed GPU retirement>" : name;
}
