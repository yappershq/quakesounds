<div align="center">
  <h1><strong>QuakeSounds</strong></h1>
  <p>Quake-style killstreak, combo, headshot and first-blood announcer sounds for CS2 — with per-player sound packs, volume and mute, all configurable.</p>
</div>

<p align="center">
  <a href="https://github.com/Kxnrl/modsharp-public"><img src="https://img.shields.io/badge/framework-ModSharp-5865F2?logo=github" alt="ModSharp"></a>
  <img src="https://img.shields.io/badge/game-CS2-orange" alt="CS2">
  <img src="https://img.shields.io/github/stars/yappershq/quakesounds?style=flat&logo=github" alt="Stars">
</p>

---

QuakeSounds is a [ModSharp](https://github.com/Kxnrl/modsharp-public) plugin for Counter-Strike 2 that plays Quake-arena-style voice announcements as players rack up kills — first blood, multi-kill combos, kill streaks, consecutive-headshot streaks, plus knife/grenade/suicide/team-kill stingers and a round-start "prepare" cue. Each tier can show a colored center-screen banner, and players pick their own sound pack, volume, and mute state (persisted via ClientPreferences).

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/QuakeSounds/` | `<sharp>/modules/QuakeSounds/` |
| `.build/configs/quakesounds.json` | `<sharp>/configs/quakesounds.json` |
| `.build/locales/quakesounds.json` | `<sharp>/locales/quakesounds.json` |

Restart the server (or change map) to load. Requires the ClientPreferences, LocalizerManager, MenuManager and AdminManager ModSharp modules (mute/pack/volume persistence and the menu degrade gracefully if a module is missing). The configured soundevents must be mounted and registered in a precached `.vsndevts` file (see `precacheFiles`).

## ⌨️ Commands

| Command | Aliases | Description |
|---------|---------|-------------|
| `!quake` | `!qs` | Open the settings menu (sound pack, volume, mute) |
| `!quakepack` | — | Cycle to the next sound pack you have access to |
| `!quakemute` | — | Toggle quake sounds on/off for yourself |

## ⚙️ Configuration

### ConVars

Registered on load (set in your server config):

| ConVar | Default | Meaning |
|--------|---------|---------|
| `qs_enabled` | `true` | Master enable for the plugin |
| `qs_killstreaks` | `true` | Enable kill-streak and combo sounds |
| `qs_firstblood` | `true` | Enable the first-blood sound |
| `qs_headshot` | `true` | Enable headshot-kill sounds |
| `qs_center_message` | `true` | Show colored center-screen banners |
| `qs_during_warmup` | `false` | Play sounds during warmup |
| `qs_reset_on_death` | `true` | Reset a player's kill streak when they die |
| `qs_rapid_kill_window` | `4.0` | Seconds within which kills count as a rapid multi-kill combo |
| `qs_volume` | `1.0` | Server-default sound volume (`0.0`–`1.0`) |
| `qs_sound_debounce` | `0.1` | Window (seconds) to coalesce overlapping sounds; only the highest-priority one plays |

### `configs/quakesounds.json`

Auto-generated with a `default` pack on first run if missing. Top-level keys:

| Key | Meaning |
|-----|---------|
| `precacheFiles` | List of `.vsndevts` soundevent files to precache (defaults to `soundevents/soundevents_general.vsndevts`) |
| `packs` | Sound packs — each has `id`, `name`, optional `permission` (admin flag required to select it), and a `sounds` map of sound key → soundevent name |
| `tiers` | Optional override of the killstreak / combo / headshot thresholds and the special-kill (`first_blood`, `knife_kill`, `grenade_kill`, `suicide`, `team_kill`, `headshot_ding`, `round_start`) entries; omitted blocks fall back to built-in defaults |

Each tier entry maps a `count` (streak threshold) to a `soundKey`, plus an optional `localeKey` + `color` for the center banner.

## 🔧 How it works

The module hooks `player_death` and maintains per-player slot-indexed counters: a persistent kill streak (cleared on death unless `qs_reset_on_death` is off), a short-fuse combo counter (resets outside `qs_rapid_kill_window`), and a consecutive-headshot streak. Each kill produces sound candidates that compete by priority — killstreak magnitude dominates, first blood is high, the personal headshot ding is lowest — and within a `qs_sound_debounce` window only the single highest-priority winner is played, so near-simultaneous kills never overlap into cacophony. Sounds are emitted per-client with `EmitSoundClient` at each player's chosen volume, skipping muted players, and resolved through the killer's selected sound pack.

## 📦 Build

```bash
dotnet build -c Release
```

Outputs `.build/modules/QuakeSounds/QuakeSounds.dll` plus `.build/configs/quakesounds.json` and `.build/locales/quakesounds.json`.

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
