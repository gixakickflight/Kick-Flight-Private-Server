#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
python3 "$repo_root/tests/ios-transport/verify_vector_openssl.py"
dotnet run --project "$repo_root/tests/ios-transport/KickFlight.Transport.Tests.csproj" --configuration Release
