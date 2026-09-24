import configparser
import os
import sys
from pathlib import Path
from typing import Any

CONF_DIR = Path.home() / ".config" / "Tachibana Labs"
CONF_FILE = CONF_DIR / "ACCELA.conf"


class RobustSettings:
    """ConfigParser-backed QSettings replacement for Pluto.
    Reads and writes ~/.config/Tachibana Labs/ACCELA.conf seamlessly.
    """

    def __init__(self):
        self.conf_path = CONF_FILE
        self.cp = configparser.ConfigParser(interpolation=None)
        self._load()

    def _load(self):
        if self.conf_path.exists():
            try:
                self.cp.read(self.conf_path, encoding="utf-8")
            except Exception:
                pass

    def value(self, key: str, defaultValue: Any = None, type: Any = None) -> Any:
        self._load()
        val = None
        for section in ("General", "DEFAULT"):
            if self.cp.has_option(section, key):
                val = self.cp.get(section, key)
                break

        if val is None:
            return defaultValue

        if type is bool or isinstance(defaultValue, bool):
            return str(val).lower() in ("true", "1", "yes", "on")

        if type is int or (type is None and isinstance(defaultValue, int)):
            try:
                return int(val)
            except (ValueError, TypeError):
                return defaultValue

        return val

    def setValue(self, key: str, value: Any) -> None:
        self._load()
        if not self.cp.has_section("General"):
            self.cp.add_section("General")

        if isinstance(value, bool):
            val_str = "true" if value else "false"
        else:
            val_str = str(value)

        self.cp.set("General", key, val_str)
        self.sync()

    def sync(self) -> None:
        try:
            self.conf_path.parent.mkdir(parents=True, exist_ok=True)
            with open(self.conf_path, "w", encoding="utf-8") as f:
                self.cp.write(f)
        except Exception as e:
            sys.stderr.write(f"[Settings] Error writing {self.conf_path}: {e}\n")


_instance = None


def get_settings() -> RobustSettings:
    global _instance
    if _instance is None:
        _instance = RobustSettings()
    return _instance
