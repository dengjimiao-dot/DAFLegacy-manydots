#!/usr/bin/env bash
# DAF-MCP 模块单独发布入口（docs/design/09-mcp-packet-tap.md），与 publish.sh
# 拆分。产物落在 dist/mcp-${rid}。
set -euo pipefail

cd "$(dirname "$0")"

detect_rid() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"
    case "$os" in
        Darwin)
            case "$arch" in
                arm64) echo osx-arm64 ;;
                x86_64) echo osx-x64 ;;
                *) echo "Unsupported macOS architecture: $arch" >&2; exit 1 ;;
            esac
            ;;
        Linux)
            case "$arch" in
                x86_64) echo linux-x64 ;;
                aarch64 | arm64) echo linux-arm64 ;;
                *) echo "Unsupported Linux architecture: $arch" >&2; exit 1 ;;
            esac
            ;;
        MINGW* | MSYS* | CYGWIN*) echo win-x64 ;;
        *) echo "Unsupported platform: $os" >&2; exit 1 ;;
    esac
}

publish_rid() {
    local rid="$1"
    local out="dist/mcp-${rid}"
    echo "[publish-mcp] Publishing DAF-MCP module for ${rid} to ${out} ..."
    rm -rf "${out}"
    dotnet publish Server/DFLegacy.Mcp/DFLegacy.Mcp.csproj \
        -c Release -r "${rid}" --self-contained false -o "${out}"
    local dll
    for dll in DFLegacy.Mcp.dll ModelContextProtocol.Core.dll ModelContextProtocol.dll \
               ModelContextProtocol.AspNetCore.dll Microsoft.Extensions.AI.Abstractions.dll; do
        if [[ ! -f "${out}/${dll}" ]]; then
            echo "[publish-mcp] Missing module DLL: ${dll}" >&2
            exit 1
        fi
    done
}

if [[ "${PUBLISH_ALL:-}" == "1" ]]; then
    for rid in win-x64 linux-x64 linux-arm64; do
        publish_rid "$rid"
    done
else
    publish_rid "$(detect_rid)"
fi

echo "[publish-mcp] Done."
