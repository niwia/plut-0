# PLUT-0 // Plugin Game Launcher & Management Tool

A fast, minimalist C# (.NET 10) launcher and plugin management tool designed for games installed via ACCELA / SLSsteam plugins (`plugin_library.json`). Built from the ground up for handhelds, TV setups, and couch gaming with **first-class SDL2 GameController / Gamepad support**.

---

## Features

- **Controller-First Navigation (SDL2)**:
  - Full hotplugging support for Xbox, PlayStation DualSense/DualShock, Steam Deck, Switch Pro, and standard GameControllers.
  - Analog stick & D-Pad navigation with deadzone compensation and auto-scroll.
  - Dedicated controller actions:
    - `[A]` Confirm & Launch Game (`steam://rungameid/<appid>`)
    - `[X]` Manage Game / View Depots & AES Decryption Keys
    - `[Y]` Focus & filter Search Box
    - `[B]` Cancel / Close Modal / Unfocus Search
    - `[LB / RB]` Fast Page Up / Page Down (5 items)
    - `[Start]` Sync all games to SLSsteam `config.yaml`
- **Minimalist Landscape UI**:
  - Compact landscape window optimized for 16:9 / 16:10 displays (minimum 760x440).
  - High-contrast sleek dark theme with instant responsive search filter.
  - Real-time status indicators showing whether an app is synchronized with SLSsteam's `config.yaml`.
- **ACCELA & SLSsteam Ecosystem Integration**:
  - Live data source: `/home/aiwin/.local/share/ACCELA/db/plugin_library.json`
  - In-place configuration editor for `~/.config/SLSsteam/config.yaml` preserving file inode for Linux `inotify` file-watching.
  - SLSsteam IPC API Pipe integration (`/tmp/SLSsteam.API`) for triggering `reloadlua`, `install`, and `uninstall` commands.
  - File watcher on `plugin_library.json` for seamless live updates when ACCELA adds or modifies games.

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
| **Sync All Games** | **Start** / Menu | Sync All Button |
| **Refresh Library**| Re-scans `plugin_library.json` automatically | `F5` / Refresh Button |

---

## Technical Architecture

```
Pluto/
├── Models/
│   └── PluginGame.cs            # Data model matching ACCELA plugin_library.json
├── Services/
│   ├── PluginLibraryService.cs  # Reads, writes, & watches plugin_library.json
│   ├── SlsSteamService.cs       # In-place SLS config.yaml sync, API pipe, launch
│   └── GamepadService.cs        # SDL2 GameController event loop & hotplugging
├── MainWindow.axaml             # Minimalist landscape UI layout
├── MainWindow.axaml.cs          # Dispatcher, input routing, & view logic
├── App.axaml                    # Dark theme variant & styles
└── Pluto.csproj                 # .NET 10 project definition
```

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
