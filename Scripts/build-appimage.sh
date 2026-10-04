#!/usr/bin/env bash
#
# Builds Pluto as an AppImage.
#
# Layout note: this publishes self-contained but deliberately NOT single-file.
# Pluto loads libSDL2, libSkiaSharp, libHarfBuzzSharp and libe_sqlite3 by name
# through the dynamic loader. Bundling those into a single self-extracting
# executable puts them somewhere the loader does not search, which silently
# breaks controller support. Keeping the directory layout intact and letting
# appimagetool pack the folder keeps every native library resolvable.
#
# Usage:
#   Scripts/build-appimage.sh [--runtime static|none] [--out DIR] [--no-upload-check]
#
# Environment:
#   APPIMAGETOOL   path to an existing appimagetool binary (skips download)
#   PLUTO_VERSION  overrides the version stamped into the filename

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

BUILD_DIR="$REPO_ROOT/build/appimage"
APPDIR="$BUILD_DIR/Pluto.AppDir"
OUT_DIR="$REPO_ROOT/dist"
RUNTIME="static"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --runtime) RUNTIME="${2:?--runtime needs static or none}"; shift 2 ;;
        --out)     OUT_DIR="${2:?--out needs a directory}"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

log()  { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m warn:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

command -v dotnet >/dev/null || die "dotnet not found"
[[ -f "$REPO_ROOT/Assets/pluto-256.png" ]] || die "Assets/pluto-256.png missing - run: python3 Scripts/make_icon.py"

# ── Version ────────────────────────────────────────────────────────────────
# Follows the repo rule (0.0.8-xxday+month+26) so the artifact name matches what
# the app reports. Base version and build counter come from PlutoVersion.cs; the
# date is the build date, mirroring how the app derives it from the assembly.
if [[ -n "${PLUTO_VERSION:-}" ]]; then
    VERSION_LINE="$PLUTO_VERSION"
else
    BASE_VERSION="$(grep -oP 'BaseVersion\s*=\s*"\K[^"]+' Services/PlutoVersion.cs | head -1)"
    BUILD_NUMBER="$(grep -oP 'BuildNumber\s*=\s*\K[0-9]+' Services/PlutoVersion.cs | head -1)"
    BASE_VERSION="${BASE_VERSION:-0.0.8}"
    BUILD_NUMBER="${BUILD_NUMBER:-1}"

    read -r Y M D <<<"$(date '+%Y %m %d')"
    VERSION_LINE="$(printf '%s-%02d%02d+%02d+%s' \
        "$BASE_VERSION" "$BUILD_NUMBER" "$((10#$D))" "$((10#$M))" "${Y:2:2}")"
fi

APPIMAGE_NAME="Pluto-${VERSION_LINE}-x86_64.AppImage"

log "building $APPIMAGE_NAME"

# ── Publish ────────────────────────────────────────────────────────────────
rm -rf "$BUILD_DIR"
mkdir -p "$APPDIR" "$OUT_DIR"

log "dotnet publish (self-contained, framework-dependent layout)"
dotnet publish -c Release -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    -p:SatelliteResourceLanguages=en \
    -o "$APPDIR/usr/bin"

# The bundled DepotDownloaderMod and its dependencies ride along via the
# csproj CopyToOutputDirectory rule; verify rather than assume.
if [[ -f "$APPDIR/usr/bin/Engine/DepotDownloader/Bin/DepotDownloader.dll" ]]; then
    log "DepotDownloaderMod bundled"
else
    die "DepotDownloader.dll missing from publish output"
fi

for lib in libSDL2.so libSkiaSharp.so libe_sqlite3.so; do
    [[ -f "$APPDIR/usr/bin/$lib" ]] || die "expected native library missing: $lib"
done

# ── Desktop entry ──────────────────────────────────────────────────────────
ICON_SIZE=$(identify -format "%wx%h" "$REPO_ROOT/Assets/pluto-256.png" 2>/dev/null || echo "256x256")
install -Dm644 "$REPO_ROOT/Assets/pluto.png" \
    "$APPDIR/usr/share/icons/hicolor/${ICON_SIZE}/apps/pluto.png"
install -Dm644 "$REPO_ROOT/Assets/pluto-256.png" \
    "$APPDIR/pluto.png"

cat > "$APPDIR/pluto.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=Pluto
GenericName=Game Launcher
Comment=Plugin game launcher and management tool for ACCELA / SLSsteam
Exec=Pluto
Icon=pluto
Categories=Game;Utility;
Terminal=false
StartupWMClass=Pluto
DESKTOP

# ── AppRun ─────────────────────────────────────────────────────────────────
# Sets up the runtime environment then hands off to the real binary. Kept as a
# plain sh script so the AppImage works with a static runtime and no FUSE.
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
# AppImage entry point for Pluto.
set -eu
HERE="$(dirname "$(readlink -f "$0")")"

# Keep gamepad access working when the image runs without a udev session.
export SDL_VIDEODRIVER="${SDL_VIDEODRIVER:-x11}"

exec "$HERE/usr/bin/Pluto" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# ── appimagetool ───────────────────────────────────────────────────────────
TOOL="${APPIMAGETOOL:-}"
if [[ -z "$TOOL" ]]; then
    TOOL="$BUILD_DIR/appimagetool"
    if [[ ! -x "$TOOL" ]]; then
        log "downloading appimagetool"
        curl -fsSL -o "$TOOL" \
            "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage" \
            || die "could not download appimagetool (set APPIMAGETOOL=/path/to/tool)"
        chmod +x "$TOOL"
    fi
fi

# The FUSE-free runtime is a *separate* asset from appimagetool. Passing
# appimagetool itself as --runtime-file embeds the packaging tool rather than
# the runtime, and the resulting image refuses to start with "SOURCE is missing".
RUNTIME_ARG=()
if [[ "$RUNTIME" == "static" ]]; then
    RUNTIME_BIN="$BUILD_DIR/runtime-x86_64"
    if [[ ! -f "$RUNTIME_BIN" ]]; then
        log "downloading the static (FUSE-free) runtime"
        curl -fsSL -o "$RUNTIME_BIN" \
            "https://github.com/AppImage/AppImageKit/releases/download/continuous/runtime-x86_64" \
            || die "could not download the AppImage runtime"
        chmod +x "$RUNTIME_BIN"
    fi
    log "embedding the static runtime (image runs without FUSE)"
    RUNTIME_ARG=(--runtime-file "$RUNTIME_BIN")
elif [[ "$RUNTIME" != "none" ]]; then
    die "--runtime must be 'static' or 'none'"
fi

log "packing"
# appimagetool itself needs FUSE on many systems; fall back to extracting it.
if ! "$TOOL" "${RUNTIME_ARG[@]}" "$APPDIR" "$OUT_DIR/$APPIMAGE_NAME" 2>/dev/null; then
    warn "direct run failed (usually missing fuse2), retrying via --appimage-extract-and-run"
    "$TOOL" --appimage-extract-and-run "${RUNTIME_ARG[@]}" \
        "$APPDIR" "$OUT_DIR/$APPIMAGE_NAME"
fi

chmod +x "$OUT_DIR/$APPIMAGE_NAME"

SIZE=$(du -h "$OUT_DIR/$APPIMAGE_NAME" | cut -f1)
log "done: $OUT_DIR/$APPIMAGE_NAME ($SIZE)"

cat <<'NOTES'

Running it
----------
  ./Pluto-<version>-x86_64.AppImage

On a system without fuse2 (some SteamOS and NixOS setups):

  ./Pluto-<version>-x86_64.AppImage --appimage-extract-and-run
  # or, if you already use one for ASSella:
  appimage-run ./Pluto-<version>-x86_64.AppImage

Note: Pluto reads and writes your real library and SLSsteam config
(~/.config and ~/.local/share), so an AppImage run modifies live state.
Back up ~/.config/SLSsteam/config.yaml before experimenting.
NOTES