using System.Collections.Generic;
using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// Fakes a second player without networking: records the local player's movement and replays
/// it after a delay, shifted sideways so the ghost doesn't stand inside the player.
/// </summary>
public sealed class GhostReplaySource : IPoseSource
{
    readonly Queue<PlayerPose> _history = new();
    readonly float _delay;
    readonly Vector3 _offset;

    public GhostReplaySource(float delaySeconds, Vector3 offset)
    {
        _delay = delaySeconds;
        _offset = offset;
    }

    PlayerPose _current;
    bool _hasCurrent;

    public void Record(PlayerPose pose) => _history.Enqueue(pose);

    /// <summary>Always returns the latest replayed pose, so the body stays pinned between samples.</summary>
    public bool TryGetPose(float now, out PlayerPose pose)
    {
        while (_history.Count > 0 && _history.Peek().Time <= now - _delay)
        {
            _current = _history.Dequeue();
            _current.Position += _offset;
            _hasCurrent = true;
        }
        pose = _current;
        return _hasCurrent;
    }
}
