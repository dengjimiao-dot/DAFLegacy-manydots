#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

#include "memory_patch.h"
#include "output_paths.h"

namespace
{
    constexpr std::uintptr_t ResolveClientPathFunction = 0x00401A20;
    // Keep whole x86 instructions in the trampoline. The resolver starts
    // with `sub esp, 9Ch`, which is six bytes after the first three bytes.
    constexpr std::size_t StolenPrologueSize = 9;
    constexpr std::size_t RelativeBranchSize = 5;

    constexpr std::array<unsigned char, StolenPrologueSize>
        OriginalResolvePrologue{
            0x55,
            0x8B,
            0xEC,
            0x81,
            0xEC,
            0x9C,
            0x00,
            0x00,
            0x00};

    using ResolveClientPath = char* (__cdecl*)(const char*);

    ResolveClientPath OriginalResolve = nullptr;
    bool HookInstalled = false;

    // Path resolution is normally called synchronously during client startup,
    // but thread-local storage prevents concurrent log/screenshot calls from
    // overwriting one another's returned buffer.
    __declspec(thread) char RedirectedPath[2048]{};

    bool IsAsciiSlash(char value) noexcept
    {
        return value == '\\' || value == '/';
    }

    bool EqualsIgnoreCase(
        const char* left,
        const char* right) noexcept
    {
        if (left == nullptr || right == nullptr)
        {
            return false;
        }

        while (*left != '\0' && *right != '\0')
        {
            char leftValue = *left;
            char rightValue = *right;
            if (leftValue >= 'a' && leftValue <= 'z')
            {
                leftValue = static_cast<char>(leftValue - 'a' + 'A');
            }
            if (rightValue >= 'a' && rightValue <= 'z')
            {
                rightValue = static_cast<char>(rightValue - 'a' + 'A');
            }
            if (leftValue != rightValue)
            {
                return false;
            }
            ++left;
            ++right;
        }
        return *left == '\0' && *right == '\0';
    }

    bool StartsWithIgnoreCase(
        const char* value,
        const char* prefix) noexcept
    {
        if (value == nullptr || prefix == nullptr)
        {
            return false;
        }

        while (*prefix != '\0')
        {
            char valueCharacter = *value++;
            char prefixCharacter = *prefix++;
            if (valueCharacter >= 'a' && valueCharacter <= 'z')
            {
                valueCharacter = static_cast<char>(valueCharacter - 'a' + 'A');
            }
            if (prefixCharacter >= 'a' && prefixCharacter <= 'z')
            {
                prefixCharacter = static_cast<char>(
                    prefixCharacter - 'a' + 'A');
            }
            if (valueCharacter != prefixCharacter)
            {
                return false;
            }
        }
        return true;
    }

    const char* BaseName(const char* path) noexcept
    {
        if (path == nullptr)
        {
            return nullptr;
        }

        const char* base = path;
        for (const char* cursor = path; *cursor != '\0'; ++cursor)
        {
            if (IsAsciiSlash(*cursor))
            {
                base = cursor + 1;
            }
        }
        return base;
    }

    bool IsCrashArtifact(const char* baseName) noexcept
    {
        if (baseName == nullptr)
        {
            return false;
        }
        if (StartsWithIgnoreCase(baseName, "DNF_DUMP"))
        {
            return true;
        }

        const std::size_t length = std::strlen(baseName);
        if (length < 4)
        {
            return false;
        }

        const char* extension = baseName + length - 4;
        return EqualsIgnoreCase(extension, ".dmp")
            || EqualsIgnoreCase(extension, ".trc");
    }

    bool IsLogFile(const char* baseName) noexcept
    {
        if (EqualsIgnoreCase(baseName, "DNF.trc")
            || EqualsIgnoreCase(baseName, "NeopleEngine.LOG")
            || EqualsIgnoreCase(baseName, "NiMemory.log")
            || EqualsIgnoreCase(baseName, "NiProfile.log")
            || EqualsIgnoreCase(baseName, "SocketLog.log")
            || IsCrashArtifact(baseName))
        {
            return true;
        }

        if (baseName == nullptr)
        {
            return false;
        }
        const std::size_t length = std::strlen(baseName);
        return length >= 4
            && EqualsIgnoreCase(baseName + length - 4, ".log");
    }

    bool Append(
        char* output,
        std::size_t outputCapacity,
        const char* text,
        std::size_t textLength,
        std::size_t* offset) noexcept
    {
        if (*offset >= outputCapacity
            || textLength >= outputCapacity - *offset)
        {
            return false;
        }
        std::memcpy(output + *offset, text, textLength);
        *offset += textLength;
        output[*offset] = '\0';
        return true;
    }

    bool AppendLiteral(
        char* output,
        std::size_t outputCapacity,
        const char* text,
        std::size_t* offset) noexcept
    {
        return Append(
            output,
            outputCapacity,
            text,
            std::strlen(text),
            offset);
    }

    bool BuildTrampolineJump(
        std::uintptr_t address,
        std::uintptr_t destination,
        std::array<unsigned char, RelativeBranchSize>& output) noexcept
    {
        const std::int64_t displacement =
            static_cast<std::int64_t>(destination)
            - static_cast<std::int64_t>(address)
            - static_cast<std::int64_t>(RelativeBranchSize);
        if (displacement
                < std::numeric_limits<std::int32_t>::min()
            || displacement
                > std::numeric_limits<std::int32_t>::max())
        {
            return false;
        }

        output[0] = 0xE9;
        const auto relative = static_cast<std::int32_t>(displacement);
        std::memcpy(output.data() + 1, &relative, sizeof(relative));
        return true;
    }

    char* __cdecl ResolveClientPathHook(const char* input) noexcept
    {
        if (dfl::output_paths::BuildRelativePath(
                input,
                RedirectedPath,
                sizeof(RedirectedPath)))
        {
            return RedirectedPath;
        }

        return OriginalResolve == nullptr
            ? const_cast<char*>(input)
            : OriginalResolve(input);
    }

    bool EnsureOutputDirectories() noexcept
    {
        const BOOL logsCreated = CreateDirectoryA("Logs", nullptr);
        const DWORD logsError = GetLastError();
        const BOOL screenshotsCreated = CreateDirectoryA(
            "ScreenShot",
            nullptr);
        const DWORD screenshotsError = GetLastError();
        const bool logsReady = logsCreated != FALSE
            || logsError == ERROR_ALREADY_EXISTS;
        const bool screenshotsReady = screenshotsCreated != FALSE
            || screenshotsError == ERROR_ALREADY_EXISTS;
        return logsReady && screenshotsReady;
    }
}

namespace dfl::output_paths
{
    bool BuildRelativePath(
        const char* input,
        char* output,
        std::size_t outputCapacity) noexcept
    {
        if (input == nullptr || output == nullptr || outputCapacity == 0)
        {
            return false;
        }

        const char* baseName = BaseName(input);
        if (baseName == nullptr)
        {
            return false;
        }

        constexpr char ScreenShotDirectory[] = "ScreenShot\\";
        constexpr char LogsDirectory[] = "Logs\\";

        constexpr std::size_t ScreenShotNameLength = 10;
        if (EqualsIgnoreCase(input, "ScreenShot")
            || (StartsWithIgnoreCase(input, "ScreenShot")
                && IsAsciiSlash(input[ScreenShotNameLength])))
        {
            const char* suffix = input + ScreenShotNameLength;
            while (IsAsciiSlash(*suffix))
            {
                ++suffix;
            }

            std::size_t offset = 0;
            if (!AppendLiteral(
                    output,
                    outputCapacity,
                    ScreenShotDirectory,
                    &offset))
            {
                return false;
            }
            return *suffix == '\0'
                || Append(
                    output,
                    outputCapacity,
                    suffix,
                    std::strlen(suffix),
                    &offset);
        }

        if (!IsLogFile(baseName))
        {
            return false;
        }

        std::size_t offset = 0;
        return AppendLiteral(
                   output,
                   outputCapacity,
                   LogsDirectory,
                   &offset)
            && Append(
                output,
                outputCapacity,
                baseName,
                std::strlen(baseName),
                &offset);
    }

    bool InstallHook() noexcept
    {
        if (HookInstalled)
        {
            return true;
        }
        if (!EnsureOutputDirectories())
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: could not create relative output directories.\n");
            return false;
        }

        auto* const target = reinterpret_cast<unsigned char*>(
            ResolveClientPathFunction);
        if (std::memcmp(
                target,
                OriginalResolvePrologue.data(),
                OriginalResolvePrologue.size()) != 0)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: client path resolver bytes do not match 2008DF.\n");
            return false;
        }

        const std::size_t trampolineSize =
            StolenPrologueSize + RelativeBranchSize;
        auto* const trampoline = static_cast<unsigned char*>(VirtualAlloc(
            nullptr,
            trampolineSize,
            MEM_COMMIT | MEM_RESERVE,
            PAGE_EXECUTE_READWRITE));
        if (trampoline == nullptr)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: could not allocate path resolver trampoline.\n");
            return false;
        }

        std::memcpy(
            trampoline,
            target,
            StolenPrologueSize);
        std::array<unsigned char, RelativeBranchSize> jumpBack{};
        if (!BuildTrampolineJump(
                reinterpret_cast<std::uintptr_t>(trampoline)
                    + StolenPrologueSize,
                ResolveClientPathFunction + StolenPrologueSize,
                jumpBack))
        {
            VirtualFree(trampoline, 0, MEM_RELEASE);
            OutputDebugStringA(
                "DFLegacy.Ijl15: path resolver trampoline jump is out of range.\n");
            return false;
        }
        std::memcpy(
            trampoline + StolenPrologueSize,
            jumpBack.data(),
            jumpBack.size());
        FlushInstructionCache(
            GetCurrentProcess(),
            trampoline,
            trampolineSize);

        OriginalResolve = reinterpret_cast<ResolveClientPath>(trampoline);
        std::array<unsigned char, StolenPrologueSize> replacement{};
        std::array<unsigned char, RelativeBranchSize> jumpToHook{};
        if (!BuildTrampolineJump(
                ResolveClientPathFunction,
                reinterpret_cast<std::uintptr_t>(&ResolveClientPathHook),
                jumpToHook))
        {
            OriginalResolve = nullptr;
            VirtualFree(trampoline, 0, MEM_RELEASE);
            OutputDebugStringA(
                "DFLegacy.Ijl15: path resolver hook jump is out of range.\n");
            return false;
        }
        std::memcpy(replacement.data(), jumpToHook.data(), jumpToHook.size());
        replacement[5] = 0x90;
        replacement[6] = 0x90;

        if (!dfl::memory_patch::WriteBytes(
                ResolveClientPathFunction,
                replacement.data(),
                replacement.size()))
        {
            OriginalResolve = nullptr;
            VirtualFree(trampoline, 0, MEM_RELEASE);
            OutputDebugStringA(
                "DFLegacy.Ijl15: failed to install relative output path hook.\n");
            return false;
        }

        HookInstalled = true;
        OutputDebugStringA(
            "DFLegacy.Ijl15: relative Logs/ScreenShot paths installed.\n");
        return true;
    }
}
