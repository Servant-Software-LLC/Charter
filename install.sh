#!/usr/bin/env bash
# =============================================================================
# install.sh — macOS/Linux bootstrap for Charter (NO .NET required).
#
# Downloads the prebuilt self-contained binary from the GitHub Release and installs it on PATH.
# Prefer Homebrew if you have it:  brew install servant-software-llc/tap/charter
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/Servant-Software-LLC/Charter/master/install.sh | bash
#   ./install.sh                 # latest release
#   ./install.sh 0.1.0-preview.1 # specific version
#
# Env overrides:
#   CHARTER_BIN   dir for the `charter` binary (default: ~/.local/bin)
# =============================================================================
set -euo pipefail

REPO="Servant-Software-LLC/Charter"
BIN_DIR="${CHARTER_BIN:-$HOME/.local/bin}"

# --- 1. detect platform -> .NET RID -----------------------------------------
os="$(uname -s)"; arch="$(uname -m)"
case "$os" in
  Darwin) os_rid="osx" ;;
  Linux)  os_rid="linux" ;;
  *) echo "ERROR: unsupported OS '$os' (this installer covers macOS and Linux)." >&2; exit 1 ;;
esac
case "$arch" in
  arm64|aarch64) arch_rid="arm64" ;;
  x86_64|amd64)  arch_rid="x64" ;;
  *) echo "ERROR: unsupported architecture '$arch'." >&2; exit 1 ;;
esac
RID="$os_rid-$arch_rid"

# --- 2. resolve version/tag (latest release, prereleases included) ----------
if [ "${1:-}" != "" ]; then
  VER="${1#v}"
else
  VER="$(curl -fsSL "https://api.github.com/repos/$REPO/releases" \
        | grep -m1 '"tag_name"' | sed -E 's/.*"tag_name" *: *"v?([^"]+)".*/\1/')"
fi
[ -n "$VER" ] || { echo "ERROR: could not resolve a release version." >&2; exit 1; }
TAG="v$VER"
ASSET="charter-$VER-$RID.tar.gz"
URL="https://github.com/$REPO/releases/download/$TAG/$ASSET"

echo "Installing Charter $VER ($RID)"
echo "  from $URL"

# --- 3. download, verify, extract -------------------------------------------
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
curl -fsSL "$URL" -o "$tmp/$ASSET"

if curl -fsSL "$URL.sha256" -o "$tmp/$ASSET.sha256" 2>/dev/null; then
  expected="$(awk '{print $1}' "$tmp/$ASSET.sha256")"
  if command -v sha256sum >/dev/null 2>&1; then actual="$(sha256sum "$tmp/$ASSET" | awk '{print $1}')"
  else actual="$(shasum -a 256 "$tmp/$ASSET" | awk '{print $1}')"; fi
  [ "$expected" = "$actual" ] || { echo "ERROR: checksum mismatch for $ASSET." >&2; exit 1; }
  echo "  checksum OK"
fi

tar -C "$tmp" -xzf "$tmp/$ASSET"
mkdir -p "$BIN_DIR"
install -m 0755 "$tmp/charter" "$BIN_DIR/charter"

# --- 4. next steps -----------------------------------------------------------
echo ""
echo "Charter $VER installed to $BIN_DIR/charter"
case ":$PATH:" in
  *":$BIN_DIR:"*) : ;;
  *) echo "NOTE: add $BIN_DIR to your PATH, e.g.:"
     echo "      echo 'export PATH=\"$BIN_DIR:\$PATH\"' >> ~/.zshrc && source ~/.zshrc" ;;
esac
echo "Run:  charter --version"
