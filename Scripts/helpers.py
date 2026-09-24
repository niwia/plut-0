import os
import sys
from pathlib import Path


def get_base_path() -> Path:
    """Return base path for ACCELA data."""
    if sys.platform == "win32":
        return Path(os.environ.get("APPDATA", "~")) / "ACCELA"
    return Path.home() / ".local" / "share" / "ACCELA"


def get_data_file_path(filename: str) -> Path:
    """Return path for a database or state file under db/."""
    base = get_base_path()
    db_file = base / "db" / filename
    if db_file.exists():
        return db_file
    legacy_file = base / filename
    if legacy_file.exists():
        return legacy_file
    db_file.parent.mkdir(parents=True, exist_ok=True)
    return db_file
