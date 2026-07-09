#!/usr/bin/env bash
#
# Assemble a Rhino package manager (yak) package for CoRhino.
#
# Why this exists: yak just zips a directory. The directory must contain the
# EXACT set of files Rhino needs to load the plugin — the .rhp plus every runtime
# dependency (web/ UI, cc-plugin/, the flattened native pty lib, Porta.Pty.dll,
# deps.json / runtimeconfig.json). That set is precisely what `dotnet build`
# already stages in the build output dir (and copies to the plugin dir). So we
# reuse that output verbatim instead of re-deriving the file list — matching the
# known-working local install is the safest correctness guarantee.
#
# Native deps are per-OS (mac: libporta_pty.dylib fat binary; win: Vanara*.dll +
# win-x64 native). A package therefore only runs on the OS it was built on, so we
# tag the platform accordingly and this script must be run once per target OS:
#   - macOS  -> produces corhino-<ver>-rh8_0-mac.yak
#   - Windows-> produces corhino-<ver>-rh8_0-win.yak  (run under Git Bash / WSL)
#
# Usage:
#   ./build-yak.sh                 # build Release, package with csproj version
#   ./build-yak.sh 0.1.0           # override the package version
#   YAK=/path/to/yak ./build-yak.sh   # override the yak binary
#
set -euo pipefail

PROJ_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CSPROJ="$PROJ_DIR/src/CoRhino.csproj"
CONFIG="Release"
TFM="net8.0"
BUILD_OUT="$PROJ_DIR/src/bin/$CONFIG/$TFM"
OUT_DIR="$PROJ_DIR/dist/yak"          # dist/ is gitignored
STAGE="$OUT_DIR/stage"
# Package version: explicit arg wins; otherwise read <Version> from the csproj so
# the .yak version exactly matches the assembly and stays a single source of
# truth. (We pass it to `yak build --version` rather than relying on the
# manifest's `$version`, because the SDK's informational version carries a
# `+<gitsha>` build-metadata suffix that would leak into the package version.)
VERSION_OVERRIDE="${1:-}"
if [[ -z "$VERSION_OVERRIDE" ]]; then
  VERSION_OVERRIDE="$(grep -oE '<Version>[^<]+</Version>' "$CSPROJ" | sed -E 's/<\/?Version>//g')"
fi

# --- Resolve yak binary + platform tag per OS ---------------------------------
case "$(uname -s)" in
  Darwin)
    YAK="${YAK:-/Applications/Rhino 8.app/Contents/Resources/bin/yak}"
    PLATFORM="mac"
    ;;
  MINGW*|MSYS*|CYGWIN*|Windows_NT)
    YAK="${YAK:-/c/Program Files/Rhino 8/System/Yak.exe}"
    PLATFORM="win"
    ;;
  *)
    echo "Unsupported OS: $(uname -s)" >&2; exit 1 ;;
esac

if [[ ! -x "$YAK" && ! -f "$YAK" ]]; then
  echo "yak not found at: $YAK" >&2
  echo "Set YAK=/path/to/yak and re-run." >&2
  exit 1
fi

# --- 1. Release build (also runs vite UI build + native flatten) --------------
echo ">> dotnet build ($CONFIG)"
dotnet build "$CSPROJ" -c "$CONFIG"

if [[ ! -f "$BUILD_OUT/CoRhino.rhp" ]]; then
  echo "CoRhino.rhp not found in $BUILD_OUT" >&2; exit 1
fi

# --- 2. Stage a clean copy of the build output --------------------------------
echo ">> staging -> $STAGE"
rm -rf "$STAGE"
mkdir -p "$STAGE"
# Mirror the build output but drop debug symbols and macOS cruft. deps.json /
# runtimeconfig.json are KEPT: CoreCLR needs them to resolve dependencies.
rsync -a \
  --exclude '*.pdb' \
  --exclude '.DS_Store' \
  "$BUILD_OUT"/ "$STAGE"/

# --- 3. Drop in manifest + icon at the package root ---------------------------
cp "$PROJ_DIR/yak/manifest.yml" "$STAGE/manifest.yml"
cp "$PROJ_DIR/yak/icon.png"     "$STAGE/icon.png"

# --- 4. Build the package -----------------------------------------------------
echo ">> yak build --platform $PLATFORM"
BUILD_ARGS=(build --platform "$PLATFORM")
if [[ -n "$VERSION_OVERRIDE" ]]; then
  BUILD_ARGS+=(--version "$VERSION_OVERRIDE")
fi
( cd "$STAGE" && "$YAK" "${BUILD_ARGS[@]}" )

# --- 5. Move the .yak up into dist/yak ----------------------------------------
find "$STAGE" -maxdepth 1 -name '*.yak' -exec mv {} "$OUT_DIR"/ \;

echo ""
echo "Done. Package(s) in $OUT_DIR:"
ls -1 "$OUT_DIR"/*.yak 2>/dev/null || { echo "  (no .yak produced)"; exit 1; }
cat <<EOF

Next steps:
  # inspect
  "$YAK" search corhino --source https://test.yak.rhino3d.com

  # login (opens browser; token lasts ~30 days)
  "$YAK" login

  # push to TEST server first (wiped nightly)
  "$YAK" push --source https://test.yak.rhino3d.com $OUT_DIR/<file>.yak

  # push to PRODUCTION
  "$YAK" push $OUT_DIR/<file>.yak
EOF
