# PLUTO // ACCELA & AT0-M System Integration & Handover Guide

> **Target Audience**: Developers, subagents, and tools building or extending **Pluto** (C# .NET 10 / Avalonia UI).  
> **Purpose**: This document specifies how Pluto can read ACCELA-managed games, seamlessly transition games between **ACCELA Managed** mode and **AT0-M (Plugin Native)** mode, interact with Steam `appmanifest_<appid>.acf` files, and communicate with the **SLSsteam Bridge** (`assella_bridge.lua`).

---

## Table of Contents
1. [Ecosystem Architecture & Data Layout](#1-ecosystem-architecture--data-layout)
2. [Game Operating Modes](#2-game-operating-modes)
3. [How to Discover & Read ACCELA Games](#3-how-to-discover--read-accela-games)
4. [Mode Transition Protocols](#4-mode-transition-protocols)
   - [A. Moving ACCELA Managed ➔ AT0-M (Plugin Native)](#a-moving-accela-managed--at0-m-plugin-native)
   - [B. Moving AT0-M ➔ ACCELA Managed](#b-moving-at0-m--accela-managed)
5. [The Steam ACF Manifest Contract (`appmanifest_<appid>.acf`)](#5-the-steam-acf-manifest-contract)
6. [AT0-M / Vapor Global Settings](#6-at0-m--vapor-global-settings)
7. [Best Architecture for Pluto to Manage ASSella Games](#7-best-architecture-for-pluto-to-manage-assella-games)
8. [SLSsteam & Bridge Integration (`assella_bridge.lua`)](#8-slssteam--bridge-integration)
9. [Implementation Blueprint for Pluto (C# Services)](#9-implementation-blueprint-for-pluto)
10. [Critical Gotchas & Guardrails](#10-critical-gotchas--guardrails)

---

## 1. Ecosystem Architecture & Data Layout

```
                  ┌──────────────────────────────────────────────┐
                  │                 ACCELA / ASSella             │
                  │   - Discovers Steam libraries                │
                  │   - Downloads manifests & depot chunks       │
                  │   - Maintains databases & games_cache.json   │
                  └───────────────────────┬──────────────────────┘
                                          │
                  ┌───────────────────────▼──────────────────────┐
                  │       Shared Local File System State         │
                  │   • ~/.local/share/ACCELA/db/                │
                  │   • ~/.config/SLSsteam/config.yaml           │
                  │   • Steam library common/ & steamapps/       │
                  └───────────────────────▲──────────────────────┘
                                          │
                  ┌───────────────────────┴──────────────────────┐
                  │                    PLUTO                     │
                  │   - Fast C# .NET 10 / Avalonia Launcher      │
                  │   - SDL2 Gamepad First (Deck/TV)             │
                  │   - Real-time file & IPC integration         │
                  └──────────────────────────────────────────────┘
```

### Core Paths & Files

| Component | Path | Description |
| :--- | :--- | :--- |
| **Plugin Library DB** | `~/.local/share/ACCELA/db/plugin_library.json` | JSON dictionary of games registered for AT0-M / SLS plugins. |
| **ACCELA Games Cache**| `~/.local/share/ACCELA/db/games_cache.json` | Pre-parsed snapshot of all discovered games, paths, flags, and build IDs. |
| **Depot Keys SQLite** | `~/.local/share/ACCELA/db/depot_keys.db` | SQLite database storing AES decryption keys by AppID / DepotID. |
| **Steam Libraries VDF**| `~/.local/share/Steam/steamapps/libraryfolders.vdf` | Steam's official registry of all mounted library paths (internal, microSD, external drives). |
| **ACCELA / AT0-M Settings**| `~/.config/Tachibana Labs/ACCELA.conf` | INI configuration file storing AT0-M/Vapor settings, download behavior, and UI preferences. |
| **SLSsteam Config** | `~/.config/SLSsteam/config.yaml` | Active SLS configuration containing `AdditionalApps`, `AdditionalDepots`, and `DecryptionKeys`. |
| **SLSsteam Plugins** | `~/.config/SLSsteam/plugins/` | Location of Lua plugins, including `assella_bridge.lua`. |
| **SLSsteam API Pipe** | `/tmp/SLSsteam.API` | Named FIFO / text pipe used to send commands directly to SLSsteam (e.g., `reloadlua`). |
| **Bridge IPC Socket** | `/tmp/assella_ipc.sock` | UNIX domain stream socket for two-way communication with `assella_bridge.lua`. |
| **Bridge Fallback File**| `/tmp/assella_cmd.json` | One-shot JSON command file polled by `assella_bridge.lua` if socket is not bound. |

---

## 2. Game Operating Modes

Every game installed in a Steam library on the system falls into one of three states:

```
┌────────────────────────────────────────────────────────────────────────┐
│ 1. ACCELA Managed                                                      │
│    • Folder has marker: <game_path>/.ACCELA or .DepotDownloader        │
│    • Manifests & updates handled manually by ACCELA downloader         │
│    • SLSsteam config does not strictly require the AppID               │
├────────────────────────────────────────────────────────────────────────┤
│ 2. AT0-M / Plugin Native (Vapor)                                       │
│    • Marker folders (.ACCELA / .DepotDownloader) are REMOVED           │
│    • Registered in ~/.local/share/ACCELA/db/plugin_library.json        │
│    • AppID in SLS config AdditionalApps                                │
│    • Depots in SLS config AdditionalDepots                             │
│    • Decryption keys in SLS config DecryptionKeys                      │
│    • appmanifest_<appid>.acf has valid InstalledDepots & SizeOnDisk    │
│    • Steam Native Client updates & verifies files directly             │
├────────────────────────────────────────────────────────────────────────┤
│ 3. Legitimate / Vanilla Steam Owned                                    │
│    • Owned on the active Steam account (loginusers.vdf)                │
│    • No markers, not in AdditionalApps                                 │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 3. How to Discover & Read ACCELA Games

Pluto can read ACCELA-managed games via two approaches: **Fast Cache Read** (instant) or **Direct Library Scan** (standalone).

### Method A: Fast Cache Read (`games_cache.json`)

ACCELA maintains `~/.local/share/ACCELA/db/games_cache.json`. This is the fastest way for Pluto to load all games without disk-crawling:

```json
{
  "version": 1,
  "saved_at": 1790290441.9,
  "games": [
    {
      "appid": "1966900",
      "game_name": "20 Minutes Till Dawn",
      "install_dir": "20MinuteTillDawn",
      "install_path": "/home/aiwin/.local/share/Steam/steamapps/common/20MinuteTillDawn",
      "library_path": "/home/aiwin/.local/share/Steam",
      "size_on_disk": 49525600,
      "source": "ACCELA",
      "is_accela_install": true,
      "is_vapor": false,
      "is_atom": false,
      "is_plugin_game": false,
      "appmanifest_path": "/home/aiwin/.local/share/Steam/steamapps/appmanifest_1966900.acf",
      "buildid": "12679152"
    }
  ]
}
```

- If `is_accela_install == true` and `is_atom == false` / `is_vapor == false`: **ACCELA Managed**.
- If `is_atom == true` or `is_vapor == true` or `source == "at0-m"`: **AT0-M Plugin Mode**.

### Method B: Standalone Steam Library Scanning

If Pluto needs to run independently without depending on `games_cache.json`:

1. **Find Library Paths**: Parse `~/.local/share/Steam/steamapps/libraryfolders.vdf`. Each library block contains `"path" "/path/to/library"`.
2. **Scan Common Folders**: In each `<library_path>/steamapps/common/`:
   - Iterate through every subdirectory `<game_path>`.
   - Check if `<game_path>` contains marker folders:
     - `.ACCELA`
     - `.accela`
     - `.DepotDownloader`
     - `.depotdownloader`
3. **Match with ACF**: Look for `<library_path>/steamapps/appmanifest_<appid>.acf`.
   - Read `"appid"`, `"name"`, `"installdir"`, and `"buildid"`.

---

## 4. Mode Transition Protocols

### A. Moving ACCELA Managed ➔ AT0-M (Plugin Native)

When moving a game to AT0-M, you convert it so that **Steam treats it as natively installed and updatable via SLSsteam**.

#### Step 1: Remove Marker Folders
Recursively delete any of the following inside `<game_path>`:
- `.ACCELA`
- `.accela`
- `.DepotDownloader`
- `.depotdownloader`

#### Step 2: Resolve Depots & Decryption Keys
Retrieve the known Depots and AES Decryption Keys for the AppID:
1. First check `depot_keys.db` (SQLite at `~/.local/share/ACCELA/db/depot_keys.db`):
   ```sql
   SELECT depot_id, decryption_key FROM depot_keys WHERE app_id = ?;
   ```
2. Or read them from `games_cache.json` / `manifests`.

#### Step 3: Register in `plugin_library.json`
Write/Update the record in `~/.local/share/ACCELA/db/plugin_library.json`:
```json
{
  "2756920": {
    "appid": "2756920",
    "name": "Keep Driving",
    "installdir": "Keep Driving",
    "depots": ["2756920", "2756921"],
    "keys": {
      "2756921": "6c44243b...0f4"
    },
    "depot_names": {
      "2756921": "Keep Driving Content"
    },
    "source": "plugin_native",
    "updated_at": 1790290500
  }
}
```

#### Step 4: In-Place Update of SLSsteam `config.yaml`
Preserve the file inode! Append or merge:
- `AdditionalApps`: add `- 2756920`
- `AdditionalDepots`: add `- 2756920` and `- 2756921`
- `DecryptionKeys`: add `2756921: 6c44243b...0f4`

#### Step 5: Update the Steam ACF Manifest
*(See Section 5 for details)* Ensure `appmanifest_<appid>.acf` has:
- `InstalledDepots` block populated with manifest IDs.
- `SizeOnDisk` calculated from actual folder size on disk.

#### Step 6: Reload SLSsteam
Send the reload command:
```bash
echo "reloadlua" > /tmp/SLSsteam.API
```

---

### B. Moving AT0-M ➔ ACCELA Managed

When reverting a game back to ACCELA management:

#### Step 1: Re-create ACCELA Marker Folder
```bash
mkdir -p "<game_path>/.ACCELA"
```

#### Step 2: Unregister from `plugin_library.json`
Remove the key `"<appid>"` from `~/.local/share/ACCELA/db/plugin_library.json` and save.

#### Step 3: Clean up SLSsteam `config.yaml`
- Remove `- <appid>` from `AdditionalApps`.
- For each depot and key in the game: if **no other game** in `plugin_library.json` requires that depot ID, remove it from `AdditionalDepots` and `DecryptionKeys`.
- Write `config.yaml` in-place.

#### Step 4: Reload SLSsteam
```bash
echo "reloadlua" > /tmp/SLSsteam.API
```

---

## 5. The Steam ACF Manifest Contract

Steam Native mode will **silently fail** to verify, run, or update a game if its `appmanifest_<appid>.acf` is empty or malformed.

### Manifest File Format (Valve VDF)
Path: `<library_path>/steamapps/appmanifest_<appid>.acf`

```vdf
"AppState"
{
	"appid"		"2756920"
	"Universe"		"1"
	"name"		"Keep Driving"
	"StateFlags"		"4"
	"installdir"		"Keep Driving"
	"LastUpdated"		"0"
	"SizeOnDisk"		"2736302772"
	"buildid"		"21425122"
	"LastOwner"		"76561199083839651"
	"DownloadType"		"1"
	"TargetBuildID"		"21425122"
	"InstalledDepots"
	{
		"2756921"
		{
			"manifest"		"5790425083458455485"
			"size"		"2736302772"
		}
	}
	"UserConfig"
	{
	}
	"MountedConfig"
	{
	}
}
```

### Essential Rules for ACF Manipulation & SizeOnDisk Clarification
1. **For SLSsteam Injection (`config.yaml`)**:
   - `SizeOnDisk` is **not needed** by SLSsteam at all.
   - Cleanly injecting `AdditionalApps`, `AdditionalDepots`, and `DecryptionKeys` into `~/.config/SLSsteam/config.yaml` and issuing `reloadlua` is 100% of what SLSsteam needs to unlock licenses and allow Steam to run or download the game.
2. **For Steam Client (`appmanifest_<appid>.acf`)**:
   - `StateFlags` should be `"4"` (`STATE_FULLY_INSTALLED`).
   - `InstalledDepots` should ideally be present so Steam recognizes existing depots.
   - `SizeOnDisk`: Pluto does **not** need to crawl the filesystem to calculate `SizeOnDisk`. If it's `"0"` or left as-is, Steam's client automatically re-evaluates the folder on verification or ASSella calculates it asynchronously in the background. Clean `config.yaml` injection is the true priority.

---

## 6. AT0-M / Vapor Global Settings

All global settings configured in ASSella's **Settings ➔ AT0-M** tab are stored in the user's Qt configuration file:

**File Location:**  
`~/.config/Tachibana Labs/ACCELA.conf`

### Configuration Keys & Values

```ini
[General]
# Enable / disable AT0-M integration globally
enable_vapor=true
enable_at0m=true

# Download behavior for AT0-M games:
# "native"  = Native Steam client handles downloads and updates
# "ask"     = Prompt the user each time
# "accela"  = Use ACCELA's DepotDownloader engine
vapor_default_download_action=native

# Prevent Steam client from auto-updating AT0-M games
vapor_disable_updates=false
at0m_disable_updates=false

# Whether to prompt with the depot checklist before initiating download
vapor_show_depot_checklist=true

# Start download behavior: "immediate" or "queued"
vapor_start_download_action=immediate
vapor_start_download_immediately=true

# Use native Steam download handoff
use_native_steam_download=true
```

Pluto can read this `.conf` file (standard INI format) on startup to align its behavior with the user's preferences (e.g. knowing whether native downloads or auto-updates are preferred).

---

## 7. Best Architecture for Pluto to Manage ASSella Games

To let Pluto seamlessly manage both **ACCELA-managed games** and **AT0-M plugin games**, adopt this dual-source pattern:

### 1. Dual-Source Library Loading
In Pluto's `PluginLibraryService` (or a unified `GameLibraryService`):
1. **Load AT0-M games** from `~/.local/share/ACCELA/db/plugin_library.json`.
2. **Load all ACCELA / Steam games** from `~/.local/share/ACCELA/db/games_cache.json`.
3. Merge them into a single list of `PlutoGame` models:
   - If `is_atom == true` or `is_vapor == true`: Mode is `[AT0-M]`.
   - If `is_accela_install == true` and not in `plugin_library.json`: Mode is `[ACCELA]`.
   - If purely owned on Steam: Mode is `[STEAM]`.

### 2. FileSystemWatchers on Both Sources
Set up `FileSystemWatcher` on:
- `~/.local/share/ACCELA/db/plugin_library.json`
- `~/.local/share/ACCELA/db/games_cache.json`

Any time ASSella installs a game or updates its cache, Pluto's list refreshes automatically!

### 3. One-Click Mode Toggle (`[X]` Button on Gamepad)
When a user selects a game in Pluto and presses `[X]` (or clicks "Toggle Mode"):
- **If currently `[ACCELA]` ➔ Convert to `[AT0-M]`**:
  1. Delete marker `<game_path>/.ACCELA` (or `.DepotDownloader`).
  2. Add AppID, Depots, and Keys into `~/.config/SLSsteam/config.yaml` (in-place).
  3. Add entry to `~/.local/share/ACCELA/db/plugin_library.json`.
  4. Send `reloadlua\n` to `/tmp/SLSsteam.API`.
- **If currently `[AT0-M]` ➔ Convert to `[ACCELA]`**:
  1. Create `<game_path>/.ACCELA`.
  2. Remove from `plugin_library.json`.
  3. Remove AppID from `config.yaml` (in-place).
  4. Send `reloadlua\n` to `/tmp/SLSsteam.API`.


## 8. SLSsteam & Bridge Integration

SLSsteam hooks into the Steam client process and controls license emulation and depot unlocking.

### Communication Channels

```
Pluto / ASSella
   │
   ├─► Write In-Place: ~/.config/SLSsteam/config.yaml (inotify)
   ├─► Pipe:           /tmp/SLSsteam.API ("reloadlua\n")
   └─► UNIX Socket:    /tmp/assella_ipc.sock (Two-way IPC)
                               ▲
                               │
                       assella_bridge.lua
                       (inside SLSsteam)
```

### 1. The Named Pipe (`/tmp/SLSsteam.API`)
Whenever you update `config.yaml` or want SLSsteam to reload Lua scripts, write the command followed by a newline:
```csharp
File.WriteAllText("/tmp/SLSsteam.API", "reloadlua\n");
```
Supported API pipe commands:
- `reloadlua` — Re-executes all Lua plugins in `~/.config/SLSsteam/plugins/`.
- `install <appid>` — Emulates Steam game install.
- `uninstall <appid>` — Emulates Steam game uninstall.

### 2. The Lua Bridge (`assella_bridge.lua`)
Located at `~/.config/SLSsteam/plugins/assella_bridge.lua`.
- **Dynamic Depot Injection**: Instead of editing `config.yaml`, the bridge supports injecting depots at runtime:
  ```json
  {"cmd": "add_depot", "depot_id": 2756921}
  ```
- **Dynamic Decryption Key Injection**:
  ```json
  {"cmd": "add_key", "depot_id": 2756921, "key": "6c44243b...0f4"}
  ```
- **Reload Trigger**:
  ```json
  {"cmd": "reload_config"}
  ```

### 3. IPC Server Implementation
If Pluto opens a UNIX domain socket at `/tmp/assella_ipc.sock`, `assella_bridge.lua` will automatically send JSON events upon connection:
```json
{
  "event": "pluginLoaded",
  "steam_active": true,
  "downloader_loaded": true,
  "depots_count": 14,
  "keys_count": 8
}
```
If Pluto does not run an IPC server, Pluto can simply write commands to `/tmp/assella_cmd.json`, which the bridge reads and unlinks when SLS triggers a reload.

---

## 9. Implementation Blueprint for Pluto

Below are ready-to-use C# service implementations for Pluto to handle ACCELA discovery, ACF repairs, and mode transitions.

### A. Steam ACF Helper Service
```csharp
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Pluto.Services;

public static class SteamAcfService
{
    public static void EnsureAcfDepots(string acfPath, string appid, string[] depotIds, string[] manifests, long sizeOnDisk)
    {
        if (!File.Exists(acfPath)) return;

        var content = File.ReadAllText(acfPath);

        // Check if InstalledDepots already populated
        bool hasDepots = Regex.IsMatch(content, @"\""InstalledDepots\""\s*\{[^}]*\""\d+\""", RegexOptions.Singleline);
        bool needsSize = Regex.IsMatch(content, @"\""SizeOnDisk\""\s*\""0\""");

        if (hasDepots && !needsSize) return;

        // Build InstalledDepots block
        if (!hasDepots && depotIds.Length > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("\t\"InstalledDepots\"");
            sb.AppendLine("\t{");
            for (int i = 0; i < depotIds.Length; i++)
            {
                var did = depotIds[i];
                if (did == appid) continue;
                var mgid = (i < manifests.Length && !string.IsNullOrEmpty(manifests[i])) ? manifests[i] : "0";
                long dSize = depotIds.Length == 1 ? sizeOnDisk : (sizeOnDisk / depotIds.Length);

                sb.AppendLine($"\t\t\"{did}\"");
                sb.AppendLine("\t\t{");
                sb.AppendLine($"\t\t\t\"manifest\"\t\t\"{mgid}\"");
                sb.AppendLine($"\t\t\t\"size\"\t\t\"{dSize}\"");
                sb.AppendLine("\t\t}");
            }
            sb.Append("\t}");

            content = Regex.Replace(content, @"\""InstalledDepots\""\s*\{\s*\}", sb.ToString());
        }

        if (needsSize && sizeOnDisk > 0)
        {
            content = Regex.Replace(content, @"\""SizeOnDisk\""\s*\""\d+\""", $"\"SizeOnDisk\"\t\t\"{sizeOnDisk}\"");
        }

        File.WriteAllText(acfPath, content);
    }
}
```

### B. Mode Transition Service
```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using Pluto.Models;

namespace Pluto.Services;

public class GameTransitionService
{
    private readonly SlsSteamService _slsService;
    private readonly PluginLibraryService _pluginService;

    public GameTransitionService(SlsSteamService slsService, PluginLibraryService pluginService)
    {
        _slsService = slsService;
        _pluginService = pluginService;
    }

    public async Task<bool> ConvertToAtomPluginAsync(PluginGame game, string gameInstallPath, string acfPath)
    {
        // 1. Remove ACCELA markers
        string[] markers = { ".ACCELA", ".accela", ".DepotDownloader", ".depotdownloader" };
        foreach (var m in markers)
        {
            var p = Path.Combine(gameInstallPath, m);
            if (Directory.Exists(p)) Directory.Delete(p, true);
            else if (File.Exists(p)) File.Delete(p);
        }

        // 2. Sync to SLS config.yaml (in-place)
        await _slsService.SyncGameToConfigAsync(game);

        // 3. Ensure appmanifest has InstalledDepots
        long size = CalculateDirectorySize(gameInstallPath);
        SteamAcfService.EnsureAcfDepots(acfPath, game.AppId, game.Depots.ToArray(), Array.Empty<string>(), size);

        // 4. Reload SLS
        _slsService.NotifyReload();
        return true;
    }

    public async Task<bool> ConvertToAccelaManagedAsync(PluginGame game, string gameInstallPath)
    {
        // 1. Create .ACCELA marker
        var markerPath = Path.Combine(gameInstallPath, ".ACCELA");
        Directory.CreateDirectory(markerPath);

        // 2. Remove from SLS config
        var allGames = await _pluginService.LoadGamesAsync();
        await _slsService.RemoveGameFromConfigAsync(game, allGames);

        // 3. Reload SLS
        _slsService.NotifyReload();
        return true;
    }

    private static long CalculateDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { }
        }
        return total;
    }
}
```

---

## 10. Critical Gotchas & Guardrails

1. **Inode Preservation on `~/.config/SLSsteam/config.yaml`**:
   - SLSsteam uses Linux `inotify` on `config.yaml`.
   - If an application uses `File.WriteAllText(temp) + File.Move(temp, target, overwrite=true)`, the file inode changes. SLSsteam's inotify watcher will **permanently disconnect** until SLSsteam is restarted.
   - **Rule**: Always open the existing file with `FileMode.Open` / `FileAccess.Write` and truncate in-place (as implemented in `SlsSteamService.WriteInPlaceAsync`).

2. **Lua FFI Type Redefinition**:
   - SLSsteam reloads Lua plugins inside the **same persistent LuaJIT state**.
   - Any FFI C-declarations must be guarded with `pcall(ffi.cdef, [[ ... ]])`.
   - This was patched in `assella_bridge.lua`, making `reloadlua` calls 100% safe.

3. **Steam Client State Flags**:
   - Never set `StateFlags` to `"0"` in `appmanifest_<appid>.acf`. Keep `"StateFlags" "4"` so Steam treats the game as fully installed.

4. **Multi-Library Support**:
   - Never assume games are only installed in `~/.local/share/Steam`. Games on Steam Deck are almost always on microSD cards (`/run/media/mmcblk0p1/...` or `/run/media/aiwin/SDCARD/...`).
   - Always read `appmanifest_path` and `install_path` dynamically from `games_cache.json` or `libraryfolders.vdf`.
