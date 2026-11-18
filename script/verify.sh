#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
node --check src/extension/background.js
node --check src/extension/security.js
node --check src/desktop/main.cjs
node --check src/desktop/preload.cjs
node --check src/desktop/workspace.cjs
node --test tests/desktop/*.test.cjs
node --check src/desktop/renderer/app.js
node --test tests/extension/*.test.cjs
(cd src/frontend && npm install && npm run build)
(cd tests/frontend && npm install && npm run test:unit)
(cd src/admin && npm install && npm run build)
(cd tests/admin && npm install && npm run test:unit)
if command -v dotnet >/dev/null 2>&1; then
  dotnet run --project tests/backend/LllmFacade.Business.Tests/LllmFacade.Business.Tests.csproj
  dotnet run --project tests/backend/LllmFacade.Browser.Tests/LllmFacade.Browser.Tests.csproj
  if command -v docker >/dev/null 2>&1; then
    dotnet run --project tests/backend/LllmFacade.Api.Tests/LllmFacade.Api.Tests.csproj
  else
    echo "docker non disponibile: test API saltati"
  fi
else
  echo "dotnet non disponibile: test backend saltati"
fi

