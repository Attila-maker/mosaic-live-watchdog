#!/usr/bin/env bash
# Builds and tests NodeWatch, then makes the release package:
#   ./build.sh            -> out/nodewatch-<version>.tar.gz
# Needs the .NET 10 SDK (Ubuntu 24.04: sudo apt install dotnet-sdk-10.0).
# The package is framework-dependent: the node only needs dotnet-runtime-10.0 (install.sh installs it).
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

echo "==> tests"
dotnet build tests/NodeWatch.Tests -c Release -warnaserror -v quiet
dotnet tests/NodeWatch.Tests/bin/Release/net10.0/NodeWatch.Tests.dll

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/NodeWatch/NodeWatch.csproj)"
PKG="nodewatch-$VERSION"
echo "==> $PKG"
rm -rf "out/$PKG" "out/$PKG.tar.gz"
dotnet publish src/NodeWatch -c Release --no-self-contained -p:UseAppHost=false -p:DebugType=none -o "out/$PKG/app" -v quiet
cp deploy/install.sh deploy/uninstall.sh deploy/nodewatch.service deploy/50-nodewatch.rules \
   deploy/appsettings.example.json "out/$PKG/"
chmod 0755 "out/$PKG/install.sh" "out/$PKG/uninstall.sh"
tar --owner=0 --group=0 -czf "out/$PKG.tar.gz" -C out "$PKG"
echo "==> out/$PKG.tar.gz"
