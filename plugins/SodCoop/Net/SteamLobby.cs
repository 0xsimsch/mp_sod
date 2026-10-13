using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SodCoop.Net;

/// <summary>
/// Friends-only Steam lobby used to find each other. The host creates it; clients join by lobby id
/// (from the log, an accepted invite, which launches the game with <c>+connect_lobby &lt;id&gt;</c>,
/// Steam's "Join game", which launches it with <c>+mp_sod_lobby &lt;id&gt;</c> via rich presence),
/// or with id 0 by looking for a friend who is hosting (<see cref="FindAndJoin"/>).
/// API call results are polled, no callbacks: a running game never hears about invites accepted in the
/// overlay (that is a callback), which is why the friend search exists.
/// </summary>
public sealed class SteamLobby : IDisposable
{
    public const string RichPresenceArg = "+mp_sod_lobby";
    /// <summary>What Steam passes when a lobby invite is accepted while the game isn't running.</summary>
    public const string InviteArg = "+connect_lobby";
    const float FriendSearchSeconds = 20f;

    readonly IntPtr _mm = SteamNative.SteamAPI_SteamMatchmaking_v009();
    readonly IntPtr _utils = SteamNative.SteamAPI_SteamUtils_v010();
    readonly IntPtr _friends = SteamNative.SteamAPI_SteamFriends_v017();
    ulong _pendingCall;
    int _pendingCallback;
    float _nextMemberPoll;
    readonly HashSet<ulong> _members = new();
    float _searchUntil = -1f, _nextSearch;

    public ulong LobbyId { get; private set; }
    public ulong Self { get; } = SteamNative.SteamAPI_ISteamUser_GetSteamID(SteamNative.SteamAPI_SteamUser_v023());
    public bool IsHost { get; private set; }

    /// <summary>Host: a Steam user entered the lobby.</summary>
    public event Action<PeerId> MemberJoined;
    /// <summary>Host: a Steam user left the lobby.</summary>
    public event Action<PeerId> MemberLeft;
    /// <summary>Client: lobby entered; argument is the lobby owner (the host).</summary>
    public event Action<PeerId> Entered;
    public event Action<string> Failed;

    public void Create(int maxMembers)
    {
        IsHost = true;
        _pendingCall = SteamNative.SteamAPI_ISteamMatchmaking_CreateLobby(_mm, SteamNative.LobbyTypeFriendsOnly, maxMembers);
        _pendingCallback = SteamNative.CallbackLobbyCreated;
    }

    /// <summary>
    /// Join the lobby of a friend who is hosting (playing this game with a lobby). Rich presence of friends
    /// arrives asynchronously, so retries for <see cref="FriendSearchSeconds"/>.
    /// </summary>
    public void FindAndJoin()
    {
        IsHost = false;
        _searchUntil = -2f; // armed; set to a real deadline on the first Tick (needs the clock)
    }

    public void Join(ulong lobbyId)
    {
        IsHost = false;
        _pendingCall = SteamNative.SteamAPI_ISteamMatchmaking_JoinLobby(_mm, lobbyId);
        _pendingCallback = SteamNative.CallbackLobbyEnter;
    }

    public void OpenInviteDialog()
    {
        if (LobbyId != 0) SteamNative.SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(_friends, LobbyId);
    }

    public string FriendName(PeerId peer) => SteamNative.Utf8(SteamNative.SteamAPI_ISteamFriends_GetFriendPersonaName(_friends, peer.Value));

    public void Tick(float now)
    {
        if (_searchUntil == -2f) { _searchUntil = now + FriendSearchSeconds; _nextSearch = now; }
        if (_searchUntil > 0 && now >= _nextSearch) SearchFriends(now);
        if (_pendingCall != 0) PollCall();
        if (LobbyId != 0 && IsHost && now >= _nextMemberPoll)
        {
            _nextMemberPoll = now + 0.5f;
            PollMembers();
        }
    }

    void PollCall()
    {
        if (!SteamNative.SteamAPI_ISteamUtils_IsAPICallCompleted(_utils, _pendingCall, out var failed)) return;
        var call = _pendingCall;
        _pendingCall = 0;
        if (failed) { Failed?.Invoke("Steam API call failed"); return; }

        var buf = Marshal.AllocHGlobal(32);
        try
        {
            if (!SteamNative.SteamAPI_ISteamUtils_GetAPICallResult(_utils, call, buf, _pendingCallback == SteamNative.CallbackLobbyCreated ? 16 : 24, _pendingCallback, out failed) || failed)
            {
                Failed?.Invoke("could not read Steam API call result");
                return;
            }
            if (_pendingCallback == SteamNative.CallbackLobbyCreated)
            {
                int result = Marshal.ReadInt32(buf, 0);           // EResult, 1 = OK
                ulong lobby = (ulong)Marshal.ReadInt64(buf, 8);
                if (result != 1) { Failed?.Invoke($"CreateLobby failed (EResult {result})"); return; }
                LobbyId = lobby;
                SteamNative.SteamAPI_ISteamMatchmaking_SetLobbyData(_mm, lobby, "mp_sod", Protocol.Version.ToString());
                SteamNative.SteamAPI_ISteamFriends_SetRichPresence(_friends, "connect", $"{RichPresenceArg} {lobby}");
                _members.Add(Self);
            }
            else
            {
                ulong lobby = (ulong)Marshal.ReadInt64(buf, 0);
                uint response = (uint)Marshal.ReadInt32(buf, 16); // EChatRoomEnterResponse, 1 = success
                if (response != 1) { Failed?.Invoke($"JoinLobby failed (response {response})"); return; }
                LobbyId = lobby;
                Entered?.Invoke(new PeerId(SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyOwner(_mm, lobby)));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    void PollMembers()
    {
        var now = new HashSet<ulong>();
        int n = SteamNative.SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(_mm, LobbyId);
        for (int i = 0; i < n; i++) now.Add(SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(_mm, LobbyId, i));
        foreach (var m in now) if (_members.Add(m) && m != Self) MemberJoined?.Invoke(new PeerId(m));
        _members.RemoveWhere(m =>
        {
            if (now.Contains(m)) return false;
            MemberLeft?.Invoke(new PeerId(m));
            return true;
        });
    }

    void SearchFriends(float now)
    {
        _nextSearch = now + 2f;
        var found = FriendLobbies(requestMissing: true);
        if (found.Count > 0)
        {
            _searchUntil = -1f;
            var (friend, name, lobby) = found[0];
            Plugin.Event($"NET found {found.Count} friend lobby(s): " + string.Join(", ", found.ConvertAll(f => $"{f.name} ({f.friend}) lobby {f.lobby}")) +
                         $"; joining {name}'s");
            Join(lobby);
            return;
        }
        if (now >= _searchUntil)
        {
            _searchUntil = -1f;
            Failed?.Invoke($"no friend is hosting (looked {FriendSearchSeconds:F0} s). The host must press F11 with Mode = HostSteam first; " +
                           "or set JoinLobby to the id from the host's log");
        }
    }

    /// <summary>
    /// Friends currently in this game with a lobby: from their rich presence (<c>connect</c>, set by a SodCoop host)
    /// or, if that hasn't arrived yet, the lobby Steam reports in their game info.
    /// </summary>
    public List<(ulong friend, string name, ulong lobby)> FriendLobbies(bool requestMissing)
    {
        var result = new List<(ulong, string, ulong)>();
        uint appId = SteamNative.SteamAPI_ISteamUtils_GetAppID(_utils);
        var info = Marshal.AllocHGlobal(32);
        try
        {
            int n = SteamNative.SteamAPI_ISteamFriends_GetFriendCount(_friends, SteamNative.FriendFlagImmediate);
            for (int i = 0; i < n; i++)
            {
                ulong friend = SteamNative.SteamAPI_ISteamFriends_GetFriendByIndex(_friends, i, SteamNative.FriendFlagImmediate);
                for (int b = 0; b < 32; b++) Marshal.WriteByte(info, b, 0);
                if (!SteamNative.SteamAPI_ISteamFriends_GetFriendGamePlayed(_friends, friend, info)) continue;
                ulong gameId = (ulong)Marshal.ReadInt64(info, 0);
                if ((uint)(gameId & 0xFFFFFF) != appId) continue;

                string connect = SteamNative.Utf8(SteamNative.SteamAPI_ISteamFriends_GetFriendRichPresence(_friends, friend, "connect"));
                ulong lobby = ParseLobbyArg(connect);
                if (lobby == 0) lobby = (ulong)Marshal.ReadInt64(info, 16); // FriendGameInfo_t.m_steamIDLobby
                if (lobby == 0)
                {
                    if (requestMissing) SteamNative.SteamAPI_ISteamFriends_RequestFriendRichPresence(_friends, friend);
                    continue;
                }
                result.Add((friend, SteamNative.Utf8(SteamNative.SteamAPI_ISteamFriends_GetFriendPersonaName(_friends, friend)), lobby));
            }
        }
        finally { Marshal.FreeHGlobal(info); }
        return result;
    }

    /// <summary>Lobby id from "+mp_sod_lobby &lt;id&gt;" or "+connect_lobby &lt;id&gt;" (rich presence or command line), or 0.</summary>
    public static ulong ParseLobbyArg(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            if ((parts[i] == RichPresenceArg || parts[i] == InviteArg) && ulong.TryParse(parts[i + 1], out var id)) return id;
        return 0;
    }

    /// <summary>Lobby id passed on the command line by an accepted invite or Steam's "Join game", or 0.</summary>
    public static ulong LobbyFromCommandLine() => ParseLobbyArg(string.Join(" ", Environment.GetCommandLineArgs()));

    public void Dispose()
    {
        if (LobbyId != 0) SteamNative.SteamAPI_ISteamMatchmaking_LeaveLobby(_mm, LobbyId);
        if (IsHost) SteamNative.SteamAPI_ISteamFriends_SetRichPresence(_friends, "connect", "");
        LobbyId = 0;
    }
}
