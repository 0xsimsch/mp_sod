using System;

namespace SodCoop.Net;

/// <summary>Who a packet came from / goes to. SteamID64 for Steam, a synthetic id for UDP.</summary>
public readonly record struct PeerId(ulong Value)
{
    public override string ToString() => $"peer:{Value}";
}

/// <summary>
/// Packet transport. Implementations: <see cref="SteamTransport"/> (real sessions) and
/// <see cref="UdpTransport"/> (same machine / LAN testing). Everything is polled from the game's
/// main thread; no callbacks.
/// </summary>
public interface ITransport : IDisposable
{
    string Name { get; }

    /// <summary>False when the transport refused the packet (e.g. Steam's send buffer is full); the caller may retry later.</summary>
    bool Send(PeerId to, byte[] data, bool reliable);

    /// <summary>Reliable bytes queued for <paramref name="peer"/> but not yet sent; bulk senders pace on this.</summary>
    int PendingReliableBytes(PeerId peer) => 0;

    bool TryReceive(out PeerId from, out byte[] data);

    /// <summary>Called once per frame (lobby polling etc.).</summary>
    void Tick();
}
