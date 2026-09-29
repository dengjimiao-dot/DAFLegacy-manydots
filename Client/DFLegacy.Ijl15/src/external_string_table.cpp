#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <limits>
#include <vector>

#include "external_string_table.h"
#include "memory_patch.h"

namespace
{
    constexpr std::uintptr_t ExtractionCall = 0x008AA5FA;
    constexpr std::uintptr_t OriginalExtractionFunction = 0x008AA170;
    constexpr std::uintptr_t ResolveClientPathFunction = 0x00401A20;
    constexpr std::size_t MaximumStringTableBytes = 16U * 1024U * 1024U;

    using ResolveClientPath = char* (__cdecl*)(const char*);

    bool IsUtf8WithMultibyteCharacters(
        const unsigned char* bytes,
        std::size_t size) noexcept
    {
        bool hasMultibyteCharacters = false;
        std::size_t index = 0;
        while (index < size)
        {
            const unsigned char lead = bytes[index];
            if (lead <= 0x7F)
            {
                ++index;
                continue;
            }

            std::size_t sequenceLength = 0;
            if (lead >= 0xC2 && lead <= 0xDF)
            {
                sequenceLength = 2;
            }
            else if (lead >= 0xE0 && lead <= 0xEF)
            {
                sequenceLength = 3;
            }
            else if (lead >= 0xF0 && lead <= 0xF4)
            {
                sequenceLength = 4;
            }
            else
            {
                return false;
            }

            if (sequenceLength > size - index)
            {
                return false;
            }
            for (std::size_t offset = 1; offset < sequenceLength; ++offset)
            {
                if ((bytes[index + offset] & 0xC0) != 0x80)
                {
                    return false;
                }
            }

            const unsigned char second = bytes[index + 1];
            if ((lead == 0xE0 && second < 0xA0)
                || (lead == 0xED && second >= 0xA0)
                || (lead == 0xF0 && second < 0x90)
                || (lead == 0xF4 && second > 0x8F))
            {
                return false;
            }

            hasMultibyteCharacters = true;
            index += sequenceLength;
        }
        return hasMultibyteCharacters;
    }

    bool IsStructurallyCp936(
        const unsigned char* bytes,
        std::size_t size) noexcept
    {
        std::size_t index = 0;
        while (index < size)
        {
            const unsigned char value = bytes[index];
            if (value == 0)
            {
                return false;
            }
            if (value <= 0x7F)
            {
                ++index;
                continue;
            }
            if (value < 0x81 || value > 0xFE || index + 1 >= size)
            {
                return false;
            }

            const unsigned char trail = bytes[index + 1];
            if (trail < 0x40 || trail > 0xFE || trail == 0x7F)
            {
                return false;
            }
            index += 2;
        }
        return true;
    }

    bool ReadFileBytes(
        const wchar_t* path,
        std::vector<unsigned char>& bytes) noexcept
    {
        const HANDLE file = CreateFileW(
            path,
            GENERIC_READ,
            FILE_SHARE_READ,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        LARGE_INTEGER fileSize{};
        bool success = GetFileSizeEx(file, &fileSize) != FALSE
            && fileSize.QuadPart > 0
            && fileSize.QuadPart
                <= static_cast<LONGLONG>(MaximumStringTableBytes);
        if (success)
        {
            try
            {
                bytes.resize(static_cast<std::size_t>(fileSize.QuadPart));
            }
            catch (...)
            {
                success = false;
            }
        }

        if (success)
        {
            DWORD bytesRead = 0;
            success = ReadFile(
                file,
                bytes.data(),
                static_cast<DWORD>(bytes.size()),
                &bytesRead,
                nullptr) != FALSE
                && bytesRead == bytes.size();
        }
        CloseHandle(file);
        return success;
    }

    bool WriteFileBytes(
        const char* path,
        const std::vector<unsigned char>& bytes) noexcept
    {
        const HANDLE file = CreateFileA(
            path,
            GENERIC_WRITE,
            0,
            nullptr,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_TEMPORARY,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        DWORD bytesWritten = 0;
        const bool success = WriteFile(
            file,
            bytes.data(),
            static_cast<DWORD>(bytes.size()),
            &bytesWritten,
            nullptr) != FALSE
            && bytesWritten == bytes.size()
            && FlushFileBuffers(file) != FALSE;
        CloseHandle(file);
        if (!success)
        {
            DeleteFileA(path);
        }
        return success;
    }

    const char* EncodingErrorText(
        dfl::external_string_table::EncodingValidation validation) noexcept
    {
        using Validation = dfl::external_string_table::EncodingValidation;
        switch (validation)
        {
        case Validation::ValidCp936:
            return "valid CP936";
        case Validation::Empty:
            return "the file is empty";
        case Validation::UnicodeBom:
            return "a Unicode BOM was found";
        case Validation::Utf8:
            return "UTF-8 content was found";
        case Validation::EmbeddedNull:
            return "an embedded NUL byte was found";
        case Validation::InvalidCp936:
            return "the byte sequence is not valid CP936";
        case Validation::TooLarge:
            return "the file exceeds the supported size";
        }
        return "an unknown encoding error occurred";
    }

    int __cdecl ExtractExternalStringTable(
        const char* destinationFileName) noexcept
    {
        wchar_t executablePath[MAX_PATH]{};
        wchar_t sourcePath[MAX_PATH]{};
        const DWORD pathLength = GetModuleFileNameW(
            nullptr,
            executablePath,
            MAX_PATH);
        if (pathLength == 0
            || pathLength >= MAX_PATH
            || !dfl::external_string_table::BuildPluginPath(
                executablePath,
                sourcePath,
                MAX_PATH))
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: could not resolve Plugin\\dnf.str.\n");
            return -1;
        }

        std::vector<unsigned char> bytes;
        if (!ReadFileBytes(sourcePath, bytes))
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: could not read Plugin\\dnf.str.\n");
            return -1;
        }

        const auto validation = dfl::external_string_table::ValidateCp936(
            bytes.data(),
            bytes.size());
        if (validation
            != dfl::external_string_table::EncodingValidation::ValidCp936)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: Plugin\\dnf.str rejected: ");
            OutputDebugStringA(EncodingErrorText(validation));
            OutputDebugStringA(". CP936 is required.\n");
            return -1;
        }

        if (destinationFileName == nullptr)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: the client dnf.str destination is null.\n");
            return -1;
        }

        const auto resolveClientPath = reinterpret_cast<ResolveClientPath>(
            ResolveClientPathFunction);
        char* const destinationPath = resolveClientPath(destinationFileName);
        if (destinationPath == nullptr
            || !WriteFileBytes(destinationPath, bytes))
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: could not stage Plugin\\dnf.str for the client parser.\n");
            return -1;
        }
        return 1;
    }

    bool GetCallDestination(
        std::uintptr_t callAddress,
        std::uintptr_t& destination) noexcept
    {
        unsigned char opcode = 0;
        std::memcpy(
            &opcode,
            reinterpret_cast<const void*>(callAddress),
            sizeof(opcode));
        if (opcode != 0xE8)
        {
            return false;
        }

        std::int32_t displacement = 0;
        std::memcpy(
            &displacement,
            reinterpret_cast<const void*>(callAddress + 1),
            sizeof(displacement));
        const std::int64_t target =
            static_cast<std::int64_t>(callAddress + 5)
            + static_cast<std::int64_t>(displacement);
        if (target < 0
            || static_cast<std::uint64_t>(target)
                > static_cast<std::uint64_t>(UINTPTR_MAX))
        {
            return false;
        }

        destination = static_cast<std::uintptr_t>(target);
        return true;
    }
}

namespace dfl::external_string_table
{
    EncodingValidation ValidateCp936(
        const unsigned char* bytes,
        std::size_t size) noexcept
    {
        if (bytes == nullptr || size == 0)
        {
            return EncodingValidation::Empty;
        }
        if (size > MaximumStringTableBytes
            || size > static_cast<std::size_t>(
                std::numeric_limits<int>::max()))
        {
            return EncodingValidation::TooLarge;
        }
        if ((size >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF)
            || (size >= 2
                && ((bytes[0] == 0xFF && bytes[1] == 0xFE)
                    || (bytes[0] == 0xFE && bytes[1] == 0xFF))))
        {
            return EncodingValidation::UnicodeBom;
        }
        if (std::find(bytes, bytes + size, 0) != bytes + size)
        {
            return EncodingValidation::EmbeddedNull;
        }
        if (IsUtf8WithMultibyteCharacters(bytes, size))
        {
            return EncodingValidation::Utf8;
        }
        if (!IsStructurallyCp936(bytes, size)
            || MultiByteToWideChar(
                936,
                MB_ERR_INVALID_CHARS,
                reinterpret_cast<const char*>(bytes),
                static_cast<int>(size),
                nullptr,
                0) <= 0)
        {
            return EncodingValidation::InvalidCp936;
        }
        return EncodingValidation::ValidCp936;
    }

    bool BuildPluginPath(
        const wchar_t* executablePath,
        wchar_t* output,
        std::size_t outputCapacity) noexcept
    {
        if (executablePath == nullptr
            || *executablePath == L'\0'
            || output == nullptr
            || outputCapacity == 0)
        {
            return false;
        }

        const wchar_t* const backslash = std::wcsrchr(
            executablePath,
            L'\\');
        const wchar_t* const slash = std::wcsrchr(executablePath, L'/');
        const wchar_t* separator = backslash;
        if (separator == nullptr || (slash != nullptr && slash > separator))
        {
            separator = slash;
        }

        const std::size_t prefixLength = separator == nullptr
            ? 0
            : static_cast<std::size_t>(separator - executablePath + 1);
        const std::size_t suffixLength = std::wcslen(RelativePath);
        if (prefixLength > outputCapacity
            || suffixLength >= outputCapacity - prefixLength)
        {
            return false;
        }

        if (prefixLength != 0)
        {
            std::wmemcpy(output, executablePath, prefixLength);
        }
        std::wmemcpy(output + prefixLength, RelativePath, suffixLength + 1);
        return true;
    }

    HookState InspectHook() noexcept
    {
        std::uintptr_t destination = 0;
        if (!GetCallDestination(ExtractionCall, destination))
        {
            return HookState::Mismatch;
        }
        if (destination == OriginalExtractionFunction)
        {
            return HookState::Original;
        }
        if (destination == reinterpret_cast<std::uintptr_t>(
                &ExtractExternalStringTable))
        {
            return HookState::Installed;
        }
        return HookState::Mismatch;
    }

    bool InstallHook() noexcept
    {
        const HookState state = InspectHook();
        if (state == HookState::Installed)
        {
            return true;
        }
        return state == HookState::Original
            && memory_patch::WriteRelativeCall(
                ExtractionCall,
                reinterpret_cast<std::uintptr_t>(
                    &ExtractExternalStringTable));
    }

    bool RestoreOriginalHook() noexcept
    {
        const HookState state = InspectHook();
        if (state == HookState::Original)
        {
            return true;
        }
        return state == HookState::Installed
            && memory_patch::WriteRelativeCall(
                ExtractionCall,
                OriginalExtractionFunction);
    }
}
