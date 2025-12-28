#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT_PATH="$ROOT_DIR/WolfEngine.Editor/WolfEngine.Editor.csproj"

APP_NAME="${APP_NAME:-WolfEngine.Editor}"
RUNTIME="${RUNTIME:-osx-arm64}"
CONFIGURATION="${CONFIGURATION:-Release}"

PUBLISH_DIR="$ROOT_DIR/dist/publish/$RUNTIME"
APP_BUNDLE="$ROOT_DIR/dist/${APP_NAME}.app"
CONTENTS_DIR="$APP_BUNDLE/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

rm -rf "$APP_BUNDLE"

dotnet publish "$PROJECT_PATH" \
  -c "$CONFIGURATION" \
  -r "$RUNTIME" \
  --self-contained true \
  -o "$PUBLISH_DIR"

mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"
cp -R "$PUBLISH_DIR/." "$MACOS_DIR/"

# Ensure all runtime-native assets from NuGet are included under runtimes/.
NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
DEPS_JSON="$PUBLISH_DIR/WolfEngine.Editor.deps.json"

if [[ -f "$DEPS_JSON" ]]; then
  NUGET_PACKAGES="$NUGET_PACKAGES" \
  DEPS_JSON="$DEPS_JSON" \
  MACOS_DIR="$MACOS_DIR" \
  /usr/bin/python3 - <<'PY'
import json
import os
import shutil

deps_path = os.environ["DEPS_JSON"]
nuget = os.environ["NUGET_PACKAGES"]
dest_root = os.environ["MACOS_DIR"]

with open(deps_path, "r", encoding="utf-8") as handle:
    data = json.load(handle)

libraries = data.get("libraries", {})
copied = 0

def copy_asset(libname, asset):
    global copied
    lib = libraries.get(libname)
    if not lib or lib.get("type") != "package":
        return
    rel = lib.get("path")
    if not rel:
        return
    src = os.path.join(nuget, rel, asset)
    if not os.path.exists(src):
        return
    dest = os.path.join(dest_root, asset)
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    shutil.copy2(src, dest)
    copied += 1

for target in data.get("targets", {}).values():
    for libname, libdata in target.items():
        runtime_targets = libdata.get("runtimeTargets", {})
        for asset in runtime_targets.keys():
            if asset.startswith("runtimes/"):
                copy_asset(libname, asset)
        native_assets = libdata.get("native", {})
        for asset in native_assets.keys():
            if asset.startswith("runtimes/"):
                copy_asset(libname, asset)

print(f"Copied {copied} runtime asset(s) into app bundle.")
PY
fi

PLIST_TEMPLATE="$ROOT_DIR/scripts/macos/Info.plist"
PLIST_OUT="$CONTENTS_DIR/Info.plist"

sed -e "s|__APP_NAME__|$APP_NAME|g" \
    -e "s|__BUNDLE_ID__|${BUNDLE_ID:-com.wolfengine.editor}|g" \
    -e "s|__VERSION__|${VERSION:-0.1.0}|g" \
    -e "s|__MIN_SYSTEM_VERSION__|${MIN_SYSTEM_VERSION:-15.0}|g" \
    "$PLIST_TEMPLATE" > "$PLIST_OUT"

if [[ -n "${CODESIGN_IDENTITY:-}" ]]; then
  codesign --force --deep --options runtime --sign "$CODESIGN_IDENTITY" "$APP_BUNDLE"
elif [[ "${ADHOC_SIGN:-true}" == "true" ]]; then
  codesign --force --deep --sign - "$APP_BUNDLE"
fi

echo "App bundle created at: $APP_BUNDLE"
