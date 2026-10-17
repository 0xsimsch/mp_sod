# The main menu: buttons, bars, background

Static: `BepInEx/interop/Assembly-CSharp.dll` (v42.04) dumped with the new `analysis/tools/interop_dump.py`
(needs `pip install dnfile`; works on the real interop and on the stripped `lib/refs`).
Dynamic: SodCoop in game, 2026-10-16. Behavior not seen in the native code is marked *(inferred)*.

## Why this exists

1. `CoopMenu` inserts up to 4 button copies under "Load Game" in the game's own main/Esc menu. The list grows
   downward; the game's **header/footer decoration lines did not move with it** and the footer ended up across
   the new bottom buttons. Fixed 2026-10-16 by having `CoopMenu` remember each line's original distance to the
   visible button column and re-place it every canvas frame (`CaptureBars`/`PlaceBars` in `Patches/CoopMenu.cs`).
2. Future goal: **replace the menu background**. Findings below.

## Structure of MainMenuController (verified member types)

`MainMenuController` (`.MainMenuController : MonoBehaviour`, singleton `Instance`). Note: the BepInEx IL2CPP
interop exposes every game field as a **public property** - that is what `SodCoop` compiles against, and
property accessors survive `RefStrip`, so members like `menu.topBarRend` are usable without reflection.
`interop_dump.py "$GAME/BepInEx/interop/Assembly-CSharp.dll" MainMenuController`.

- Button column (`ButtonController` fields, all public): `saveGameButton`, `loadGameButton`,
  `sandboxGameButton`, `cityGenButton`, `resumeGameButton`, `helpButton`, `bugReportButton`, `modsButton`,
  `developerOptionsButton`, `playerNameButton`, …
- The header/footer lines are **not** controller fields. In the scene they are the `MainMenuPanel`'s children
  `EmbelishmentTop`/`EmbelishmentBottom` (red line `Image`s, siblings of the buttons → same local space).
  `topBarRend`/`bottomBarRend` are a trap: they are the screen-edge vignette gradients (`TopGradient` at world
  y≈1440, `ButtomGradient` at y≈213) - moving them changes nothing visible, which is what sent the first fix
  attempts chasing zeros. Found the real pair with a one-off `DumpTree()` scene dump (see "The bar-follow fix").
- `UnityEngine.Canvas` (the placement hook, see below) lives in **UnityEngine.UIModule.dll** - the mod
  references that module (also added to `lib/refs` via `RefStrip`).
- Panels: `SetMenuComponent(Component|int)`, `EnableMainMenu(val, …, Component)`, `currentComponent`,
  `previousComponent`, `components` (a list of `MenuComponent`), the `MenuMotion`/`FadeMenu` coroutines,
  `OnMenuComponentSwitchComplete`.
- `MenuComponent` (plain `Object`, one entry per panel): `component` (enum), `rect` (RectTransform),
  `xPhase`, `onscreenAnchoredPosition`, `buttons` (List\<ButtonController\>), `previouslySelected`, `skipMotion`.
  So each panel is a rect that **slides in horizontally** *(inferred from xPhase/onscreenAnchoredPosition)*.
- `MainMenuController.Component` enum: `none, mainMenuButtons, settings, newGameSelect, city, citySelect,
  generateCity, charSetup, interfaceSettings, graphicsSettings, audioSettings, gameplaySettings,
  controlSettings, devSettings, saveGame, loadGame, credits, loadingCity, splash, controlDetect,
  streamingSettings, bugReport, mods, gameplayModifiers, controlBindings`.

## Background (for the planned change)

- `public UnityEngine.UI.Image backgroundImage` - a UI Image on the menu canvas, behind the panels.
- `public UnityEngine.UI.Image logoImage` - the game logo.
- `public UnityEngine.CanvasRenderer fadeOverlay` - the fade used in `EnableMainMenu`/`MenuMotion`.
- `public RectTransform mainMenuContainer` - the menu's root rect.
- **Resolved 2026-10-17** (UnityPy extraction): the backdrop is a **still image** - `Texture2D`/`Sprite` `menuBG`
  (2048x1152, RGBA32, single mip level) in `sharedassets0.assets`. The scene's `Background Image` GameObject is
  anchored full-rect (0..1, zero offsets, no fitter), so the texture is **stretched to the screen at any aspect**
  (1.35x horizontal at 21:9) and bilinearly upscaled beyond its 2048 px width; there is no higher-res variant.
  `Image.color`/the canvas stack: not live after all - swapping the background is just setting
  `backgroundImage.sprite` (and keeping the bars' sprite untouched), which SodCoop 0.9.32 now does from an
  embedded PNG (`plugins/SodCoop/Patches/MenuBackground.cs`).

## Button behavior the mod relies on

- `ButtonController`: `rect`, `button` (UGUI Button), `rend` (CanvasRenderer), `background`/`icon` (Image),
  `text` (TextMeshProUGUI), plus a big set of navigation fields (`allowLeftNavigation`, …,
  `thisNavRectPoint`/`otherNavRectPoint`, `findableForAutoNavigation`). `SetInteractable(bool)` is what the
  mod greys buttons out with.
- Copies made by `CoopMenu` inherit the game's `MenuAutoTextController` (re-labels the button back to the
  localized original) - the mod destroys it; and they are **not** in the panel's `MenuComponent.buttons`
  list, so the game's keyboard/controller navigation can skip them. If navigation needs to include the
  co-op entries, appending them to that list is the lever.

## The bar-follow fix (in `Patches/CoopMenu.cs`)

The two red lines are the row's children `EmbelishmentTop`/`EmbelishmentBottom`, found by name in the
`MainMenuPanel` (`FindChild`). Being siblings of the buttons, their world-y is directly comparable with the
buttons' world-y - no scale mismatch (that mismatch is exactly why the gradient objects failed).

- Capture (`CaptureBars` → `TryCaptureBars`), against the **vanilla** column: our copies sit IN the list
  (after Load Game) and shift the game buttons down, so measuring around them would bake the shift into the
  gap and make the follow a no-op. `TryCaptureBars` hides the visible copies, does a `ForceRebuildLayoutImmediate`,
  measures the game-only top/bottom edge (name-filtered AND hidden, template's own x-column only), then
  restores them. The gap is `Embelishment.position.y - columnEdge`.
- Acceptance: the menu plays its buttons in one by one, so a single reading can be mid-intro. A streak of
  >= 3 identical samples (edges + visible game-button count) spanning >= 0.6 s must pass before the gaps are
  used. Frame-rate independent on purpose: a fixed 0.4 s per-sample gap never triggered in the main menu
  because the `Refresh` tick is 30 frames = 0.2 s at 144 fps, and only "worked" in-world (~60 fps).
- Placement (`PlaceBars`), on **every canvas frame**, via a Harmony postfix on `UnityEngine.Canvas.SendWillRenderCanvases`
  - the last managed point before the frame draws, so it beats the game's `Update`, coroutines, `LateUpdate`
  and the `CanvasUpdateRegistry` layout rebuilds alike. (The `willRenderCanvases` event itself is not
  subscribable from managed code: the interop delegate's only constructor takes a native `IntPtr`.) It places
  each line at `full column edge + gap` (full = including our copies, so the footer follows Host/Join ↔
  Invite/Leave changes) and only while the row is active, so other panels are left to the game.
- Caching: accepted gaps go to `PlayerPrefs` (`sodcoop.menuGapTop.v2`/`menuGapBottom.v2`, plus the `menuGapRes`
  string they were measured at). The next launch places the lines from the cache on frame one (no
  wrong-position flash); a resolution change misses the guard and a fresh measurement runs.
- Trap seen (0.9.19/0.9.20): "re-validate every launch and refresh the cache" is **circular** once cached
  placement is active - after the first placement, `Embelishment.position.y - columnEdge` measures our own
  write, not the game's resting spot, and got stored (log: cached bottom -4.9, then "updated" to -81.8 →
  footer ~80 px too low). Rule now: gaps are measured exactly ONCE per game run, always before the first
  placement (`_haveGaps` guards both `CaptureBars` re-entry and `TryCaptureBars`), then final for the run.
- Interop gotchas found while testing (all cost a test run each):
  - `transform.parent as RectTransform` returns **null** at runtime (the wrapper is typed `Transform`) -
    use `GetComponent<RectTransform>()`. Same for children.
  - `GetWorldCorners(Vector3[])` **does not fill the array** across the IL2CPP call; edges come from
    `rect` + `TransformPoint` (struct return values marshal fine).
  - Per-frame writes only take effect from the canvas callback; from `Update` **and** `LateUpdate` the bar
    snapped back before drawing.
  - The bars were `topBarRend`/`bottomBarRend`... which are the vignette gradients, not the lines. The
    real objects were only settled with a one-off `DumpTree()` (walks the canvas root, logs name/position/
    renderer flags). Kept out of the build; rerun from history if the menu changes.
- Log lines: `MENU bars use cached offsets: top … / bottom …` (cache hit at open), then
  `MENU bar offsets captured (row MainMenuPanel, N buttons): gaps top … / bottom …; column hi..lo` when the streak accepts, or `MENU bar capture failed: <reason>` (once).
- Known cosmetic edge, first run only (no cache yet): the footer sits at the game's fixed spot during the
  button intro and jumps into place ~1 s after it settles. From the second run the cache removes it.
