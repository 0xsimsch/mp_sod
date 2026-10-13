using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SodCoop.Net;

/// <summary>
/// Steam P2P via ISteamNetworkingMessages (NAT traversal and relay handled by Steam).
/// PeerId = SteamID64. Channel 0 for everything.
/// </summary>
public sealed class SteamTransport : ITransport
{
    const int Channel = 0;
    readonly IntPtr _msgs;
    readonly IntPtr[] _batch = new IntPtr[32];
    readonly Queue<(PeerId, byte[])> _inbox = new();

    public PeerId Self { get; }
    public string Name => $"steam:{Self.Value}";

    public SteamTransport()
    {
        _msgs = SteamNative.SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        if (_msgs == IntPtr.Zero) throw new InvalidOperationException("ISteamNetworkingMessages unavailable (Steam not initialised?)");
        Self = new PeerId(SteamNative.SteamAPI_ISteamUser_GetSteamID(SteamNative.SteamAPI_SteamUser_v023()));
        // Start fetching the relay network config now instead of on the first send (saves seconds on connect).
        _utils = SteamNative.SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
        if (_utils != IntPtr.Zero)
        {
            SteamNative.SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(_utils);
            RaiseRateLimits();
        }
    }

    // Steam's defaults (logged below) start every session slow and cap queued data at 512 KB: the first two-machine
    // join moved 8 MB at ~250 KB/s. Raise them before any session exists (the values are copied into new sessions).
    // The minimum is the rate Steam starts at and won't drop below even when congested, so it stays moderate.
    const int WantSendRateMin = 1024 * 1024;        // 1 MB/s
    const int WantSendRateMax = 16 * 1024 * 1024;   // 16 MB/s: effectively "as fast as the link allows"
    const int WantSendBuffer = 8 * 1024 * 1024;     // 8 MB queued before Steam refuses a send

    void RaiseRateLimits()
    {
        string Show() => $"send rate min {Get(SteamNative.ConfigSendRateMin) / 1024} KB/s, max {Get(SteamNative.ConfigSendRateMax) / 1024} KB/s, " +
                         $"send buffer {Get(SteamNative.ConfigSendBufferSize) / 1024} KB";
        try
        {
            string before = Show();
            if (Environment.GetEnvironmentVariable("SODCOOP_NO_RATE") == null && System.IO.File.Exists(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "sodcoop_no_rate"))) { Plugin.Event($"NET steam limits left as they are (test): {before}"); return; }
            bool ok = SteamNative.SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(_utils, SteamNative.ConfigSendBufferSize, WantSendBuffer)
                    & SteamNative.SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(_utils, SteamNative.ConfigSendRateMax, WantSendRateMax)
                    & SteamNative.SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(_utils, SteamNative.ConfigSendRateMin, WantSendRateMin);
            Plugin.Event($"NET steam limits: {before} -> {Show()}{(ok ? "" : " (some values were refused)")}");
        }
        catch (Exception e) { Plugin.Event($"NET steam limits unchanged: {e.Message}"); }
    }

    int Get(int value)
    {
        int type = 0, result = -1;
        var size = (UIntPtr)4;
        int r = SteamNative.SteamAPI_ISteamNetworkingUtils_GetConfigValue(_utils, value, 1, IntPtr.Zero, ref type, ref result, ref size);
        return r > 0 ? result : -1;
    }

    readonly IntPtr _utils;

    /// <summary>Steam Datagram Relay availability: 100 = ready, 1..99 = still fetching/trying, negative = failed.</summary>
    public int RelayStatus => _utils == IntPtr.Zero ? -999 : SteamNative.SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus(_utils, IntPtr.Zero);

    /// <summary>
    /// One-line state of the Steam session with <paramref name="peer"/>: state, ping, relayed or direct, and why
    /// it ended if it did. <paramref name="state"/> is the raw ESteamNetworkingConnectionState.
    /// </summary>
    public string Describe(PeerId peer, out int state)
    {
        var id = SteamNetworkingIdentity.FromSteamId(peer.Value);
        var info = Marshal.AllocHGlobal(1024);
        var quick = Marshal.AllocHGlobal(256);
        try
        {
            for (int i = 0; i < 1024; i++) Marshal.WriteByte(info, i, 0);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(quick, i, 0);
            state = SteamNative.SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(_msgs, ref id, info, quick);
            if (state == 0) return "no session";
            int ping = Marshal.ReadInt32(quick, SteamConnectionLayout.StatusPing);
            float quality = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(quick, SteamConnectionLayout.StatusQualityLocal));
            int flags = Marshal.ReadInt32(info, SteamConnectionLayout.InfoFlags);
            int endReason = Marshal.ReadInt32(info, SteamConnectionLayout.InfoEndReason);
            string endDebug = Marshal.PtrToStringAnsi(info + SteamConnectionLayout.InfoEndDebug) ?? "";
            string desc = Marshal.PtrToStringAnsi(info + SteamConnectionLayout.InfoDescription) ?? "";
            return $"{SteamConnectionLayout.StateName(state)} ping={ping}ms quality={quality:F2} " +
                   ((flags & SteamConnectionLayout.FlagRelayed) != 0 ? "relayed" : "direct") +
                   (endReason != 0 ? $" end={endReason} \"{endDebug}\"" : "") + $" [{desc}]";
        }
        finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(quick); }
    }

    const int ResultOk = 1, ResultLimitExceeded = 25;
    readonly HashSet<int> _reportedResults = new();

    /// <summary>
    /// False if Steam refused the message. Reliable messages are refused with LimitExceeded once the session's
    /// send buffer (512 KB) is full: the relay sends ~256 KB/s, so a fast bulk sender must pace itself
    /// (<see cref="PendingReliableBytes"/>) or retry.
    /// </summary>
    public bool Send(PeerId to, byte[] data, bool reliable)
    {
        var id = SteamNetworkingIdentity.FromSteamId(to.Value);
        int flags = SteamNative.SendAutoRestartBrokenSession | (reliable ? SteamNative.SendReliable : SteamNative.SendUnreliable | SteamNative.SendNoNagle);
        int result = SteamNative.SteamAPI_ISteamNetworkingMessages_SendMessageToUser(_msgs, ref id, data, (uint)data.Length, flags, Channel);
        if (result == ResultOk) return true;
        if (_reportedResults.Add(result))
            Plugin.Event($"NET steam send to {to} refused: EResult {result}" + (result == ResultLimitExceeded ? " (send buffer full)" : "") + " (reported once per code)");
        return false;
    }

    public int PendingReliableBytes(PeerId peer)
    {
        var id = SteamNetworkingIdentity.FromSteamId(peer.Value);
        var quick = Marshal.AllocHGlobal(SteamConnectionLayout.StatusSize);
        try
        {
            for (int i = 0; i < SteamConnectionLayout.StatusSize; i++) Marshal.WriteByte(quick, i, 0);
            if (SteamNative.SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(_msgs, ref id, IntPtr.Zero, quick) == 0) return 0;
            return Marshal.ReadInt32(quick, SteamConnectionLayout.StatusPendingReliable)
                 + Marshal.ReadInt32(quick, SteamConnectionLayout.StatusSentUnackedReliable);
        }
        finally { Marshal.FreeHGlobal(quick); }
    }

    /// <summary>Accept a pending session request from <paramref name="peer"/> (harmless if none is pending).</summary>
    public bool Accept(PeerId peer)
    {
        var id = SteamNetworkingIdentity.FromSteamId(peer.Value);
        return SteamNative.SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(_msgs, ref id);
    }

    public void Close(PeerId peer)
    {
        var id = SteamNetworkingIdentity.FromSteamId(peer.Value);
        SteamNative.SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(_msgs, ref id);
    }

    public bool TryReceive(out PeerId from, out byte[] data)
    {
        if (_inbox.Count == 0) Pump();
        if (_inbox.Count == 0) { from = default; data = null; return false; }
        (from, data) = _inbox.Dequeue();
        return true;
    }

    void Pump()
    {
        int n = SteamNative.SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(_msgs, Channel, _batch, _batch.Length);
        for (int i = 0; i < n; i++)
        {
            var m = _batch[i];
            try
            {
                var ptr = Marshal.ReadIntPtr(m, SteamMessageLayout.Data);
                int size = Marshal.ReadInt32(m, SteamMessageLayout.Size);
                ulong peer = (ulong)Marshal.ReadInt64(m, SteamMessageLayout.PeerSteamId);
                var buf = new byte[size];
                Marshal.Copy(ptr, buf, 0, size);
                _inbox.Enqueue((new PeerId(peer), buf));
            }
            finally { SteamNative.SteamAPI_SteamNetworkingMessage_t_Release(m); }
        }
    }

    public void Tick() { }

    public void Dispose() { }
}
