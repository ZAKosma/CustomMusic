# Custom Music — Battlestar Galactica: Scratched Vinyls

My second mod for *Battlestar Galactica: Scattered Hopes* (see also
[Extra Rerolls](../../)). This one lets you swap the game's built-in music
tracks — title screen, fleet, combat, the bar, and more — for your own
`.wav`, `.mp3`, or `.ogg` files, with smooth configurable crossfades when
the game switches between them.

This is part of an ongoing series of mods for this game. Let me know if
you want help building your own!

## What it does

- Lets you drop your own music files into per-context folders to replace
  the game's stock tracks for the title screen, fleet, bar, combat, and
  more (12 contexts total — see the list below)
- Crossfades smoothly between tracks when a game trigger switches context,
  with independently configurable fade-in and fade-out times
- If a folder has more than one file in it, picks between them either by
  shuffle or in alphabetical rotation — your choice
- Optional stem-based crossfade for a track that should shift in
  intensity (e.g. a combat theme that layers up) by naming files
  `stem_0.ogg`, `stem_1.ogg`, `stem_2.ogg`...
- This build is music-only — ambient sound beds and sound effects are
  left completely untouched

## Requirements

- **MelonLoader** must be installed for the game. If you don't have it yet:
  1. Download the MelonLoader installer: https://melonwiki.xyz/
  2. Run it, click "Select" and point it at your game's `.exe`
     (the same folder as `BattlestarGalacticaScatteredHopes.exe`)
  3. Click Install. This only needs to be done once, ever, it's shared by
     all MelonLoader mods, not just this one.

## Installing this mod

1. Download `CustomMusic.dll` from this repo's [Releases](../../releases)
   page.
2. Open your game's install folder and find the `Mods` folder
   (created automatically once MelonLoader is installed).
3. Drop `CustomMusic.dll` into `Mods`.
4. Launch the game once, just up to the main menu is enough, then quit.
   **This first launch is required** — it's what creates the folders
   you'll drop your music into. See below.
5. Add your music, then launch again. See **Setting up your music** below.

A console window opens alongside the game; on that first launch you
should see something like:

```
[CustomMusic] CustomMusic mod loaded.
[CustomMusic] CustomMusic: PlaybackOrder = 'Shuffle', IntroFadeSeconds = 5, OutroFadeSeconds = 5, HastenedFadeSeconds = 1.
[CustomMusic] CustomMusic: discovered 150 total audio contexts; this is a music-only build, so folders were created for 12 of them under 'Mods/CustomMusicAssets/'.
```

## Setting up your music

1. Launch the game with the mod installed and let it reach the main menu
   — you don't need to start a run. Then quit.
2. That launch creates a folder for every replaceable music context under:

   ```
   Mods\CustomMusicAssets\
   ```

3. Open that folder. You'll see one subfolder per context:

   | Folder | Roughly plays during |
   |---|---|
   | `MainMusicTitleScreenEventRef` | Main menu / title screen |
   | `MainMusicRecap` | Recap screen |
   | `MainMusicFleetEventRef` | General fleet / exploration screen |
   | `MainMusicGameOver` | Game over screen |
   | `MainMusicCombatListEventRef` | Combat prep / target list screen |
   | `MainMusicCombatBossFirstAndSecondEventRef` | Boss fight, early phases |
   | `MainMusicCombatBossFinalEventRef` | Boss fight, final phase |
   | `MainMusicMetaUpgradeEventRef` | Meta-progression / upgrade screen |
   | `MainMusicTechnicalInteriorsEventRef` | Interior / technical rooms |
   | `MainMusicVictoryEventRef` | Victory screen |
   | `MainMusicBarEventRef` | The bar |
   | `MainMusicVisionEventRef` | Vision / story sequence |

   (These mappings are my best read of the game's own naming, not
   something officially documented — if one doesn't match what you hear,
   trust your ears over the table.)

4. Drop one or more `.wav`, `.mp3`, or `.ogg` files into whichever
   folder(s) you want to override. Leave a folder empty to keep that
   context's original music untouched.
5. Launch the game again — your music will now play at those triggers
   instead of the stock track.

You can add, remove, or swap files in these folders any time; changes
apply the next time that context's music would normally start.

### Multiple files in one folder

If a folder has more than one file, the mod picks between them using the
`PlaybackOrder` setting (see below) — either a random shuffle, or cycling
through them alphabetically each time that context plays.

### Advanced: intensity layers with stems

For a track that should shift in energy rather than just loop flat, name
your files `stem_0.ogg`, `stem_1.ogg`, `stem_2.ogg`, and so on (low to
high intensity, at least 2 files). The mod plays all of them at once and
crossfades between them based on the game's own intensity parameter for
that context, instead of just picking one file to loop.

## Configuring playback and fade timing (optional)

You don't need to do this, the mod works out of the box with sensible
defaults. But if you want to change how it behaves:

1. Run the game once with the mod installed (this generates the config file).
2. Close the game.
3. Open `UserData\MelonPreferences.cfg` in the game folder with any text editor.
4. Find the `[CustomMusic]` section and adjust any of:
   - `PlaybackOrder` — `Shuffle` or `Alphabetical`
   - `IntroFadeSeconds` — how long an incoming track takes to fade up
   - `OutroFadeSeconds` — how long the outgoing track takes to fade out
   - `HastenedFadeSeconds` — how quickly a track that's already fading
     out gets cut short if yet another context switch interrupts it
5. Save, relaunch the game.

## ⚠️ Experimental — read before installing

This is an early, experimental mod for a game I haven't seen modded by
anyone else yet, so there's no established community precedent here for
what is or isn't safe to touch. In my own testing it's worked fine with
no save corruption or crashes tied to the mod itself, but:

- **I am not responsible if this causes save corruption, lost progress,
  or other issues with your game.** Back up your save files before using
  this if you want to be safe.
- Use at your own risk, especially on a save you care about.

If you do run into an issue, please open an Issue on this repo with your
`MelonLoader\Latest.log` attached — that helps a lot in tracking down
what happened.

## Troubleshooting

**No console window appears at all** — MelonLoader isn't installed correctly.
Re-run the MelonLoader installer and make sure you selected the correct
game `.exe`.

**Console appears, but no "CustomMusic" line** — the DLL isn't in the
right folder. Double check it's directly inside `Mods`, not a subfolder.

**`CustomMusicAssets` folder never appeared** — make sure you actually
reached the main menu (not just the initial loading screen) before
quitting on that first launch; that's the moment the folders get created.

**A folder has music in it but the stock track still plays** — check the
file extension is `.wav`, `.mp3`, or `.ogg`, and that the file is directly
inside the context folder (not a further subfolder).

**Music still sounds like it's overlapping** — try lowering
`OutroFadeSeconds` and/or `HastenedFadeSeconds` in the config file for a
snappier, less overlapping transition.

**Ambient sounds/background noise didn't change** — that's by design.
This build only replaces actual music tracks; ambient beds and sound
effects are left completely stock.

## Uninstalling

Delete `CustomMusic.dll` from the `Mods` folder. You can also delete the
`Mods\CustomMusicAssets` folder if you want to remove your added music
files too — neither is required for the other. Stock music returns as
soon as the DLL is gone.

## Credits

Made by Zachary Aidan Kosma. Built with [MelonLoader](https://melonwiki.xyz/)
and [Harmony](https://harmony.pardeike.net/).


## MIT LICENSE

Copyright © 2026 Zachary Kosma

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
