#!/usr/bin/env python3
"""
Pluto SLS & AT0-M Management CLI
Shared backend utility bridging Pluto (C# Avalonia) with ASSella's
battle-tested SLSsteam config manipulation and AT0-M plugin management.
"""

import argparse
import json
import os
import shutil
import sqlite3
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

# Ensure local script directory is on sys.path
SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import helpers
import plugin_games
import settings
import sls_bridge
import yaml_config_manager


def get_games_cache_path() -> Path:
    return helpers.get_data_file_path("games_cache.json")


def load_games_cache() -> List[Dict[str, Any]]:
    p = get_games_cache_path()
    if not p.exists():
        return []
    try:
        with open(p, "r", encoding="utf-8") as f:
            data = json.load(f)
            return data.get("games", [])
    except Exception as e:
        sys.stderr.write(f"[pluto_sls] Error reading games cache: {e}\n")
        return []


def query_depot_keys(appid: str) -> Dict[str, str]:
    db_path = helpers.get_data_file_path("depot_keys.db")
    keys = {}
    if db_path.exists():
        try:
            conn = sqlite3.connect(str(db_path))
            cur = conn.cursor()
            cur.execute("SELECT depot_id, aes_key FROM depot_keys WHERE appid = ?", (str(appid),))
            for did, k in cur.fetchall():
                if did and k:
                    keys[str(did)] = str(k).strip()
            conn.close()
        except Exception as e:
            sys.stderr.write(f"[pluto_sls] Error reading depot_keys.db: {e}\n")
    return keys


def cmd_list_all(args):
    plugins = plugin_games.load_plugin_library()
    cache = load_games_cache()

    seen_appids = set()
    result = []

    # 1. Add all plugin games first
    for appid, pdata in plugins.items():
        appid_str = str(appid)
        seen_appids.add(appid_str)
        result.append({
            "appid": appid_str,
            "name": pdata.get("name", f"App {appid_str}"),
            "installdir": pdata.get("installdir", ""),
            "mode": "at0m",
            "is_atom": True,
            "is_accela": False,
            "depots": pdata.get("depots", []),
            "keys": pdata.get("keys", {}),
            "depot_names": pdata.get("depot_names", {}),
            "source": pdata.get("source", "at0-m"),
            "updated_at": pdata.get("updated_at", 0),
        })

    # 2. Add ACCELA-managed games from cache not already in plugins
    for g in cache:
        aid = str(g.get("appid", ""))
        if not aid or aid in seen_appids:
            continue
        is_atom = g.get("is_atom", False) or g.get("is_vapor", False) or g.get("source") == "at0-m"
        is_accela = g.get("is_accela_install", False)
        if not (is_atom or is_accela):
            continue

        seen_appids.add(aid)
        result.append({
            "appid": aid,
            "name": g.get("game_name", f"App {aid}"),
            "installdir": g.get("install_dir", ""),
            "mode": "at0m" if is_atom else "accela",
            "is_atom": is_atom,
            "is_accela": is_accela,
            "install_path": g.get("install_path", ""),
            "appmanifest_path": g.get("appmanifest_path", ""),
            "depots": [],
            "keys": {},
            "depot_names": {},
            "source": "at0-m" if is_atom else "ACCELA",
            "updated_at": g.get("last_updated", 0),
        })

    print(json.dumps(result, indent=2))


def cmd_list_plugins(args):
    plugins = plugin_games.load_plugin_library()
    print(json.dumps(plugins, indent=2))


def cmd_to_atom(args):
    appid = str(args.appid)
    plugins = plugin_games.load_plugin_library()
    cache = load_games_cache()

    # Find game info from cache
    game_entry = None
    for g in cache:
        if str(g.get("appid")) == appid:
            game_entry = g
            break

    name = args.name or (game_entry.get("game_name") if game_entry else f"App {appid}")
    installdir = args.installdir or (game_entry.get("install_dir") if game_entry else name)
    install_path = args.install_path or (game_entry.get("install_path") if game_entry else "")

    # Remove markers if install_path exists
    if install_path and os.path.isdir(install_path):
        for m in (".ACCELA", ".accela", ".DepotDownloader", ".depotdownloader"):
            mp = os.path.join(install_path, m)
            if os.path.isdir(mp):
                shutil.rmtree(mp, ignore_errors=True)
            elif os.path.isfile(mp):
                try:
                    os.remove(mp)
                except Exception:
                    pass

    # Find depots & keys
    keys = query_depot_keys(appid)
    depots = list(keys.keys()) if keys else [appid]

    # Register into plugin_library and update config.yaml
    ok = plugin_games.register_plugin_game(
        appid=appid,
        name=name,
        installdir=installdir,
        depot_ids=depots,
        decryption_keys=keys,
        depot_names={},
    )

    # Signal reload
    sls_bridge.SLSBridge.notify_reload()

    print(json.dumps({
        "success": ok,
        "appid": appid,
        "name": name,
        "mode": "at0m",
        "depots_count": len(depots),
        "keys_count": len(keys),
    }))


def cmd_to_accela(args):
    appid = str(args.appid)
    cache = load_games_cache()

    # Find game info from cache
    game_entry = None
    for g in cache:
        if str(g.get("appid")) == appid:
            game_entry = g
            break

    install_path = args.install_path or (game_entry.get("install_path") if game_entry else "")

    # Restore .ACCELA marker
    if install_path and os.path.isdir(install_path):
        m = os.path.join(install_path, ".ACCELA")
        try:
            os.makedirs(m, exist_ok=True)
        except Exception:
            pass

    # Unregister from plugin_library and update config.yaml
    ok = plugin_games.unregister_plugin_game(appid)

    # Signal reload
    sls_bridge.SLSBridge.notify_reload()

    print(json.dumps({
        "success": ok,
        "appid": appid,
        "mode": "accela",
    }))


def cmd_sync_yaml(args):
    appid = str(args.appid)
    plugins = plugin_games.load_plugin_library()
    pdata = plugins.get(appid, {})
    name = pdata.get("name") or f"App {appid}"
    depots = pdata.get("depots", [])
    keys = pdata.get("keys", {})
    depot_names = pdata.get("depot_names", {})

    cfg = yaml_config_manager.get_user_config_path()
    yaml_config_manager.add_additional_app(cfg, appid, name)
    for did in depots:
        dname = depot_names.get(str(did), "")
        comment = f"{name} - {dname} ({did})" if dname else f"{name} ({did})"
        yaml_config_manager.add_additional_depot(cfg, did, comment)
    for did, k in keys.items():
        dname = depot_names.get(str(did), "")
        comment = f"{name} - {dname}" if dname else name
        yaml_config_manager.add_decryption_key(cfg, did, k, comment)

    sls_bridge.SLSBridge.notify_reload()
    print(json.dumps({"success": True, "appid": appid}))


def cmd_reload(args):
    ok = sls_bridge.SLSBridge.notify_reload()
    print(json.dumps({"success": ok}))


def cmd_get_settings(args):
    s = settings.get_settings()
    d = {}
    for opt in s.cp.options("General") if s.cp.has_section("General") else []:
        d[opt] = s.value(opt)
    print(json.dumps(d, indent=2))


def cmd_set_setting(args):
    s = settings.get_settings()
    s.setValue(args.key, args.value)
    print(json.dumps({"success": True, "key": args.key, "value": args.value}))


def main():
    parser = argparse.ArgumentParser(description="Pluto SLS & AT0-M Management CLI")
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("list-all")
    subparsers.add_parser("list-plugins")

    p_to_atom = subparsers.add_parser("to-atom")
    p_to_atom.add_argument("appid", type=str)
    p_to_atom.add_argument("--name", type=str, default="")
    p_to_atom.add_argument("--installdir", type=str, default="")
    p_to_atom.add_argument("--install-path", type=str, default="")

    p_to_accela = subparsers.add_parser("to-accela")
    p_to_accela.add_argument("appid", type=str)
    p_to_accela.add_argument("--install-path", type=str, default="")

    p_sync = subparsers.add_parser("sync-yaml")
    p_sync.add_argument("appid", type=str)

    subparsers.add_parser("reload")
    subparsers.add_parser("get-settings")

    p_set = subparsers.add_parser("set-setting")
    p_set.add_argument("key", type=str)
    p_set.add_argument("value", type=str)

    args = parser.parse_args()

    commands = {
        "list-all": cmd_list_all,
        "list-plugins": cmd_list_plugins,
        "to-atom": cmd_to_atom,
        "to-accela": cmd_to_accela,
        "sync-yaml": cmd_sync_yaml,
        "reload": cmd_reload,
        "get-settings": cmd_get_settings,
        "set-setting": cmd_set_setting,
    }

    fn = commands.get(args.command)
    if fn:
        fn(args)
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
