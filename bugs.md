random crash: 'suspend tread loop failed'

- hasnt happen since turning of fsync

on spawn host is not having a body
  - 0.9.52: same root cause as the "only the face is drawn" bug (outfit parts stamped onto the culled
    Player layer). Fixed by Patches/OutfitLayerPatch.cs + RemotePlayerBody.RepairModelLayers(). Untested.

case is not updates when client does mission
murder case start: big title text missing on the client (host sees it)
  - 0.9.53: the title is the game's own InterfaceController.NewMurderCaseDisplay(), which runs in the host's
    MurderController.OnVictimDiscovery - a method the client skips (CaseIds forwards the discovery to the host),
    so only the case record/objectives reached it. The case record now carries a "fresh" flag (protocol v45): a
    client shows the title when a fresh murder case lands that it doesn't have yet. The welcome batch is sent
    not fresh, so joining players don't see it for already-running cases. Untested in game.
host is invisible after join @client. even label is not shown
  - 0.9.52: body part fixed together with "on spawn host is not having a body" above; if the label
    (nametag) is still missing after that, it is a separate bug in MapMarker.

client cant dmg host

actions do no sound
