using System;
using System.Collections.Generic;
using SodCoop.Core;

namespace SodCoop.Net;

/// <summary>
/// In-game messages about the partner ("Hunter99 found new evidence", "… was knocked out", "case solved by …"),
/// shown with the game's own message feed (<c>InterfaceController.NewGameMessage</c>, type notification).
/// Local events call <see cref="Show"/> on the receiving machine directly from existing receive paths; free-form
/// notices from another machine travel as MsgType 42 (host relays).
/// </summary>
public static class Notify
{
    const byte KindText = 1;

    /// <summary>Same message more than once within this many seconds is shown once (bursts of evidence events).</summary>
    const float RepeatWindow = 4f;
    static readonly Dictionary<string, float> _lastShown = new();

    public static string NameOf(byte playerId)
    {
        if (PlayerRegistry.TryGet(new PlayerId(playerId), out var p) && !string.IsNullOrEmpty(p.DisplayName)) return p.DisplayName;
        return playerId == 0 ? "The host" : $"Player {playerId}";
    }

    public static void Show(string text, InterfaceControls.Icon icon = InterfaceControls.Icon.agent)
    {
        if (string.IsNullOrEmpty(text) || !World.Ready) return;
        float now = UnityEngine.Time.unscaledTime;
        if (_lastShown.TryGetValue(text, out var at) && now - at < RepeatWindow) return;
        _lastShown[text] = now;
        try
        {
            InterfaceController.Instance?.NewGameMessage(InterfaceController.GameMessageType.notification, 0, text, icon,
                null, false, default, -1, 0f, null, GameMessageController.PingOnComplete.none, null, null, null);
            Plugin.Event($"NOTIFY {text}");
        }
        catch (Exception e) { Plugin.Event($"NOTIFY failed ({text}): {e.Message}"); }
    }

    /// <summary>A partner event seen by another player (e.g. "case solved by …"): shown on every other machine.</summary>
    public static void Broadcast(string text, InterfaceControls.Icon icon = InterfaceControls.Icon.agent)
    {
        var s = NetSession.Current;
        if (s == null || PlayerRegistry.Local == null) return;
        s.SendExtra(ExtraWire.Write(ExtraWire.Notice, KindText, w =>
        {
            w.Write(PlayerRegistry.Local.Id.Value);
            ExtraWire.Str(w, text);
            w.Write((byte)icon);
        }));
    }

    internal static void OnPacket(NetSession s, PeerId from, PlayerId? fromPlayer, byte[] data)
    {
        using var r = ExtraWire.Read(data, out var kind);
        if (kind != KindText) return;
        byte by = r.ReadByte();
        string text = ExtraWire.Str(r);
        var icon = (InterfaceControls.Icon)r.ReadByte();
        if (s.Role == NetSession.SessionRole.Host)
        {
            if (fromPlayer == null || fromPlayer.Value.Value != by) return; // a client speaks only for itself
            s.SendExtraExcept(data, from);
        }
        Show(text, icon);
    }
}
