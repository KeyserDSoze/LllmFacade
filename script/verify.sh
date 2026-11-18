#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
node --check src/extension/background.js
node --check src/extension/security.js
node --check src/desktop/main.cjs
node --check src/desktop/preload.cjs
node --check src/desktop/renderer/app.js
node --test tests/extension/*.test.cjs
(cd src/frontend && npm install && npm run build)
(cd tests/frontend && npm install && npm run test:unit)
if command -v dotnet >/dev/null 2>&1; then
  dotnet run --project tests/backend/LllmFacade.Business.Tests/LllmFacade.Business.Tests.csproj
else
  echo "dotnet non disponibile: test backend saltati"
fi

