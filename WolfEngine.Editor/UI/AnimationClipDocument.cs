using System.Numerics;
using System.Text.Json;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

/// <summary>Addresses one dopesheet key: every transform channel of a bone on a frame.</summary>
public readonly record struct AnimationKeyRef(string Bone, int Frame);

/// <summary>
/// An authored clip or an imported take's annotations open for editing, with its own undo history.
/// <para>
/// Undo records only the tracks an edit touched rather than snapshotting the whole clip, because a
/// clip duplicated from mocap can hold a key on every frame for every bone, and a gizmo drag edits
/// continuously. Edits grouped with <see cref="BeginTransaction"/> become a single undo step.
/// </para>
/// </summary>
public sealed class AnimationClipDocument
{
	private readonly Stack<ClipChange> _undo = new();
	private readonly Stack<ClipChange> _redo = new();
	private ClipChange? _pendingChange;
	private int _transactionDepth;
	private long _nextStateId = 1;
	private long _stateId;
	private long _savedStateId;
	private string? _importedSubAssetKey;
	private float _importedDuration;

	public AnimationClipDocument(string path, AuthoredAnimationClipFile clip, bool existsOnDisk)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(clip);

		clip.Validate();
		Path = path;
		Clip = clip;

		// A clip that has never been written starts dirty, so closing it prompts instead of losing it.
		_savedStateId = existsOnDisk ? _stateId : -1;
	}

	public string Path { get; private set; }

	/// <summary>
	/// The clip being edited. Read it freely, but change it only through this document, or the
	/// change escapes undo and dirty tracking.
	/// </summary>
	public AuthoredAnimationClipFile Clip { get; }

	/// <summary>Increments on every change, including undo and redo. Use it to invalidate caches such as the preview clip.</summary>
	public long Revision { get; private set; }

	public bool IsDirty => _stateId != _savedStateId;
	public bool CanUndo => _undo.Count > 0 && _transactionDepth == 0;
	public bool CanRedo => _redo.Count > 0 && _transactionDepth == 0;
	public string? UndoDescription => _undo.Count > 0 ? _undo.Peek().Description : null;
	public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;
	public bool IsInTransaction => _transactionDepth > 0;

	public static AnimationClipDocument Open(string path) =>
		new(path, AuthoredAnimationClipJson.Read(path), existsOnDisk: true);

	/// <summary>Opens just the annotations of an imported take without copying its motion keys.</summary>
	public static AnimationClipDocument OpenImported(string sourcePath, string subAssetKey, AnimationClip clip)
	{
		var sourceMetadata = new AssetMetadataStore().Load(sourcePath + ".meta");
		var settings = sourceMetadata.GetImportSettingsOrDefault(() => new ModelImportSettings());
		var timeline = (settings.AnimationClips.GetValueOrDefault(subAssetKey) ?? new AnimationClipMetadata()).Clone();
		var framesPerSecond = Math.Clamp((int)MathF.Round(clip.FramesPerSecond), 1, AuthoredAnimationClipFile.MaxFramesPerSecond);
		var document = new AnimationClipDocument(sourcePath, new AuthoredAnimationClipFile
		{
			FramesPerSecond = framesPerSecond,
			LengthFrames = (int)MathF.Ceiling(clip.Duration * framesPerSecond),
			Curves = timeline.Curves,
			Markers = timeline.Markers
		}, existsOnDisk: true);
		document._importedSubAssetKey = subAssetKey;
		document._importedDuration = clip.Duration;

		return document;
	}

	public static AnimationClipDocument CreateNew(string path, Guid skeletonId)
	{
		var clip = new AuthoredAnimationClipFile
		{
			Skeleton = new AssetRef<Skeleton>
			{
				NodeId = skeletonId
			}
		};

		return new AnimationClipDocument(path, clip, existsOnDisk: false);
	}

	/// <summary>Follows the source file after a rename or move in the asset browser.</summary>
	public void Relocate(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (_importedSubAssetKey is null && AuthoredAnimationClipJson.IsAuthoredClipPath(path) == false)
		{
			throw new ArgumentException($"'{path}' is not an authored animation clip.", nameof(path));
		}

		Path = path;
	}

	public void Save()
	{
		if (_transactionDepth > 0)
		{
			throw new InvalidOperationException("Cannot save while an edit is in progress.");
		}

		if (_importedSubAssetKey is { } key)
		{
			var timeline = new AnimationClipMetadata { Curves = Clip.Curves, Markers = Clip.Markers };
			timeline.Validate(_importedDuration);
			var store = new AssetMetadataStore();
			var sourceMetadata = store.Load(Path + ".meta");
			var settings = sourceMetadata.GetImportSettingsOrDefault(() => new ModelImportSettings());
			settings.AnimationClips[key] = timeline.Clone();
			sourceMetadata.SetImportSettings(settings);
			store.Save(Path + ".meta", sourceMetadata);
		}
		else
		{
			Clip.Validate();
			AuthoredAnimationClipJson.Write(Path, Clip);
		}

		_savedStateId = _stateId;
	}

	/// <summary>
	/// Groups every edit until the returned scope is disposed into one undo step, for example a
	/// whole gizmo drag. Transactions nest; only the outermost one commits.
	/// </summary>
	public IDisposable BeginTransaction(string description)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(description);

		if (_transactionDepth == 0)
		{
			_pendingChange = new ClipChange(description, ClipSettings.Capture(Clip));
		}

		_transactionDepth++;
		return new TransactionScope(this);
	}

	public void Undo()
	{
		ThrowIfInTransaction();
		if (_undo.TryPop(out var change) == false)
		{
			return;
		}

		change.Revert(Clip);
		_redo.Push(change);
		_stateId = change.StateIdBefore;
		Revision++;
	}

	public void Redo()
	{
		ThrowIfInTransaction();
		if (_redo.TryPop(out var change) == false)
		{
			return;
		}

		change.Apply(Clip);
		_undo.Push(change);
		_stateId = change.StateIdAfter;
		Revision++;
	}

	public bool HasKey(string bone, int frame)
	{
		var track = Clip.FindBoneTrack(bone);
		if (track is null)
		{
			return false;
		}

		return track.Position.IndexOf(frame) >= 0 ||
		       track.Rotation.IndexOf(frame) >= 0 ||
		       track.Scale.IndexOf(frame) >= 0;
	}

	/// <summary>Frames holding a key on any channel of the bone, ascending. This is one dopesheet row.</summary>
	public IReadOnlyList<int> GetKeyFrames(string bone)
	{
		var track = Clip.FindBoneTrack(bone);
		if (track is null)
		{
			return Array.Empty<int>();
		}

		var frames = new SortedSet<int>(track.Position.Frames);
		frames.UnionWith(track.Rotation.Frames);
		frames.UnionWith(track.Scale.Frames);
		return frames.ToArray();
	}

	/// <summary>Writes a full transform key for the bone, replacing any key already on that frame.</summary>
	public void SetKey(string bone, int frame, BoneTransform transform)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(bone);
		ThrowIfFrameOutOfRange(frame);

		using var transaction = BeginTransaction("Set Key");

		var track = GetOrCreateBoneTrack(bone);
		track.Position.Set(frame, transform.Position);
		track.Rotation.Set(frame, Quaternion.Normalize(transform.Rotation));
		track.Scale.Set(frame, transform.Scale);
	}

	/// <summary>Removes every channel's key at each referenced frame. Returns false when nothing was keyed there.</summary>
	public bool DeleteKeys(IReadOnlyCollection<AnimationKeyRef> keys)
	{
		ArgumentNullException.ThrowIfNull(keys);

		using var transaction = BeginTransaction("Delete Keys");

		var removed = false;
		foreach (var group in GroupByBone(keys))
		{
			var track = Clip.FindBoneTrack(group.Key);
			if (track is null)
			{
				continue;
			}

			RecordTrack(track.GetBinding());
			foreach (var frame in group.Value)
			{
				removed |= track.Position.Remove(frame);
				removed |= track.Rotation.Remove(frame);
				removed |= track.Scale.Remove(frame);
			}

			RemoveIfEmpty(track);
		}

		return removed;
	}

	/// <summary>
	/// Shifts the referenced keys in time, overwriting unselected keys at the destination. Returns
	/// false without changing anything when any key would leave the clip.
	/// </summary>
	public bool MoveKeys(IReadOnlyCollection<AnimationKeyRef> keys, int frameDelta) =>
		ShiftKeys(keys, frameDelta, removeSource: true, "Move Keys");

	/// <summary>Copies the referenced keys by an offset, overwriting keys at the destination.</summary>
	public bool DuplicateKeys(IReadOnlyCollection<AnimationKeyRef> keys, int frameDelta) =>
		ShiftKeys(keys, frameDelta, removeSource: false, "Duplicate Keys");

	/// <summary>
	/// Changes the frame rate while keeping keys at the same time in seconds, snapped to the nearest
	/// new frame. When keys collide, the one that was closest to the frame wins.
	/// </summary>
	public void SetFramesPerSecond(int framesPerSecond)
	{
		if (framesPerSecond < 1 || framesPerSecond > AuthoredAnimationClipFile.MaxFramesPerSecond)
		{
			throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
		}

		if (framesPerSecond == Clip.FramesPerSecond)
		{
			return;
		}

		using var transaction = BeginTransaction("Change Frame Rate");

		var scale = (double)framesPerSecond / Clip.FramesPerSecond;
		for (var i = 0; i < Clip.TransformTracks.Count; i++)
		{
			var track = Clip.TransformTracks[i];
			RecordTrack(track.GetBinding());
			RescaleFrames(track.Position, scale);
			RescaleFrames(track.Rotation, scale);
			RescaleFrames(track.Scale, scale);
		}

		Clip.LengthFrames = (int)Math.Round(Clip.LengthFrames * scale, MidpointRounding.AwayFromZero);
		Clip.FramesPerSecond = framesPerSecond;
	}

	/// <summary>Number of keys that <see cref="SetLengthFrames"/> would delete, so the editor can ask first.</summary>
	public int CountKeysBeyond(int lengthFrames)
	{
		var count = 0;
		for (var i = 0; i < Clip.TransformTracks.Count; i++)
		{
			var track = Clip.TransformTracks[i];
			count += CountFramesBeyond(track.Position.Frames, lengthFrames);
			count += CountFramesBeyond(track.Rotation.Frames, lengthFrames);
			count += CountFramesBeyond(track.Scale.Frames, lengthFrames);
		}

		var duration = Clip.FrameToSeconds(lengthFrames);
		count += Clip.Markers.Count(marker => marker.Time > duration);
		count += Clip.Curves.Sum(curve => curve.Times.Count(time => time > duration));

		return count;
	}

	/// <summary>Sets the clip length, deleting any keys past the new end.</summary>
	public void SetLengthFrames(int lengthFrames)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(lengthFrames);

		if (lengthFrames == Clip.LengthFrames)
		{
			return;
		}

		using var transaction = BeginTransaction("Change Length");

		if (lengthFrames < Clip.LengthFrames)
		{
			for (var i = Clip.TransformTracks.Count - 1; i >= 0; i--)
			{
				var track = Clip.TransformTracks[i];
				if (CountKeysBeyond(track, lengthFrames) == 0)
				{
					continue;
				}

				RecordTrack(track.GetBinding());
				TrimFrames(track.Position, lengthFrames);
				TrimFrames(track.Rotation, lengthFrames);
				TrimFrames(track.Scale, lengthFrames);
				RemoveIfEmpty(track);
			}
		}

		if (lengthFrames < Clip.LengthFrames)
		{
			_pendingChange!.RecordMetadataBefore(Clip);
			var duration = Clip.FrameToSeconds(lengthFrames);
			Clip.Markers.RemoveAll(marker => marker.Time > duration);
			foreach (var curve in Clip.Curves)
			{
				var count = curve.Times.Count(time => time <= duration);
				curve.Times = curve.Times.Take(count).ToArray();
				curve.Values = curve.Values.Take(count).ToArray();
				curve.InTangents = curve.InTangents?.Take(count).ToArray();
				curve.OutTangents = curve.OutTangents?.Take(count).ToArray();
			}
		}

		Clip.LengthFrames = lengthFrames;
	}

	/// <summary>Replaces timeline annotations as one undoable edit; motion tracks are untouched.</summary>
	public void SetMetadata(AnimationClipMetadata metadata)
	{
		ArgumentNullException.ThrowIfNull(metadata);
		using var transaction = BeginTransaction("Edit Curves and Markers");

		_pendingChange!.RecordMetadataBefore(Clip);
		var copy = metadata.Clone();
		Clip.Curves = copy.Curves;
		Clip.Markers = copy.Markers;
	}

	public void SetLoop(bool loop)
	{
		using var transaction = BeginTransaction("Change Loop");

		Clip.Loop = loop;
	}

	public void SetSkeleton(Guid skeletonId)
	{
		using var transaction = BeginTransaction("Change Skeleton");

		Clip.Skeleton = new AssetRef<Skeleton>
		{
			NodeId = skeletonId
		};
	}

	public void SetPreviewModel(Guid modelId)
	{
		using var transaction = BeginTransaction("Change Preview Model");

		Clip.PreviewModelId = modelId;
	}

	private bool ShiftKeys(IReadOnlyCollection<AnimationKeyRef> keys, int frameDelta, bool removeSource, string description)
	{
		ArgumentNullException.ThrowIfNull(keys);

		if (frameDelta == 0 || keys.Count == 0)
		{
			return false;
		}

		foreach (var key in keys)
		{
			var destination = key.Frame + frameDelta;
			if (destination < 0 || destination > Clip.LengthFrames)
			{
				return false;
			}
		}

		using var transaction = BeginTransaction(description);

		var changed = false;
		foreach (var group in GroupByBone(keys))
		{
			var track = Clip.FindBoneTrack(group.Key);
			if (track is null)
			{
				continue;
			}

			RecordTrack(track.GetBinding());
			changed |= ShiftCurve(track.Position, group.Value, frameDelta, removeSource);
			changed |= ShiftCurve(track.Rotation, group.Value, frameDelta, removeSource);
			changed |= ShiftCurve(track.Scale, group.Value, frameDelta, removeSource);
		}

		return changed;
	}

	private static bool ShiftCurve<T>(AuthoredKeyCurve<T> curve, SortedSet<int> frames, int frameDelta, bool removeSource)
		where T : struct
	{
		// Lift every selected key before placing any, so keys moving onto each other's frames swap cleanly.
		var lifted = new List<(int Frame, T Value)>(frames.Count);
		foreach (var frame in frames)
		{
			if (curve.TryGetValue(frame, out var value))
			{
				lifted.Add((frame, value));
			}
		}

		if (lifted.Count == 0)
		{
			return false;
		}

		if (removeSource)
		{
			for (var i = 0; i < lifted.Count; i++)
			{
				curve.Remove(lifted[i].Frame);
			}
		}

		for (var i = 0; i < lifted.Count; i++)
		{
			curve.Set(lifted[i].Frame + frameDelta, lifted[i].Value);
		}

		return true;
	}

	private static void RescaleFrames<T>(AuthoredKeyCurve<T> curve, double scale)
		where T : struct
	{
		var frames = new List<int>(curve.Frames.Count);
		var values = new List<T>(curve.Values.Count);
		var errors = new List<double>(curve.Frames.Count);

		for (var i = 0; i < curve.Frames.Count; i++)
		{
			var exact = curve.Frames[i] * scale;
			var snapped = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
			var error = Math.Abs(exact - snapped);

			// Rounding is monotonic, so a collision can only be with the key just placed.
			var last = frames.Count - 1;
			if (last >= 0 && frames[last] == snapped)
			{
				if (error < errors[last])
				{
					values[last] = curve.Values[i];
					errors[last] = error;
				}

				continue;
			}

			frames.Add(snapped);
			values.Add(curve.Values[i]);
			errors.Add(error);
		}

		curve.Frames = frames;
		curve.Values = values;
	}

	private static void TrimFrames<T>(AuthoredKeyCurve<T> curve, int lengthFrames)
		where T : struct
	{
		for (var i = curve.Frames.Count - 1; i >= 0; i--)
		{
			if (curve.Frames[i] <= lengthFrames)
			{
				break;
			}

			curve.Frames.RemoveAt(i);
			curve.Values.RemoveAt(i);
		}
	}

	private static int CountKeysBeyond(AuthoredTransformTrack track, int lengthFrames) =>
		CountFramesBeyond(track.Position.Frames, lengthFrames) +
		CountFramesBeyond(track.Rotation.Frames, lengthFrames) +
		CountFramesBeyond(track.Scale.Frames, lengthFrames);

	private static int CountFramesBeyond(List<int> frames, int lengthFrames)
	{
		var count = 0;
		for (var i = frames.Count - 1; i >= 0; i--)
		{
			if (frames[i] <= lengthFrames)
			{
				break;
			}

			count++;
		}

		return count;
	}

	private static Dictionary<string, SortedSet<int>> GroupByBone(IReadOnlyCollection<AnimationKeyRef> keys)
	{
		var groups = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
		foreach (var key in keys)
		{
			if (groups.TryGetValue(key.Bone, out var frames) == false)
			{
				frames = new SortedSet<int>();
				groups.Add(key.Bone, frames);
			}

			frames.Add(key.Frame);
		}

		return groups;
	}

	private AuthoredTransformTrack GetOrCreateBoneTrack(string bone)
	{
		var binding = AnimationBinding.ForBone(bone);
		RecordTrack(binding);

		var track = Clip.FindTransformTrack(binding);
		if (track is not null)
		{
			return track;
		}

		track = AuthoredTransformTrack.ForBone(bone);
		Clip.TransformTracks.Add(track);
		return track;
	}

	/// <summary>An unkeyed bone has no track, so it falls back to the bind pose like any other untracked bone.</summary>
	private void RemoveIfEmpty(AuthoredTransformTrack track)
	{
		if (track.Kind != AnimationBindingKind.Bone || track.HasKeys())
		{
			return;
		}

		Clip.TransformTracks.Remove(track);
	}

	private void RecordTrack(AnimationBinding binding)
	{
		if (_pendingChange is null)
		{
			throw new InvalidOperationException("Clip tracks can only change inside a transaction.");
		}

		_pendingChange.RecordBefore(Clip, binding);
	}

	private void EndTransaction()
	{
		if (_transactionDepth == 0)
		{
			throw new InvalidOperationException("No transaction is open.");
		}

		_transactionDepth--;
		if (_transactionDepth > 0)
		{
			return;
		}

		var change = _pendingChange!;
		_pendingChange = null;

		if (change.CaptureAfter(Clip) == false)
		{
			return;
		}

		change.StateIdBefore = _stateId;
		change.StateIdAfter = _nextStateId++;
		_stateId = change.StateIdAfter;
		_undo.Push(change);
		_redo.Clear();
		Revision++;
	}

	private void ThrowIfInTransaction()
	{
		if (_transactionDepth > 0)
		{
			throw new InvalidOperationException("Cannot undo or redo while an edit is in progress.");
		}
	}

	private void ThrowIfFrameOutOfRange(int frame)
	{
		if (frame < 0 || frame > Clip.LengthFrames)
		{
			throw new ArgumentOutOfRangeException(nameof(frame), frame, $"Frame must be between 0 and {Clip.LengthFrames}.");
		}
	}

	private sealed class TransactionScope : IDisposable
	{
		private AnimationClipDocument? _document;

		public TransactionScope(AnimationClipDocument document)
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

	private readonly record struct ClipSettings(
		Guid SkeletonId,
		Guid PreviewModelId,
		int FramesPerSecond,
		int LengthFrames,
		bool Loop)
	{
		public static ClipSettings Capture(AuthoredAnimationClipFile clip) =>
			new(
				clip.Skeleton.NodeId,
				clip.PreviewModelId,
				clip.FramesPerSecond,
				clip.LengthFrames,
				clip.Loop);

		public void Restore(AuthoredAnimationClipFile clip)
		{
			clip.Skeleton = new AssetRef<Skeleton>
			{
				NodeId = SkeletonId
			};
			clip.PreviewModelId = PreviewModelId;
			clip.FramesPerSecond = FramesPerSecond;
			clip.LengthFrames = LengthFrames;
			clip.Loop = Loop;
		}
	}

	/// <summary>A track as it was before or after a change. A null track means it did not exist.</summary>
	private readonly record struct TrackState(AuthoredTransformTrack? Track, int Index);

	private sealed class ClipChange
	{
		private readonly Dictionary<AnimationBinding, TrackState> _before = new();
		private readonly Dictionary<AnimationBinding, TrackState> _after = new();
		private readonly ClipSettings _settingsBefore;
		private ClipSettings _settingsAfter;
		private string? _metadataBefore;
		private string? _metadataAfter;

		public ClipChange(string description, ClipSettings settingsBefore)
		{
			Description = description;
			_settingsBefore = settingsBefore;
		}

		public string Description { get; }
		public long StateIdBefore { get; set; }
		public long StateIdAfter { get; set; }

		/// <summary>Remembers the track's state the first time a change touches it; later touches keep that original.</summary>
		public void RecordBefore(AuthoredAnimationClipFile clip, AnimationBinding binding)
		{
			if (_before.ContainsKey(binding))
			{
				return;
			}

			_before.Add(binding, CaptureTrack(clip, binding));
		}

		public void RecordMetadataBefore(AuthoredAnimationClipFile clip)
		{
			_metadataBefore ??= SerializeMetadata(clip);
		}

		private static string SerializeMetadata(AuthoredAnimationClipFile clip) =>
			JsonSerializer.Serialize(new AnimationClipMetadata { Curves = clip.Curves, Markers = clip.Markers }, AnimationAssetJson.Options);

		private static void RestoreMetadata(AuthoredAnimationClipFile clip, string? json)
		{
			if (json is null) return;

			var timeline = JsonSerializer.Deserialize<AnimationClipMetadata>(json, AnimationAssetJson.Options)!;
			clip.Curves = timeline.Curves;
			clip.Markers = timeline.Markers;
		}

		/// <summary>Captures the result and drops tracks that ended unchanged. Returns false when nothing changed at all.</summary>
		public bool CaptureAfter(AuthoredAnimationClipFile clip)
		{
			_settingsAfter = ClipSettings.Capture(clip);

			foreach (var binding in _before.Keys.ToArray())
			{
				var after = CaptureTrack(clip, binding);
				if (TracksEqual(_before[binding].Track, after.Track))
				{
					_before.Remove(binding);
					continue;
				}

				_after.Add(binding, after);
			}

			_metadataAfter = _metadataBefore is null ? null : SerializeMetadata(clip);

			return _before.Count > 0 || _settingsBefore != _settingsAfter || _metadataBefore != _metadataAfter;
		}

		public void Revert(AuthoredAnimationClipFile clip)
		{
			RestoreTracks(clip, _before);
			_settingsBefore.Restore(clip);
			RestoreMetadata(clip, _metadataBefore);
		}

		public void Apply(AuthoredAnimationClipFile clip)
		{
			RestoreTracks(clip, _after);
			_settingsAfter.Restore(clip);
			RestoreMetadata(clip, _metadataAfter);
		}

		private static TrackState CaptureTrack(AuthoredAnimationClipFile clip, AnimationBinding binding)
		{
			for (var i = 0; i < clip.TransformTracks.Count; i++)
			{
				if (clip.TransformTracks[i].GetBinding() == binding)
				{
					return new TrackState(clip.TransformTracks[i].Clone(), i);
				}
			}

			return new TrackState(null, -1);
		}

		private static void RestoreTracks(AuthoredAnimationClipFile clip, Dictionary<AnimationBinding, TrackState> states)
		{
			// Remove first, then insert in ascending index order, so recorded indices land where they were.
			foreach (var binding in states.Keys)
			{
				var existing = clip.FindTransformTrack(binding);
				if (existing is not null)
				{
					clip.TransformTracks.Remove(existing);
				}
			}

			foreach (var state in states.Values.Where(state => state.Track is not null).OrderBy(state => state.Index))
			{
				var index = Math.Clamp(state.Index, 0, clip.TransformTracks.Count);
				clip.TransformTracks.Insert(index, state.Track!.Clone());
			}
		}

		private static bool TracksEqual(AuthoredTransformTrack? a, AuthoredTransformTrack? b)
		{
			if (a is null || b is null)
			{
				return a is null && b is null;
			}

			return CurvesEqual(a.Position, b.Position) &&
			       CurvesEqual(a.Rotation, b.Rotation) &&
			       CurvesEqual(a.Scale, b.Scale);
		}

		private static bool CurvesEqual<T>(AuthoredKeyCurve<T> a, AuthoredKeyCurve<T> b)
			where T : struct
		{
			return a.Interpolation == b.Interpolation &&
			       a.Frames.SequenceEqual(b.Frames) &&
			       a.Values.SequenceEqual(b.Values);
		}
	}
}
