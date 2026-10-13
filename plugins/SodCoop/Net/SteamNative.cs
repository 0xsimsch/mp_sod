using System;
using System.Runtime.InteropServices;

namespace SodCoop.Net;

/// <summary>
/// Direct bindings to the Steamworks flat C API in the game's own steam_api64.dll (already loaded
/// and initialised by the game). Used instead of the IL2CPP Steamworks.NET interop so we never
/// need managed callbacks: everything is polled (messages, API call results).
/// Interface versions match the DLL shipped with v42.04 (checked against its export table).
/// </summary>
static class SteamNative
{
    const string Dll = "steam_api64";

    // --- interface accessors
    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamUser_v023();
    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamFriends_v017();
    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamMatchmaking_v009();
    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamUtils_v010();
    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();

    // --- ISteamNetworkingMessages
    [DllImport(Dll)]
    public static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(
        IntPtr self, ref SteamNetworkingIdentity remote, byte[] data, uint size, int sendFlags, int remoteChannel);

    [DllImport(Dll)]
    public static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(
        IntPtr self, int localChannel, [Out] IntPtr[] outMessages, int maxMessages);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, ref SteamNetworkingIdentity remote);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, ref SteamNetworkingIdentity remote);

    [DllImport(Dll)] public static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr msg);

    /// <summary>Returns ESteamNetworkingConnectionState; fills SteamNetConnectionInfo_t and SteamNetConnectionRealTimeStatus_t.</summary>
    [DllImport(Dll)]
    public static extern int SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(IntPtr self, ref SteamNetworkingIdentity remote, IntPtr info, IntPtr quickStatus);

    // --- ISteamNetworkingUtils
    [DllImport(Dll)] public static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
    /// <summary>Returns ESteamNetworkingAvailability (100 = current, negative = failed); details may be null.</summary>
    [DllImport(Dll)] public static extern int SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus(IntPtr self, IntPtr details);

    // --- global networking config (ESteamNetworkingConfigValue: 9 SendBufferSize, 10 SendRateMin, 11 SendRateMax; scope 1 = global)
    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(IntPtr self, int eValue, int val);

    [DllImport(Dll)]
    public static extern int SteamAPI_ISteamNetworkingUtils_GetConfigValue(
        IntPtr self, int eValue, int eScopeType, IntPtr scopeObj, ref int pOutDataType, ref int pResult, ref UIntPtr cbResult);

    public const int ConfigSendBufferSize = 9, ConfigSendRateMin = 10, ConfigSendRateMax = 11;

    // --- ISteamUser / ISteamFriends
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
    [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
    [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong steamId);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Dll)] public static extern void SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(IntPtr self, ulong lobbyId);
    [DllImport(Dll)] public static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int friendFlags);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int friendFlags);
    [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresence(IntPtr self, ulong friend, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Dll)] public static extern void SteamAPI_ISteamFriends_RequestFriendRichPresence(IntPtr self, ulong friend);

    /// <summary>Fills FriendGameInfo_t (24 bytes: CGameID, IP, ports, lobby SteamID at 16).</summary>
    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong friend, IntPtr gameInfo);

    // --- ISteamMatchmaking
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(IntPtr self, int lobbyType, int maxMembers);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(IntPtr self, ulong lobbyId);
    [DllImport(Dll)] public static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(IntPtr self, ulong lobbyId);
    [DllImport(Dll)] public static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(IntPtr self, ulong lobbyId);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(IntPtr self, ulong lobbyId, int index);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(IntPtr self, ulong lobbyId);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamMatchmaking_SetLobbyData(IntPtr self, ulong lobbyId, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamMatchmaking_GetLobbyData(IntPtr self, ulong lobbyId, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);

    // --- ISteamUtils (polling API call results instead of CallResult<T>)
    [DllImport(Dll)] public static extern uint SteamAPI_ISteamUtils_GetAppID(IntPtr self);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr self, ulong call, [MarshalAs(UnmanagedType.I1)] out bool failed);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamUtils_GetAPICallResult(IntPtr self, ulong call, IntPtr result, int resultSize, int expectedCallback, [MarshalAs(UnmanagedType.I1)] out bool failed);

    public const int SendUnreliable = 0;
    public const int SendNoNagle = 1;
    public const int SendReliable = 8;
    public const int SendAutoRestartBrokenSession = 32;

    public const int FriendFlagImmediate = 4;  // k_EFriendFlagImmediate: regular friends
    public const int LobbyTypeFriendsOnly = 1;
    public const int CallbackLobbyEnter = 504;   // LobbyEnter_t
    public const int CallbackLobbyCreated = 513; // LobbyCreated_t

    public static string Utf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p);
}

/// <summary>SteamNetworkingIdentity (136 bytes). Only the SteamID form is used.</summary>
[StructLayout(LayoutKind.Explicit, Size = 136)]
struct SteamNetworkingIdentity
{
    const int TypeSteamId = 16; // k_ESteamNetworkingIdentityType_SteamID

    [FieldOffset(0)] public int Type;
    [FieldOffset(4)] public int Size;
    [FieldOffset(8)] public ulong SteamId64;

    public static SteamNetworkingIdentity FromSteamId(ulong id) => new() { Type = TypeSteamId, Size = 8, SteamId64 = id };
}

/// <summary>Offsets into SteamNetworkingMessage_t (steamnetworkingtypes.h, 8-byte packing).</summary>
static class SteamMessageLayout
{
    public const int Data = 0;            // void* m_pData
    public const int Size = 8;            // int m_cbSize
    public const int PeerIdentity = 16;   // SteamNetworkingIdentity m_identityPeer
    public const int PeerSteamId = 16 + 8;
    public const int Channel = 192;       // int m_nChannel
}

/// <summary>Offsets into SteamNetConnectionInfo_t (696 bytes) and SteamNetConnectionRealTimeStatus_t (120 bytes).</summary>
static class SteamConnectionLayout
{
    public const int InfoSize = 696;
    public const int InfoEndReason = 180;    // int m_eEndReason
    public const int InfoEndDebug = 184;     // char m_szEndDebug[128]
    public const int InfoDescription = 312;  // char m_szConnectionDescription[128]
    public const int InfoFlags = 440;        // int m_nFlags (16 = relayed)
    public const int StatusSize = 120;
    public const int StatusPing = 4;         // int m_nPing (ms)
    public const int StatusQualityLocal = 8; // float m_flConnectionQualityLocal (0..1, -1 = unknown)
    public const int StatusPendingReliable = 40;     // int m_cbPendingReliable
    public const int StatusSentUnackedReliable = 44; // int m_cbSentUnackedReliable
    public const int FlagRelayed = 16;

    public static string StateName(int state) => state switch
    {
        0 => "none", 1 => "connecting", 2 => "finding-route", 3 => "connected",
        4 => "closed-by-peer", 5 => "problem-detected-locally", -1 => "fin-wait", -2 => "linger", -3 => "dead",
        _ => $"state{state}",
    };
}
