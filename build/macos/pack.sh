#!/usr/bin/env bash
#
# Builds GitVault.app and wraps it in a .dmg.
#
# Signing and notarisation are performed only when the environment supplies an identity, so this
# script runs unchanged on a CI machine with no certificates. An unsigned build is fine for
# testing; it is not fine for distribution, and the notes at the bottom say why.
#
#   ./build/macos/pack.sh osx-arm64 0.1.0

set -euo pipefail

RUNTIME="${1:-osx-arm64}"
VERSION="${2:-0.1.0}"
CONFIGURATION="${CONFIGURATION:-Release}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PUBLISH_DIR="${REPO_ROOT}/artifacts/${RUNTIME}"
BUNDLE_DIR="${REPO_ROOT}/artifacts/bundle/GitVault.app"
INSTALLER_DIR="${REPO_ROOT}/artifacts/installers"

rm -rf "${BUNDLE_DIR}"
mkdir -p "${BUNDLE_DIR}/Contents/MacOS" "${BUNDLE_DIR}/Contents/Resources" "${INSTALLER_DIR}"

echo "Publishing ${RUNTIME}…"
dotnet publish "${REPO_ROOT}/src/GitVault.App/GitVault.App.csproj" \
    --configuration "${CONFIGURATION}" \
    --runtime "${RUNTIME}" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishTrimmed=false \
    -p:InvariantGlobalization=false \
    -p:Version="${VERSION}" \
    --output "${PUBLISH_DIR}"

cp -R "${PUBLISH_DIR}/." "${BUNDLE_DIR}/Contents/MacOS/"
chmod +x "${BUNDLE_DIR}/Contents/MacOS/GitVault"

# iconutil only runs on macOS, which is where this script runs; the .iconset itself is produced
# cross-platform by build/generate-appicon.ps1 and committed.
ICONSET="${REPO_ROOT}/build/appicon/GitVault.iconset"
if [[ -d "${ICONSET}" ]] && command -v iconutil >/dev/null 2>&1; then
    iconutil -c icns "${ICONSET}" -o "${BUNDLE_DIR}/Contents/Resources/GitVault.icns"
    echo "wrote GitVault.icns"
else
    echo "iconutil or the .iconset is unavailable; the bundle will use the default icon."
fi

cat > "${BUNDLE_DIR}/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>GitVault</string>
    <key>CFBundleDisplayName</key>
    <string>GitVault</string>
    <key>CFBundleIdentifier</key>
    <string>org.gitvault.app</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>GitVault</string>
    <key>CFBundleIconFile</key>
    <string>GitVault</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <!-- GitVault makes no network calls, so it requests no network entitlement. -->
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.developer-tools</string>
</dict>
</plist>
PLIST

if [[ -n "${MACOS_SIGN_IDENTITY:-}" ]]; then
    echo "Signing with ${MACOS_SIGN_IDENTITY}…"

    # Hardened runtime is required for notarisation. GitVault needs no entitlement exceptions:
    # it reads files the user already owns and talks to local sockets.
    codesign --force --deep --options runtime --timestamp \
        --sign "${MACOS_SIGN_IDENTITY}" "${BUNDLE_DIR}"
    codesign --verify --strict --verbose=2 "${BUNDLE_DIR}"
else
    echo "MACOS_SIGN_IDENTITY is not set; producing an unsigned bundle."
fi

DMG_PATH="${INSTALLER_DIR}/GitVault-${VERSION}-${RUNTIME}.dmg"
rm -f "${DMG_PATH}"

hdiutil create -volname "GitVault" \
    -srcfolder "${REPO_ROOT}/artifacts/bundle" \
    -ov -format UDZO "${DMG_PATH}"

echo "wrote ${DMG_PATH}"

if [[ -n "${MACOS_SIGN_IDENTITY:-}" && -n "${MACOS_NOTARY_PROFILE:-}" ]]; then
    echo "Notarising…"
    xcrun notarytool submit "${DMG_PATH}" --keychain-profile "${MACOS_NOTARY_PROFILE}" --wait
    xcrun stapler staple "${DMG_PATH}"
else
    cat <<'NOTES'

Not notarised. To distribute this build:

  1. Sign with a Developer ID Application certificate:
       export MACOS_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"

  2. Store notarisation credentials once:
       xcrun notarytool store-credentials gitvault-notary \
           --apple-id you@example.com --team-id TEAMID --password <app-specific-password>
       export MACOS_NOTARY_PROFILE=gitvault-notary

  3. Re-run this script. Without stapled notarisation, Gatekeeper will refuse to open the app
     on any machine other than the one that built it.

NOTES
fi
