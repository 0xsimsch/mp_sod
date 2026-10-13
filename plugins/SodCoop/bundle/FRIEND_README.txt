SodCoop: Shadows of Doubt co-op test build
==========================================

This is an early test: we are checking whether two machines can connect over Steam and play in the same world.
Expect rough edges. Your own saves are never written by the mod.

Install
-------
1. Steam > Shadows of Doubt > right click > Manage > Browse local files. Needs game version v42.04 (bottom right
   of the main menu).
2. Unzip this archive INTO that folder, so that winhttp.dll ends up next to "Shadows of Doubt.exe" and there is
   a BepInEx folder next to it.
3. Linux / Steam Deck (Proton) only: in Steam, game Properties > Launch options, enter
       WINEDLLOVERRIDES="winhttp=n,b" %command%
   (Windows: nothing to do.)
4. Start the game. The FIRST start takes a few minutes longer with a black or frozen window: BepInEx generates
   its files for this game version (BepInEx/interop). Let it finish. When the mod is loaded, BepInEx/sodcoop.log exists and starts with "SoD Co-op 0.9.3 loaded".

Join the host
-------------
The host (your friend with the world) starts the session first. Then any of these:
 a) Easiest: in the main menu click "Join Friend" (or press F11).
    The mod finds the friend who is hosting and joins them.
 b) Right click the host in your Steam friends list > "Join game", or accept the host's Steam invite.
    Works whether the game is running or not (if not, Steam starts it and it joins once the main menu is up).

Then wait about 30-60 s: the host's world is sent to you and loads by itself (a temporary "MP Join" save
appears and is removed again). You play in the host's world, with your own money and inventory.
While connected, a box in the top left corner shows the connection (ping, relayed/direct) and how much of
the host's world has arrived. Don't click anything while it says "receiving the host's world".

Keys
----
 F11         join (or host, see Alt+F11; during a session: write a status line to the log)
 Alt+F11     swap between joining and hosting (remembered). To host: load your own game, then F11
 Ctrl+Delete leave the session
 Ctrl+F10    hide/show the connection box
 Don't use the Save/Load menu during a session (it's disabled).

If something goes wrong
-----------------------
Send BOTH machines' files back:
   BepInEx/sodcoop.log      (the mod's log; restarting the game moves it to sodcoop.prev.log, so send both)
   BepInEx/sodcoop.prev.log
   BepInEx/LogOutput.log    (BepInEx's log)
Press F11 once while connected before quitting: it writes the connection state (ping, relayed or direct) into
the log.

Uninstall
---------
Delete winhttp.dll (that alone disables the mod), or also doorstop_config.ini, .doorstop_version,
changelog.txt and the BepInEx and dotnet folders. On Linux also clear the launch option.
