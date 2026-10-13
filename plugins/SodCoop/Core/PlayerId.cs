using System;

namespace SodCoop.Core;

/// <summary>Session-local player id. 0 is always the host.</summary>
public readonly struct PlayerId : IEquatable<PlayerId>
{
    public static readonly PlayerId Host = new(0);

    public readonly byte Value;

    public PlayerId(byte value) => Value = value;

    public bool IsHost => Value == 0;

    public bool Equals(PlayerId other) => Value == other.Value;
    public override bool Equals(object obj) => obj is PlayerId other && Equals(other);
    public override int GetHashCode() => Value;
    public override string ToString() => $"P{Value}";

    public static bool operator ==(PlayerId a, PlayerId b) => a.Value == b.Value;
    public static bool operator !=(PlayerId a, PlayerId b) => a.Value != b.Value;
}
