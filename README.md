# PLUT-0 // Plugin Game Launcher & Management Tool

A fast, minimalist C# (.NET 10 / Avalonia) launcher and plugin management tool for games
installed via ACCELA / SLSsteam plugins. Built for handhelds, TV setups, and couch gaming
with first-class SDL2 gamepad support.

Ported and rewritten in C# from [ASSella](https://github.com/niwia/ASSella), which remains
the Python reference implementation. Both read and write the same on-disk state, so a game
managed by one is fully managed by the other.

---

## Features

- **Controller-first navigation (SDL2)**: hotplugging for Xbox, PlayStation, Steam Deck,
  Switch Pro and generic controllers. Analog stick and D-pad navigation with deadzone
  compensation and auto-repeat.
- **Dual-source library**: merges AT0-M plugin-native games (`plugin_library.json`) with
  every ACCELA-managed game (`games_cache.json`), each watched with a `FileSystemWatcher` so
  the list updates live as ASSella installs or removes games.
- **In-place SLSsteam config editing**: rewrites `~/.config/SLSsteam/config.yaml` without
  replacing the inode, so SLSsteam's `inotify` watcher keeps working. A temp-file-plus-rename
  silently breaks that watcher until SLSsteam restarts.
- **Native depot downloads**: drives a bundled DepotDownloaderMod with live percentage and
  throughput streaming, plus SteamCMD/PICS for native Steam handoff.
- **Mode transition**: one action converts a game between ACCELA-managed and AT0-M
  plugin-native, cleaning up markers, `plugin_library.json`, and `config.yaml` in step.
- **Metadata**: SteamGridDB logos and hero art, RAWG descriptions and ratings, SteamDB tags,
  and an auto-rotating screenshot gallery.
- **Per-game tooling**: EOS proxy apply/remove, Steamless unpack, system health diagnostics,
  and SLSsteam IPC via `/tmp/SLSsteam.API`.
- **Landscape UI**: compact layout tuned for 16:9 and 16:10, minimum 760x440.

---

## Controller & Keyboard Mapping

| Action | Controller | Keyboard |
| :--- | :--- | :--- |
| **Navigate** | D-Pad Up/Down or Left Stick | Arrow Up / Down |
| **Launch** | **(A)** / Cross | Enter |
| **Manage / Details** | **(X)** / Square | `X` |
| **Focus Search** | **(Y)** / Triangle | `Y` or `/` |
| **Back / Clear** | **(B)** / Circle | Escape |
| **Fast Scroll** | **LB** / **RB** | Page Up / Page Down |
| **Sync All Games** | **Start** / Menu | Sync All button |
| **Refresh Library** | Automatic via file watcher | `F5` / Refresh button |

---

## Architecture

```
Pluto/
├── Models/
│   ├── PluginGame.cs             Game record; source of truth for list/detail state
│   └── SearchResultItem.cs       Search hit (may not be installed locally)
├── Services/
│   ├── PlutoPaths.cs             Every filesystem path, in one place
│   ├── PluginLibraryService.cs   Dual-source library merge + file watchers
│   ├── SlsSteamService.cs        In-place config.yaml editing, IPC pipe, launch
│   ├── SteamAcfService.cs        appmanifest_<appid>.acf parse/repair (VDF)
│   ├── DepotKeyService.cs        AES key lookup in depot_keys.db
│   ├── AccelaConfigService.cs    ACCELA.conf reader/writer
│   ├── GameTransitionService.cs  ACCELA <-> AT0-M conversion
│   ├── GamepadService.cs         SDL2 controller loop, hotplug, custom mappings
│   ├── HubcapSearchService.cs    Manifest/metadata search + key fetch
│   ├── RawgService.cs            RAWG metadata, images, disk cache
│   ├── SteamGridDbService.cs     Logos and hero art
│   ├── SteamTagService.cs        SteamDB tag table with icon mapping
│   ├── EosProxyService.cs        EOS proxy apply/remove with hash tracking
│   ├── SteamlessService.cs       Steamless unpack integration
│   ├── HealthService.cs          Steam/SLSsteam/HTTP diagnostics
│   ├── ThemeService.cs           Theme palettes
│   └── PlutoLogger.cs            Rotating file log + stdout
├── Engine/
│   ├── DepotDownloader/          DepotDownloaderMod process + progress parsing
│   ├── Steam/                    ACF writing, SteamCMD, library scan, depot names
│   └── Installation/             Install orchestration, pre-download config
└── MainWindow.*.cs               UI split across 5 partials
```

`MainWindow` is split by concern: `.axaml.cs` (lifecycle), `.Navigation.cs` (input routing),
`.Detail.cs` (game detail page), `.Search.cs` (live search), `.Settings.cs` (settings).

---

## Paths

All paths resolve through `Services/PlutoPaths.cs`. Nothing is hardcoded, so the app works
from any checkout, install location, or username.

| Purpose | Location |
| :--- | :--- |
| Plugin library | `~/.local/share/ACCELA/db/plugin_library.json` |
| ACCELA game cache | `~/.local/share/ACCELA/db/games_cache.json` |
| Depot keys (SQLite) | `~/.local/share/ACCELA/db/depot_keys.db` |
| ACCELA settings | `~/.config/Tachibana Labs/ACCELA.conf` |
| SLSsteam config | `~/.config/SLSsteam/config.yaml` |
| SLSsteam API pipe | `/tmp/SLSsteam.API` |
| Pluto logs | `~/.local/share/pluto/logs/pluto.log` |
| API keys | `~/.config/pluto/{rawg_api,steamgriddb_api}.txt` |

---

## Building & Running

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- `libSDL2` (`libSDL2-2.0.so` on Linux)

### Run

```bash
dotnet run
```

### Build Single-File Release

```bash
dotnet publish -c Release -r linux-x64 --self-contained false
```

The bundled `DepotDownloaderMod` under `Engine/DepotDownloader/Bin/` is copied to the output
directory automatically and resolved at runtime relative to the binary.

---

## Configuration

Settings are stored in ACCELA's `ACCELA.conf` under `[General]`, so Pluto and ASSella share
preferences. API keys can also be placed in `~/.config/pluto/` as plain text files.

SteamDB tags are loaded from `~/.local/share/ACCELA/tags_cache.json`. If that cache is absent,
Pluto falls back to plain text tags until the HTML table is regenerated.

---

## Logging

Everything meaningful is logged to `~/.local/share/pluto/logs/pluto.log` (rotated at 5 MB).
Network and filesystem failures are logged rather than swallowed, so a missing depots list
or a broken config produces a diagnosable trail instead of a silent no-op.

---

## Versioning

Per `.agents/rules/versioning.md`: `0.0.8-xxday+month+26`, where `xx` is a sequential build
counter in `Services/PlutoVersion.cs`. The date is derived from the assembly build timestamp
rather than hardcoded, so it cannot go stale.

---

## Interoperability with ASSella

- **Marker folders**: `<install_dir>/.ACCELA` or `.DepotDownloader` denote ACCELA-managed
  installs. Converting to AT0-M removes them; converting back recreates `.ACCELA`.
- **Metadata**: Pluto writes `.ACCELA/metadata.json` alongside installs, so ASSella can read
  back what Pluto installed.
- **ACF manifests**: both tools write Steam-standard `appmanifest_<appid>.acf` files.