using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace SodCoop.Net;

/// <summary>
/// Plain UDP, for testing on one machine or a LAN (and for analysis/tools/fake_client.py).
/// "Reliable" isn't reliable here: the session layer resends what matters (Hello).
/// </summary>
public sealed class UdpTransport : ITransport
{
    readonly UdpClient _udp;
    readonly Dictionary<PeerId, IPEndPoint> _byId = new();
    readonly Dictionary<IPEndPoint, PeerId> _byEndpoint = new();

    public string Name => $"udp:{((IPEndPoint)_udp.Client.LocalEndPoint).Port}";

    /// <param name="port">Local port; 0 picks a free one (clients).</param>
    public UdpTransport(int port)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _udp.Client.ReceiveBufferSize = 4 << 20; // snapshot chunks arrive in bursts
        // Windows reports ICMP "port unreachable" as a socket error on the next receive; ignore it.
        try { _udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); }
        catch (Exception) { /* not supported on this platform */ }
    }

    /// <summary>Register a peer we want to talk to first (the host, for a client).</summary>
    public PeerId AddPeer(IPEndPoint ep) => IdFor(ep);

    public static IPEndPoint Parse(string hostPort)
    {
        var parts = hostPort.Split(':');
        var addrs = Dns.GetHostAddresses(parts[0]);
        var addr = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0]; // socket is IPv4
        return new IPEndPoint(addr, parts.Length > 1 ? int.Parse(parts[1]) : 47615);
    }

    public bool Send(PeerId to, byte[] data, bool reliable)
    {
        if (!_byId.TryGetValue(to, out var ep)) return true;
        try { _udp.Send(data, data.Length, ep); }
        catch (SocketException) { /* unreachable peer: the session's timeout handles it */ }
        return true;
    }

    public bool TryReceive(out PeerId from, out byte[] data)
    {
        from = default;
        data = null;
        while (_udp.Available > 0)
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            try { data = _udp.Receive(ref ep); }
            catch (SocketException) { continue; }
            from = IdFor(ep);
            return true;
        }
        return false;
    }

    public void Tick() { }

    PeerId IdFor(IPEndPoint ep)
    {
        if (_byEndpoint.TryGetValue(ep, out var id)) return id;
        id = new PeerId(((ulong)BitConverter.ToUInt32(ep.Address.GetAddressBytes(), 0) << 16) | (uint)ep.Port);
        _byEndpoint[ep] = id;
        _byId[id] = ep;
        return id;
    }

    public void Dispose() => _udp.Dispose();
}
