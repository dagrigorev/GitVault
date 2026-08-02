#!/usr/bin/env bash
#
# Builds the Linux artifacts: a tarball always, a .deb when dpkg-deb is present, and an AppImage
# when appimagetool is present. Missing tools are reported rather than installed: a packaging
# script that reaches out to the network is a packaging script nobody can audit.
#
#   ./build/linux/pack.sh linux-x64 0.1.0

set -euo pipefail

RUNTIME="${1:-linux-x64}"
VERSION="${2:-0.1.0}"
CONFIGURATION="${CONFIGURATION:-Release}"

case "${RUNTIME}" in
    linux-x64)   DEB_ARCH="amd64"; APPIMAGE_ARCH="x86_64" ;;
    linux-arm64) DEB_ARCH="arm64"; APPIMAGE_ARCH="aarch64" ;;
    *) echo "Unsupported runtime ${RUNTIME}" >&2; exit 1 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PUBLISH_DIR="${REPO_ROOT}/artifacts/${RUNTIME}"
INSTALLER_DIR="${REPO_ROOT}/artifacts/installers"
STAGE_DIR="${REPO_ROOT}/artifacts/stage/${RUNTIME}"

rm -rf "${STAGE_DIR}"
mkdir -p "${INSTALLER_DIR}"

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

# ---------------------------------------------------------------- tarball
TARBALL="${INSTALLER_DIR}/GitVault-${VERSION}-${RUNTIME}.tar.gz"
tar -czf "${TARBALL}" -C "${PUBLISH_DIR}" .
echo "wrote ${TARBALL}"

# ---------------------------------------------------------------- desktop entry
write_desktop_entry() {
    cat > "$1" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=GitVault
Comment=Git accounts, keys and credentials
Comment[ru]=Учётные записи, ключи и учётные данные Git
Comment[zh_CN]=Git 账户、密钥与凭据
Exec=GitVault
Icon=gitvault
Terminal=false
Categories=Development;RevisionControl;
Keywords=git;ssh;credentials;
DESKTOP
}

# ---------------------------------------------------------------- .deb
if command -v dpkg-deb >/dev/null 2>&1; then
    DEB_ROOT="${STAGE_DIR}/deb"
    mkdir -p "${DEB_ROOT}/DEBIAN" \
             "${DEB_ROOT}/usr/lib/gitvault" \
             "${DEB_ROOT}/usr/bin" \
             "${DEB_ROOT}/usr/share/applications"

    cp -R "${PUBLISH_DIR}/." "${DEB_ROOT}/usr/lib/gitvault/"
    chmod +x "${DEB_ROOT}/usr/lib/gitvault/GitVault"
    ln -sf ../lib/gitvault/GitVault "${DEB_ROOT}/usr/bin/GitVault"
    write_desktop_entry "${DEB_ROOT}/usr/share/applications/gitvault.desktop"

    # The desktop entry names "gitvault"; hicolor is where a launcher looks for it.
    ICON_SOURCE="${REPO_ROOT}/build/appicon/GitVault.iconset"
    for size in 16 24 32 48 64 128 256; do
        if [[ -f "${ICON_SOURCE}/icon_${size}x${size}.png" ]]; then
            ICON_DIR="${DEB_ROOT}/usr/share/icons/hicolor/${size}x${size}/apps"
            mkdir -p "${ICON_DIR}"
            cp "${ICON_SOURCE}/icon_${size}x${size}.png" "${ICON_DIR}/gitvault.png"
        fi
    done

    cat > "${DEB_ROOT}/DEBIAN/control" <<CONTROL
Package: gitvault
Version: ${VERSION}
Section: devel
Priority: optional
Architecture: ${DEB_ARCH}
Maintainer: GitVault contributors <noreply@example.invalid>
Depends: libicu72 | libicu71 | libicu70 | libicu69 | libicu67 | libicu66
Recommends: git, openssh-client
Suggests: libsecret-tools
Description: Git accounts, keys and credentials manager
 GitVault discovers and manages the Git identities, SSH keys, SSH agents and
 credential-store entries on a machine, and switches between them globally or
 per repository.
 .
 It makes no network calls and collects no telemetry.
CONTROL

    DEB_PATH="${INSTALLER_DIR}/gitvault_${VERSION}_${DEB_ARCH}.deb"
    dpkg-deb --build --root-owner-group "${DEB_ROOT}" "${DEB_PATH}"
    echo "wrote ${DEB_PATH}"
else
    echo "dpkg-deb not found; skipping the .deb."
fi

# ---------------------------------------------------------------- AppImage
if command -v appimagetool >/dev/null 2>&1; then
    APPDIR="${STAGE_DIR}/GitVault.AppDir"
    mkdir -p "${APPDIR}/usr/bin" "${APPDIR}/usr/share/applications"

    cp -R "${PUBLISH_DIR}/." "${APPDIR}/usr/bin/"
    chmod +x "${APPDIR}/usr/bin/GitVault"

    write_desktop_entry "${APPDIR}/gitvault.desktop"
    cp "${APPDIR}/gitvault.desktop" "${APPDIR}/usr/share/applications/"

    cat > "${APPDIR}/AppRun" <<'APPRUN'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "${0}")")"
exec "${HERE}/usr/bin/GitVault" "$@"
APPRUN
    chmod +x "${APPDIR}/AppRun"

    ARCH="${APPIMAGE_ARCH}" appimagetool "${APPDIR}" \
        "${INSTALLER_DIR}/GitVault-${VERSION}-${APPIMAGE_ARCH}.AppImage"

    echo "wrote AppImage"
else
    echo "appimagetool not found; skipping the AppImage."
fi
