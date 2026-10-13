# Determinism test: is city generation reproducible from a seed?

**Answer: yes, for practical purposes.** Two separate game launches generating the same share code produced the same city in every respect except the stored position of 2 inventory items out of 34,426 objects.

## Setup

| | |
|---|---|
| Game build | v42.04 (Unity 2021.3.45f2, IL2CPP), Proton Hotfix, Linux |
| Share code | `New Lilly-Crofte.1.4204.86SypmW6Q43atlQr` (Medium, 6×5, city editor off) |
| Character | MinJun Chu, Male, Easy (the defaults; identical in both runs) |
| Run 1 | fresh game launch → Sandbox → Generate City (21:10–21:14) |
| Run 2 | game fully exited and relaunched → same steps (21:19–21:23) |
| Same machine | yes, so cross-machine determinism is **not** tested yet |

Files written by the game: `%LOCALAPPDATA%Low/ColePowered Games/Shadows of Doubt/Cities/<sharecode>.citb`, Brotli-compressed JSON (about 7.7 MB → 103 MB) with a few footer bytes after the Brotli stream.

- `captures/`: the raw `.citb`/`.txt` files, copied as the game wrote them
- `decoded/`: decompressed JSON
- `compare_run1_vs_run2.txt`: full comparison output

## Result

Everything compared field by field, ignoring only the `date` timestamp. Floats were compared with tolerance 1e-4.

| Section | Entries | Differences |
|---|---|---|
| citizens | 319 | 0 |
| districts / streets / cityTiles | 5 / 27 / 30 | 0 |
| groups / criminals | 73 / 1 | 0 |
| metas / multiPage / pipes | 1732 / 493 / 116 | 0 |
| interactables | 34,426 | **2 objects** (position fields only) |

The 2 differing objects:

| Index | ID | Preset | Location | What differs |
|---|---|---|---|---|
| 34423 | 10002685 | `BBCard-MickeyEsposito` | inventory of citizen 31 | `wPos` / `spWPos` / `lPos` |
| 34424 | 10002686 | `BBCard-MickeyEsposito` | inventory of citizen 198 | `wPos` / `spWPos` / `lPos` |

Their IDs, owners, values and flags all match. They're among the last objects created (ID range 10,000,000+, `wo: true`, `rPl: true`). The position looks like a world spot sampled when the card was handed out, using an unseeded random call or depending on timing. Since the cards sit in an inventory, the position doesn't matter until the card is dropped.

## Takeaways for multiplayer

1. **Clients can generate the city locally from the share code.** No need to transfer the 100 MB city.
2. **IDs are stable**, including the late-created 10M-range objects. This answers part of open question B2 for generation time. IDs created *during play* are still untested.
3. **Small leaks exist.** At least one late generation step uses non-seeded randomness. The fix is either:
   - have the host send a hash of the city on join, plus authoritative values for the few objects known to differ, or
   - patch that one placement call to use the seeded RNG.

## Not covered yet

- **Different machines:** a different CPU, thread count or OS could change the order of async generation work. Repeat this test on a second PC (just paste the share code and compare files).
- **Starting state after generation:** the "pre-sim" and start-of-game simulation run after the city file is written (where citizens are, what time it is, the first murder setup). That state lives in the save game, not the city file, and wasn't compared.
- **Generation within one session:** both runs here were fresh launches, which is the realistic multiplayer case. Generating twice inside one session was not tested.

## How to repeat

```bash
# decompress (ignore the "corrupt input" warning caused by the footer bytes)
nix shell nixpkgs#brotli -c brotli -d -c X.citb > X.json
python3 -I compare_cit.py run1.json run2.json
```
`compare_cit.py` is in this folder.
