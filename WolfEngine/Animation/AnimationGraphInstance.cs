namespace WolfEngine.Animation;

/// <summary>Per-character clocks and reusable buffers. Evaluation does not allocate after initialization.</summary>
public sealed class AnimationGraphInstance
{
    private readonly CompiledAnimationGraph _graph;
    private readonly float[] _parameters;
    private readonly int[] _integers;
    private readonly Pose[] _poses;
    private readonly Pose[] _transitionPoses;
    private readonly Pose _defaults;
    private readonly float[] _times, _previousTimes, _weights, _syncTimes, _blendTimes, _sampledTimes;
    private readonly int[] _sampledDefaults;
    private int _defaultsRevision;
    private readonly int[] _visited, _lastVisited, _sequences, _states, _sharedCursors;
    private readonly bool[] _started;
    private readonly int[][] _positionCursors, _rotationCursors, _scaleCursors, _propertyCursors, _curveCursors;
    private readonly string[] _transitionReasons;
    private readonly List<AnimationPresentationMarker> _markers = new(256);
    private readonly List<AnimationContribution> _contributions;
    private readonly List<AnimationStateInspection> _inspections;
    private int _tick;
    private float _delta, _pendingDelta;
    private bool _emitMarkers, _seeking;
    public CompiledAnimationGraph Program => _graph;
    public Pose Output { get; }
    public bool Playing { get; set; } = true;
    public float Speed { get; set; } = 1;
    public IReadOnlyList<AnimationPresentationMarker> Markers => _markers;
    public IReadOnlyList<AnimationContribution> Contributions => _contributions;
    public IReadOnlyList<AnimationStateInspection> States => _inspections;
    public float Time { get; private set; }
    /// <summary>Clip players that sampled keys during the last pose evaluation.</summary>
    public int SampledClipCount { get; private set; }

    internal AnimationGraphInstance(CompiledAnimationGraph graph)
    {
        _graph = graph;
        _parameters = graph.ParameterDefinitions.Select(p => p.Default).ToArray();
        _integers = graph.ParameterDefinitions.Select(p => (int)p.Default).ToArray();
        var count = graph.NodeCount;
        _poses = new Pose[count]; _transitionPoses = new Pose[count];
        _sampledTimes = new float[count]; Array.Fill(_sampledTimes, float.NaN); _sampledDefaults = new int[count];
        _times = new float[count]; _previousTimes = new float[count]; _weights = new float[count]; _syncTimes = new float[count]; _blendTimes = new float[count];
        _visited = new int[count]; _lastVisited = new int[count]; _sequences = new int[count]; _states = new int[count]; _sharedCursors = new int[count]; _started = new bool[count];
        _positionCursors = new int[count][]; _rotationCursors = new int[count][]; _scaleCursors = new int[count][]; _propertyCursors = new int[count][]; _curveCursors = new int[count][];
        _transitionReasons = new string[count];
        _contributions = new(count); _inspections = new(count);
        _defaults = CreatePose(); Output = CreatePose();
        for (var i = 0; i < count; i++)
        {
            _poses[i] = CreatePose(); _transitionPoses[i] = CreatePose();
            _times[i] = graph.Nodes[i].StartTime;
            var clip = graph.Clips[i]; var tracks = clip?.BoneSlots.Length ?? 0;
            _positionCursors[i] = new int[tracks]; _rotationCursors[i] = new int[tracks]; _scaleCursors[i] = new int[tracks];
            _propertyCursors[i] = new int[clip?.PropertySlots.Length ?? 0]; _curveCursors[i] = new int[clip?.Curves.Length ?? 0];
            _transitionReasons[i] = "Initial state";
        }
    }
    private Pose CreatePose()
    {
        var pose = new Pose(_graph.Skeleton.BoneCount, _graph.TransformBindings.Length, _graph.PropertyBindings.Length + _graph.CurveNames.Length);
        pose.SetToBindPose(_graph.Skeleton);
        return pose;
    }
    public void SetTransformDefault(int slot, in BoneTransform value) { _defaults.Transforms[slot] = value; _defaultsRevision++; }
    public void SetPropertyDefault(int slot, float value) { _defaults.Values[slot] = value; _defaultsRevision++; }
    public void SetFloat(AnimationParameterHandle handle, float value) { Validate(handle, AnimationParameterType.Float); if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); _parameters[handle.Index] = value; }
    public void SetBool(AnimationParameterHandle handle, bool value) { Validate(handle, AnimationParameterType.Bool); _parameters[handle.Index] = value ? 1 : 0; }
    public void SetInteger(AnimationParameterHandle handle, int value) { Validate(handle, AnimationParameterType.Integer); _integers[handle.Index] = value; _parameters[handle.Index] = value; }
    public int GetInteger(AnimationParameterHandle handle) { Validate(handle, AnimationParameterType.Integer); return _integers[handle.Index]; }
    public void SetAction(AnimationActionHandles handles, AnimationActionInput action)
    {
        SetInteger(handles.Kind, action.Kind); SetInteger(handles.Variant, action.Variant);
        SetInteger(handles.Sequence, action.SequenceId); SetFloat(handles.Time, action.NormalizedTime);
    }
    public float GetParameter(AnimationParameterHandle handle) { Validate(handle, handle.Type); return _parameters[handle.Index]; }
    public float GetCurve(AnimationCurveHandle handle)
    {
        if (handle.GraphId != _graph.Id || handle.Index < 0 || handle.Index >= _graph.CurveNames.Length) throw new ArgumentException("Curve handle belongs to another program.");
        return Output.Values[_graph.PropertyBindings.Length + handle.Index];
    }
    private void Validate(AnimationParameterHandle handle, AnimationParameterType type)
    {
        if (handle.GraphId != _graph.Id || handle.Index < 0 || handle.Index >= _parameters.Length || handle.Type != type || _graph.ParameterDefinitions[handle.Index].Type != type)
            throw new ArgumentException("Parameter handle belongs to another program or has the wrong type.");
    }
    /// <summary>Sets internal clocks without delivering markers. External action time still comes from parameters.</summary>
    public void Seek(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        Time = seconds; _pendingDelta = 0;
        for (var i = 0; i < _times.Length; i++)
        {
            _times[i] = seconds + _graph.Nodes[i].StartTime;
            _previousTimes[i] = _times[i]; _lastVisited[i] = 0; _started[i] = false; _blendTimes[i] = 0;
        }
        _seeking = true;
        try { Evaluate(0, false); } finally { _seeking = false; }
    }
    public void AdvanceClock(float deltaTime)
    {
        if (!float.IsFinite(deltaTime) || deltaTime < 0 || !float.IsFinite(Speed) || Speed < 0) throw new ArgumentOutOfRangeException(nameof(deltaTime));
        var delta = Playing ? deltaTime * Speed : 0;
        Time += delta; _pendingDelta += delta;
    }
    public bool Evaluate(float deltaTime, bool emitMarkers = true)
    {
        AdvanceClock(deltaTime);
        return EvaluatePose(emitMarkers);
    }
    /// <summary>Sample accumulated logical time; skipped samples preserve crossed marker intervals.</summary>
    public bool EvaluatePose(bool emitMarkers = true)
    {
        _delta = _pendingDelta; _pendingDelta = 0;
        _emitMarkers = emitMarkers && _delta > 0;
        if (++_tick == int.MaxValue) { Array.Clear(_visited); Array.Clear(_lastVisited); _tick = 1; }
        Array.Clear(_weights); Array.Fill(_syncTimes, float.NaN);
        SampledClipCount = 0;
        _markers.Clear(); _contributions.Clear(); _inspections.Clear();
        EvaluateNode(_graph.Output);
        AccumulateWeights(_graph.Output, 1);
        var pose = _poses[_graph.Output];
        var changed = !Same(Output, pose);
        Output.CopyFrom(pose);
        for (var i = 0; i < _weights.Length; i++)
        {
            if (_weights[i] <= 0 || _visited[i] != _tick) continue;
            if (_graph.Clips[i] is { } clip)
            {
                _contributions.Add(new(_graph.Nodes[i].Id, _graph.Nodes[i].ClipSlot, _weights[i], NormalizedTime(i)));
                if (_emitMarkers) CollectMarkers(i, clip);
            }
            if (_graph.Nodes[i].Kind == AnimationNodeKind.StateMachine)
                _inspections.Add(new(_graph.Nodes[i].Id, _states[i], _blendTimes[i], _transitionReasons[i]));
        }
        return changed;
    }
    private void EvaluateNode(int index)
    {
        if (_visited[index] == _tick) return;
        _visited[index] = _tick;
        var node = _graph.Nodes[index]; var inputs = _graph.Inputs[index]; var pose = _poses[index];
        var wasActive = _lastVisited[index] == _tick - 1;
        _lastVisited[index] = _tick;
        switch (node.Kind)
        {
            case AnimationNodeKind.BindPose: pose.CopyFrom(_defaults); break;
            case AnimationNodeKind.Clip: Sample(index, wasActive); break;
            case AnimationNodeKind.Blend:
            case AnimationNodeKind.MaskedBlend:
                var alpha = Math.Clamp(Value(index), 0, 1);
                if (alpha < 1 || node.Kind == AnimationNodeKind.MaskedBlend && _graph.MinimumMaskWeights[index] < 1) EvaluateNode(inputs[0]);
                if (alpha > 0) EvaluateNode(inputs[1]);
                if (alpha == 0) pose.CopyFrom(_poses[inputs[0]]);
                else if (node.Kind == AnimationNodeKind.MaskedBlend)
                    Pose.Blend(_poses[inputs[0]], _poses[inputs[1]], alpha, pose, _graph.Masks[index]);
                else if (alpha == 1) pose.CopyFrom(_poses[inputs[1]]);
                else Pose.Blend(_poses[inputs[0]], _poses[inputs[1]], alpha, pose);
                break;
            case AnimationNodeKind.Select:
                var selection = Math.Clamp((int)Value(index), 0, inputs.Length - 1);
                EvaluateNode(inputs[selection]); pose.CopyFrom(_poses[inputs[selection]]); break;
            case AnimationNodeKind.Locomotion1D:
                Locomotion(index, wasActive); break;
            case AnimationNodeKind.StateMachine:
                StateMachine(index, wasActive); break;
            case AnimationNodeKind.Output:
            case AnimationNodeKind.CurveRemap:
                EvaluateNode(inputs[0]); pose.CopyFrom(_poses[inputs[0]]);
                if (node.Kind == AnimationNodeKind.CurveRemap)
                {
                    var slot = _graph.PropertyBindings.Length + _graph.RemapCurves[index];
                    pose.Values[slot] = pose.Values[slot] * node.CurveScale + node.CurveOffset;
                }
                break;
        }
    }
    private float Value(int index) => _graph.Parameters[index] >= 0 ? _parameters[_graph.Parameters[index]] : _graph.Nodes[index].Value;
    private void Locomotion(int index, bool wasActive)
    {
        var node = _graph.Nodes[index]; var inputs = _graph.Inputs[index];
        var speed = Math.Clamp(Value(index), node.Thresholds[0], node.Thresholds[^1]);
        Bracket(index, speed, out var a, out var b, out var weight);
        var frequency = Frequency(a) * (1 - weight) + Frequency(b) * weight;
        if (!wasActive && node.RestartOnActivation && !_seeking) _times[index] = node.StartTime;
        _times[index] += _delta * frequency;
        for (var i = 0; i < inputs.Length; i++) _syncTimes[inputs[i]] = _times[index] * _graph.Clips[inputs[i]]!.Clip.Duration;
        if (a == b || weight == 0)
        {
            EvaluateNode(inputs[a]); _poses[index].CopyFrom(_poses[inputs[a]]);
        }
        else
        {
            EvaluateNode(inputs[a]); EvaluateNode(inputs[b]);
            Pose.Blend(_poses[inputs[a]], _poses[inputs[b]], weight, _poses[index]);
        }
        float Frequency(int position)
        {
            var threshold = node.Thresholds[position]; var clip = _graph.Clips[inputs[position]]!.Clip;
            return threshold > 0 && clip.Duration > 0 ? speed / threshold / clip.Duration * _graph.Nodes[inputs[position]].Speed * _graph.Clips[inputs[position]]!.PlaybackSpeed :
                speed == 0 && clip.Duration > 0 ? 1 / clip.Duration * _graph.Nodes[inputs[position]].Speed * _graph.Clips[inputs[position]]!.PlaybackSpeed : 0;
        }
    }
    private void Bracket(int index, float value, out int a, out int b, out float weight)
    {
        var thresholds = _graph.Nodes[index].Thresholds;
        a = 0;
        while (a + 1 < thresholds.Count && value >= thresholds[a + 1]) a++;
        b = Math.Min(a + 1, thresholds.Count - 1);
        weight = a == b ? 0 : Math.Clamp((value - thresholds[a]) / (thresholds[b] - thresholds[a]), 0, 1);
    }
    private void StateMachine(int index, bool wasActive)
    {
        var node = _graph.Nodes[index]; var inputs = _graph.Inputs[index];
        if (!wasActive && node.RestartOnActivation) { _states[index] = 0; _blendTimes[index] = 0; }
        if (!_started[index]) { EvaluateNode(inputs[_states[index]]); _poses[index].CopyFrom(_poses[inputs[_states[index]]]); _started[index] = true; }
        var offset = 0;
        foreach (var transition in node.Transitions)
        {
            var matches = (transition.From < 0 || transition.From == _states[index]) && transition.To != _states[index];
            if (transition.ExitTime >= 0) matches &= Progress(inputs[_states[index]]) >= transition.ExitTime;
            foreach (var condition in transition.Conditions)
            {
                var value = _parameters[_graph.ConditionParameters[index][offset++]];
                matches &= condition.Comparison switch
                {
                    AnimationComparison.Equal => value == condition.Value,
                    AnimationComparison.NotEqual => value != condition.Value,
                    AnimationComparison.Less => value < condition.Value,
                    AnimationComparison.LessOrEqual => value <= condition.Value,
                    AnimationComparison.Greater => value > condition.Value,
                    AnimationComparison.GreaterOrEqual => value >= condition.Value,
                    _ => false
                };
            }
            if (!matches) continue;
            _transitionPoses[index].CopyFrom(_poses[index]);
            _states[index] = transition.To; _blendTimes[index] = transition.Duration;
            _transitionReasons[index] = transition.Conditions.Count > 0 ? transition.Conditions[0].Parameter : "Clip progress";
            // Capture the currently blended pose on interruption; old states no longer emit events.
            _previousTimes[index] = transition.Duration;
            break;
        }
        EvaluateNode(inputs[_states[index]]);
        _blendTimes[index] = Math.Max(0, _blendTimes[index] - _delta);
        if (_blendTimes[index] > 0 && _previousTimes[index] > 0)
            Pose.Blend(_transitionPoses[index], _poses[inputs[_states[index]]], 1 - _blendTimes[index] / _previousTimes[index], _poses[index]);
        else _poses[index].CopyFrom(_poses[inputs[_states[index]]]);
    }
    private float Progress(int index)
    {
        if (_graph.Clips[index] is { } clip) return clip.Clip.Duration > 0 ? _times[index] / clip.Clip.Duration : 0;
        var inputs = _graph.Inputs[index];
        if (inputs.Length == 0) return 0;
        return _graph.Nodes[index].Kind switch
        {
            AnimationNodeKind.StateMachine => Progress(inputs[_states[index]]),
            AnimationNodeKind.Select => Progress(inputs[Math.Clamp((int)Value(index), 0, inputs.Length - 1)]),
            AnimationNodeKind.Blend or AnimationNodeKind.MaskedBlend => Value(index) >= 1 ? Progress(inputs[1]) : Progress(inputs[0]),
            AnimationNodeKind.Locomotion1D => _syncTimes[index],
            _ => Progress(inputs[0])
        };
    }
    private float NormalizedTime(int index)
    {
        var duration = _graph.Clips[index]!.Clip.Duration;
        if (duration <= 0) return 0;
        return _graph.Nodes[index].Loop ? Wrap(_times[index], duration) / duration : Math.Clamp(_times[index] / duration, 0, 1);
    }
    private void Sample(int index, bool wasActive)
    {
        var node = _graph.Nodes[index]; var bound = _graph.Clips[index]!; var clip = bound.Clip;
        var pose = _poses[index];
        var sequence = _graph.SequenceParameters[index] >= 0 ? _integers[_graph.SequenceParameters[index]] : 0;
        var activation = !_started[index] || (!wasActive && node.RestartOnActivation) || sequence != _sequences[index];
        var previous = _times[index];
        if (_graph.TimeParameters[index] >= 0)
            _times[index] = Math.Clamp(_parameters[_graph.TimeParameters[index]], 0, 1) * clip.Duration;
        else if (!float.IsNaN(_syncTimes[index])) _times[index] = _syncTimes[index];
        else
        {
            if (activation && _started[index]) _times[index] = node.StartTime;
            _times[index] += _delta * node.Speed * bound.PlaybackSpeed * (_graph.Parameters[index] >= 0 ? Value(index) : 1);
        }
        _previousTimes[index] = activation ? -float.Epsilon : previous;
        if (activation && !float.IsNaN(_syncTimes[index])) _previousTimes[index] = _times[index] - _delta * node.Speed;
        _sequences[index] = sequence; _started[index] = true;
        var time = node.Loop && clip.Duration > 0 ? Wrap(_times[index], clip.Duration) : Math.Clamp(_times[index], 0, clip.Duration);
        // Clock/activation bookkeeping above always runs, even for a held sample.
        // Marker intervals are collected separately after contribution weights resolve.
        if (_sampledTimes[index] == time && _sampledDefaults[index] == _defaultsRevision) return;
        _sampledTimes[index] = time; _sampledDefaults[index] = _defaultsRevision;
        pose.CopyFrom(_defaults);
        SampledClipCount++;
        if (bound.SharedTransformTimes is { } sharedTimes && CurveKeys.TryFindSegment(sharedTimes, time, ref _sharedCursors[index], out var keyA, out var keyB, out var blend))
        {
            for (var i = 0; i < clip.TransformTracks.Length; i++)
            {
                var track = clip.TransformTracks[i]; var bone = bound.BoneSlots[i]; var slot = bound.TransformSlots[i];
                var value = new BoneTransform(bound.ConstantPositions[i] ?? track.Position.EvaluateSegment(keyA, keyB, blend),
                    track.Rotation.EvaluateSegment(keyA, keyB, blend), bound.ConstantScales[i] ?? track.Scale.EvaluateSegment(keyA, keyB, blend));
                if (bone >= 0) pose.Bones[bone] = value; else pose.Transforms[slot] = value;
            }
        }
        else for (var i = 0; i < clip.TransformTracks.Length; i++)
        {
            var track = clip.TransformTracks[i]; var bone = bound.BoneSlots[i]; var slot = bound.TransformSlots[i];
            var rest = bone >= 0 ? _defaults.Bones[bone] : _defaults.Transforms[slot];
            var value = new BoneTransform(bound.ConstantPositions[i] ?? track.Position.Evaluate(time, ref _positionCursors[index][i], rest.Position),
                track.Rotation.Evaluate(time, ref _rotationCursors[index][i], rest.Rotation), bound.ConstantScales[i] ?? track.Scale.Evaluate(time, ref _scaleCursors[index][i], rest.Scale));
            if (bone >= 0) pose.Bones[bone] = value; else pose.Transforms[slot] = value;
        }
        for (var i = 0; i < bound.PropertySlots.Length; i++)
        {
            var slot = bound.PropertySlots[i];
            pose.Values[slot] = clip.PropertyTracks[i].Curve.Evaluate(time, ref _propertyCursors[index][i], _defaults.Values[slot]);
        }
        for (var i = 0; i < bound.Curves.Length; i++)
            pose.Values[_graph.PropertyBindings.Length + bound.CurveSlots[i]] = bound.Curves[i].Evaluate(time, ref _curveCursors[index][i], 0);
    }
    private void AccumulateWeights(int index, float weight)
    {
        if (weight <= 0) return;
        _weights[index] += weight;
        var inputs = _graph.Inputs[index];
        switch (_graph.Nodes[index].Kind)
        {
            case AnimationNodeKind.Blend:
            case AnimationNodeKind.MaskedBlend:
                var alpha = Math.Clamp(Value(index), 0, 1);
                var baseWeight = 1 - alpha;
                if (_graph.Nodes[index].Kind == AnimationNodeKind.MaskedBlend)
                    baseWeight = 1 - alpha * _graph.MinimumMaskWeights[index];
                AccumulateWeights(inputs[0], weight * baseWeight); AccumulateWeights(inputs[1], weight * alpha); break;
            case AnimationNodeKind.Select:
                AccumulateWeights(inputs[Math.Clamp((int)Value(index), 0, inputs.Length - 1)], weight); break;
            case AnimationNodeKind.StateMachine:
                AccumulateWeights(inputs[_states[index]], weight); break;
            case AnimationNodeKind.Locomotion1D:
                Bracket(index, Value(index), out var a, out var b, out var blend);
                AccumulateWeights(inputs[a], weight * (1 - blend)); AccumulateWeights(inputs[b], weight * blend); break;
            default:
                foreach (var input in inputs) AccumulateWeights(input, weight);
                break;
        }
    }
    private void CollectMarkers(int index, BoundAnimationClip bound)
    {
        var previous = _previousTimes[index]; var current = _times[index];
        if (current < previous) return; // A backwards external time change is a seek.
        var duration = bound.Clip.Duration;
        var looping = _graph.Nodes[index].Loop && duration > 0;
        var first = looping ? Math.Max(0, (int)MathF.Floor(previous / duration)) : 0;
        var last = looping ? Math.Max(0, (int)MathF.Floor(current / duration)) : 0;
        for (var cycle = first; cycle <= last; cycle++)
            foreach (var marker in bound.Markers)
            {
                var absoluteTime = marker.Time + (looping ? cycle * duration : 0);
                if (absoluteTime > previous && absoluteTime <= current)
                    _markers.Add(new(marker.Name, _graph.Nodes[index].Id, _sequences[index], marker.Time));
            }
    }
    private static float Wrap(float time, float duration) { var result = time % duration; return result < 0 ? result + duration : result; }
    private static bool Same(Pose a, Pose b)
    {
        for (var i = 0; i < a.Bones.Length; i++) if (!Equal(a.Bones[i], b.Bones[i])) return false;
        for (var i = 0; i < a.Transforms.Length; i++) if (!Equal(a.Transforms[i], b.Transforms[i])) return false;
        return a.Values.AsSpan().SequenceEqual(b.Values);
    }
    private static bool Equal(in BoneTransform a, in BoneTransform b) => a.Position == b.Position && a.Rotation == b.Rotation && a.Scale == b.Scale;
}
