#!/usr/bin/env bash
# Linux/macOS 发布入口（docs/design/03 · 6.1）。自探测当前平台 RID；
# 产出与 publish.bat 等价的目录布局。PUBLISH_ALL=1 时发布全部目标。
# DAF-MCP 模块由 publish-mcp.sh 单独发布。
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
    local out="dist/server-${rid}"
    echo "[publish] Publishing server for ${rid} to ${out} ..."
    dotnet publish Server/DAFLegacy.Server/DAFLegacy.Server.csproj \
        -c Release -r "${rid}" --self-contained false -o "${out}"
}

if [[ "${PUBLISH_ALL:-}" == "1" ]]; then
    for rid in win-x64 linux-x64 linux-arm64; do
        publish_rid "$rid"
    done
else
    publish_rid "$(detect_rid)"
fi

echo "[publish] Done."
