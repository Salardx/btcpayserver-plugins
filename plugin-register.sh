#!/usr/bin/env bash
set -euo pipefail

# Points a BTCPay Server development checkout (the submodule) at the debug builds of the plugins,
# so they load when BTCPay Server is started from the submodule.

source plugin-env.sh

PATHS=""
for PROJECT in $PROJECTS; do
  TARGET_PATH="$(dotnet build "src/$PROJECT/$PROJECT.csproj" -p:Configuration=Debug -getProperty:TargetPath)"
  PATHS="${PATHS:+$PATHS;}$TARGET_PATH"
done

printf '{ "DEBUG_PLUGINS": "%s" }' "$PATHS" > "submodules/btcpayserver/BTCPayServer/appsettings.dev.json"

echo "The plugins will now start when debugging BTCPay Server"
