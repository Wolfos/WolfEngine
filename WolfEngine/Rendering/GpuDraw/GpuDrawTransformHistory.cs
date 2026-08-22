#nullable enable

using System.Collections.Generic;
using System.Numerics;

namespace WolfEngine.Rendering;

/// <summary>
/// World transforms as of the previously published frame, shared by every frame snapshot.
/// </summary>
/// <remarks>
/// Snapshot buffers rotate, so a draw record only carries the transform from the last frame that wrote
/// its own buffer, which is two published frames back rather than one. A motion vector has to describe
/// a single frame of movement, so the previous transform is tracked here, outside the rotation, and
/// every database reads the same value for a given draw.
/// <para>
/// The pair last written into the GPU instance table is tracked alongside it because that table is
/// shared between the snapshots as well: a database cannot tell from its own records what the table
/// holds, since the other snapshot wrote it last. Comparing against the uploaded pair is what keeps a
/// database from skipping an update the table still needs.
/// </para>
/// <para>
/// Only the thread that builds frames touches this. Render threads read transforms that were already
/// copied into the updates and records of the snapshot they own.
/// </para>
/// </remarks>
internal sealed class GpuDrawTransformHistory
{
	private readonly Dictionary<GpuDrawDatabase.DrawRecordKey, Entry> _entries = new();
	private readonly List<GpuDrawDatabase.DrawRecordKey> _pruneScratch = new();
	private int _stamp;

	/// <summary>Opens a frame. Draws not advanced before <see cref="PruneUnseen"/> are dropped.</summary>
	public void BeginFrame()
	{
		_stamp++;
	}

	/// <summary>
	/// Records this frame's transform for a draw and returns the one it had a frame earlier. A draw
	/// seen for the first time reports the transform it was created with, which is a zero motion vector.
	/// </summary>
	public Matrix4x4 Advance(in GpuDrawDatabase.DrawRecordKey key, in Matrix4x4 world)
	{
		if (_entries.TryGetValue(key, out var entry) == false)
		{
			_entries[key] = new Entry
			{
				World = world,
				Stamp = _stamp
			};

			return world;
		}

		var previousWorld = entry.World;
		entry.World = world;
		entry.Stamp = _stamp;
		_entries[key] = entry;
		return previousWorld;
	}

	/// <summary>Whether the GPU instance table still needs this transform pair.</summary>
	public bool IsUploadStale(in GpuDrawDatabase.DrawRecordKey key, in Matrix4x4 previousWorld, in Matrix4x4 world)
	{
		if (_entries.TryGetValue(key, out var entry) == false || entry.HasUpload == false)
		{
			return true;
		}

		return entry.UploadedPrevious.Equals(previousWorld) == false ||
		       entry.UploadedWorld.Equals(world) == false;
	}

	/// <summary>Records the transform pair an update just wrote into the GPU instance table.</summary>
	public void RecordUpload(in GpuDrawDatabase.DrawRecordKey key, in Matrix4x4 previousWorld, in Matrix4x4 world)
	{
		if (_entries.TryGetValue(key, out var entry) == false)
		{
			entry = new Entry
			{
				World = world,
				Stamp = _stamp
			};
		}

		entry.HasUpload = true;
		entry.UploadedPrevious = previousWorld;
		entry.UploadedWorld = world;
		_entries[key] = entry;
	}

	/// <summary>Forgets a draw, so that re-adding it later starts from a zero motion vector.</summary>
	public void Remove(in GpuDrawDatabase.DrawRecordKey key)
	{
		_entries.Remove(key);
	}

	/// <summary>Drops draws that were not advanced during the frame opened by <see cref="BeginFrame"/>.</summary>
	public void PruneUnseen()
	{
		_pruneScratch.Clear();
		foreach (var (key, entry) in _entries)
		{
			if (entry.Stamp != _stamp)
			{
				_pruneScratch.Add(key);
			}
		}

		for (var i = 0; i < _pruneScratch.Count; i++)
		{
			_entries.Remove(_pruneScratch[i]);
		}

		_pruneScratch.Clear();
	}

	private struct Entry
	{
		public Matrix4x4 World;
		public Matrix4x4 UploadedPrevious;
		public Matrix4x4 UploadedWorld;
		public bool HasUpload;
		public int Stamp;
	}
}
