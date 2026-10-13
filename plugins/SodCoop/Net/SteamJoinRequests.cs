using System;
using System.Runtime.InteropServices;
using Steamworks;

namespace SodCoop.Net;

/// <summary>
/// Steam's "Join game" (friends list / overlay) and accepted lobby invites while the game is already running.
/// Steam reports those only as callbacks (<c>GameRichPresenceJoinRequested_t</c> 337, <c>GameLobbyJoinRequested_t</c> 333),
/// and the game's Steamworks.NET (manual dispatch, <c>CallbackDispatcher.RunFrame</c>) drops callbacks nobody registered.
///
/// <c>Callback&lt;T&gt;</c> for those two structs was never compiled into the IL2CPP build, so this borrows an instantiation
/// the game does use (<c>Callback&lt;GamepadTextInputDismissed_t&gt;</c>) and also files it under ids 333 and 337 in the
/// dispatcher's table. Its typed parameter is then meaningless; the handler reads the raw callback from
/// <c>CallbackDispatcher.m_pCallbackMsg</c> (CallbackMsg_t: int user @0, int id @4, void* param @8), which RunFrame
/// fills before calling it.
/// </summary>
static class SteamJoinRequests
{
    const int LobbyJoinRequested = 333;        // GameLobbyJoinRequested_t: CSteamID lobby @0, CSteamID friend @8
    const int RichPresenceJoinRequested = 337; // GameRichPresenceJoinRequested_t: CSteamID friend @0, char connect[256] @8

    static Callback<GamepadTextInputDismissed_t> _callback;
    static bool _failed;

    /// <summary>Per frame: register once Steamworks.NET's dispatcher is up.</summary>
    public static void EnsureRegistered()
    {
        if (_callback != null || _failed || !CallbackDispatcher.IsInitialized) return;
        try
        {
            _callback = Callback<GamepadTextInputDismissed_t>.Create((Action<GamepadTextInputDismissed_t>)OnCallback);
            var table = CallbackDispatcher.m_registeredCallbacks;
            foreach (var id in new[] { LobbyJoinRequested, RichPresenceJoinRequested })
            {
                if (!table.ContainsKey(id)) table[id] = new Il2CppSystem.Collections.Generic.List<Callback>();
                table[id].Add(_callback);
            }
            Plugin.Event("listening for Steam \"Join game\" / invite requests while running");
        }
        catch (Exception e)
        {
            _failed = true;
            Plugin.Event($"could not hook Steam join requests ({e.Message}); in a running game, join with F11 instead");
        }
    }

    /// <summary>
    /// Test (Ctrl+Home): fake a "Join game" for <paramref name="lobby"/> the way RunFrame delivers it, through the
    /// dispatcher's table entry for 337 and <c>m_pCallbackMsg</c>.
    /// </summary>
    public static void SelfTest(ulong lobby)
    {
        if (_callback == null) { Plugin.Event("Ctrl+Home: join-request hook not registered"); return; }
        var table = CallbackDispatcher.m_registeredCallbacks;
        if (!table.ContainsKey(RichPresenceJoinRequested) || table[RichPresenceJoinRequested].Count == 0)
        {
            Plugin.Event("Ctrl+Home: no callback filed under 337");
            return;
        }
        var msg = CallbackDispatcher.m_pCallbackMsg;
        if (msg == IntPtr.Zero) { Plugin.Event("Ctrl+Home: dispatcher has no callback buffer"); return; }
        var param = Marshal.AllocHGlobal(8 + 256);
        try
        {
            for (int i = 0; i < 8 + 256; i++) Marshal.WriteByte(param, i, 0);
            Marshal.WriteInt64(param, 0, 76561197960287930L);
            var text = System.Text.Encoding.ASCII.GetBytes($"{SteamLobby.RichPresenceArg} {lobby}");
            Marshal.Copy(text, 0, param + 8, text.Length);
            Marshal.WriteInt32(msg, 4, RichPresenceJoinRequested);
            Marshal.WriteIntPtr(msg, 8, param);
            table[RichPresenceJoinRequested][0].OnRunCallback(param);
        }
        finally
        {
            Marshal.WriteIntPtr(msg, 8, IntPtr.Zero);
            Marshal.FreeHGlobal(param);
        }
    }

    static void OnCallback(GamepadTextInputDismissed_t _)
    {
        try
        {
            var msg = CallbackDispatcher.m_pCallbackMsg;
            if (msg == IntPtr.Zero) return;
            int id = Marshal.ReadInt32(msg, 4);
            var param = Marshal.ReadIntPtr(msg, 8);
            if (param == IntPtr.Zero) return;

            ulong lobby;
            string source;
            if (id == RichPresenceJoinRequested)
            {
                string connect = Marshal.PtrToStringAnsi(param + 8) ?? "";
                lobby = SteamLobby.ParseLobbyArg(connect);
                source = $"\"Join game\" on friend {(ulong)Marshal.ReadInt64(param, 0)} (connect \"{connect}\")";
            }
            else if (id == LobbyJoinRequested)
            {
                lobby = (ulong)Marshal.ReadInt64(param, 0);
                source = $"lobby invite/join via friend {(ulong)Marshal.ReadInt64(param, 8)}";
            }
            else return; // the real gamepad-text callback we borrowed; the game has its own handler for it

            Plugin.Event($"STEAM {source} → lobby {lobby}");
            if (lobby == 0) { Plugin.Event("STEAM join request without a lobby id; ignored"); return; }
            var current = NetSession.Current;
            if (current != null)
            {
                if (current.Role == NetSession.SessionRole.Client && current.LobbyId == lobby) return;
                Plugin.Event("STEAM already in a session; leave it first (Ctrl+Delete), then accept again or press F11");
                return;
            }
            NetSession.JoinSteam(lobby);
        }
        catch (Exception e) { Plugin.Event($"STEAM join request failed: {e}"); }
    }
}
