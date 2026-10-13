using System.Collections.Generic;
using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// Poses received over the network, played back <see cref="Delay"/> seconds late and interpolated,
/// so jitter and the odd lost (unreliable) packet don't show.
/// Sender timestamps are mapped onto the local clock with the smallest observed offset, which tracks
/// the fastest packet and so absorbs one-way latency spikes.
/// </summary>
public sealed class NetworkPoseSource : IPoseSource
{
    public const float Delay = 0.1f;
    const int MaxBuffered = 64;

    readonly List<PlayerPose> _buffer = new(); // sorted by Time (sender clock)
    float _offset = float.MaxValue;            // local − sender
    PlayerPose _last;
    bool _hasLast;

    public float LastReceived { get; private set; }

    public void Push(PlayerPose pose, float localNow)
    {
        LastReceived = localNow;
        float offset = localNow - pose.Time;
        // Re-anchor if the sender restarted its clock or we drifted far.
        if (offset < _offset || offset > _offset + 1f) _offset = offset;

        int i = _buffer.Count;
        while (i > 0 && _buffer[i - 1].Time > pose.Time) i--;
        _buffer.Insert(i, pose);
        if (_buffer.Count > MaxBuffered) _buffer.RemoveAt(0);
    }

    public bool TryGetPose(float now, out PlayerPose pose)
    {
        if (_buffer.Count == 0) { pose = _last; return _hasLast; }
        float t = now - Delay - _offset; // render time on the sender's clock

        while (_buffer.Count >= 2 && _buffer[1].Time <= t) _buffer.RemoveAt(0);
        var a = _buffer[0];
        if (_buffer.Count == 1 || t <= a.Time)
        {
            pose = a; // nothing newer yet: hold (no extrapolation)
        }
        else
        {
            var b = _buffer[1];
            float k = Mathf.InverseLerp(a.Time, b.Time, t);
            pose = b;
            pose.Position = Vector3.Lerp(a.Position, b.Position, k);
            pose.Yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, k);
        }
        _last = pose;
        _hasLast = true;
        return true;
    }
}
