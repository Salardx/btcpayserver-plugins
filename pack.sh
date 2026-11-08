#!/usr/bin/env bash
set -euo pipefail

# Builds the .btcpay package of one plugin:  ./pack.sh BTCPayServer.Plugins.Neo
# Output: artifacts/packed/<name>/<name>.btcpay and its manifest, ready for Server Settings > Plugins.

NAME="${1:?usage: pack.sh <plugin project name>}"
PUBLISH="artifacts/publish/$NAME"

rm -rf "$PUBLISH"
dotnet publish "src/$NAME/$NAME.csproj" -c Release -m:1 -p:StaticWebAssetsEnabled=false -o "$PUBLISH"
dotnet run --project submodules/btcpayserver/BTCPayServer.PluginPacker -c Release -- "$PUBLISH" "$NAME" artifacts/packed
