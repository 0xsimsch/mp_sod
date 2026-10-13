using BepInEx.Configuration;

namespace SodCoop.Net;

/// <summary>BepInEx/config/mp_sod.coop.cfg, section [Net]. F11 starts a session with these settings.</summary>
public static class NetConfig
{
    public enum NetMode { HostSteam, JoinSteam, HostUdp, JoinUdp }

    public static ConfigEntry<NetMode> Mode;
    public static ConfigEntry<int> UdpPort;
    public static ConfigEntry<string> JoinAddress;
    public static ConfigEntry<ulong> JoinLobby;
    public static ConfigEntry<string> PlayerName;
    public static ConfigEntry<bool> VerboseLog;

    public static void Bind(ConfigFile cfg)
    {
        Mode = cfg.Bind("Net", "Mode", NetMode.HostSteam,
            "What F11 does. HostSteam: create a friends-only Steam lobby (invite with Shift+F11). JoinSteam: join JoinLobby. " +
            "HostUdp/JoinUdp: plain UDP for LAN or same-machine tests.");
        UdpPort = cfg.Bind("Net", "UdpPort", 47615, "Port a UDP host listens on.");
        JoinAddress = cfg.Bind("Net", "JoinAddress", "127.0.0.1:47615", "host:port for JoinUdp.");
        JoinLobby = cfg.Bind("Net", "JoinLobby", 0UL, "Steam lobby id for JoinSteam (the host's log prints it). Steam's \"Join game\" fills this in automatically.");
        PlayerName = cfg.Bind("Net", "PlayerName", "", "Name shown to others; empty = Steam name / \"Player\".");
        VerboseLog = cfg.Bind("Debug", "VerboseLog", false,
            "Write the developer diagnostics (perception DIAG dump every 3 s, PERF every 10 s) to BepInEx/sodcoop.log.");
    }

    public static NetSession StartFromConfig(int maxPlayers)
    {
        Mode.ConfigFile.Reload(); // pick up edits made while the game runs
        switch (Mode.Value)
        {
            case NetMode.HostSteam: return NetSession.HostSteam(maxPlayers);
            case NetMode.JoinSteam: return NetSession.JoinSteam(JoinLobby.Value);
            case NetMode.HostUdp: return NetSession.HostUdp(UdpPort.Value);
            default: return NetSession.JoinUdp(JoinAddress.Value);
        }
    }
}
