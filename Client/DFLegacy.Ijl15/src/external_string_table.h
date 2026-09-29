#pragma once

#include <cstddef>

namespace dfl::external_string_table
{
    inline constexpr wchar_t RelativePath[] = L"Plugin\\dnf.str";

    enum class EncodingValidation
    {
        ValidCp936,
        Empty,
        UnicodeBom,
        Utf8,
        EmbeddedNull,
        InvalidCp936,
        TooLarge
    };

    EncodingValidation ValidateCp936(
        const unsigned char* bytes,
        std::size_t size) noexcept;

    bool BuildPluginPath(
        const wchar_t* executablePath,
        wchar_t* output,
        std::size_t outputCapacity) noexcept;

    enum class HookState
    {
        Original,
        Installed,
        Mismatch
    };

    HookState InspectHook() noexcept;
    bool InstallHook() noexcept;
    bool RestoreOriginalHook() noexcept;
}
