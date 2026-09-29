#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

#include "memory_patch.h"

namespace
{
    bool ChangeMemory(
        std::uintptr_t address,
        const void* bytes,
        unsigned char fill,
        std::size_t size) noexcept
    {
        if (size == 0)
        {
            return true;
        }
        if (address == 0)
        {
            return false;
        }

        void* const target = reinterpret_cast<void*>(address);
        DWORD oldProtection = 0;
        if (!VirtualProtect(
            target,
            size,
            PAGE_EXECUTE_READWRITE,
            &oldProtection))
        {
            return false;
        }

        if (bytes != nullptr)
        {
            std::memcpy(target, bytes, size);
        }
        else
        {
            std::memset(target, fill, size);
        }

        bool success = FlushInstructionCache(
            GetCurrentProcess(),
            target,
            size) != FALSE;

        DWORD ignoredProtection = 0;
        if (!VirtualProtect(
            target,
            size,
            oldProtection,
            &ignoredProtection))
        {
            success = false;
        }
        return success;
    }

    bool WriteRelativeBranch(
        std::uintptr_t address,
        std::uintptr_t destination,
        unsigned char opcode) noexcept
    {
        if (address == 0 || destination == 0)
        {
            return false;
        }

        constexpr std::size_t InstructionSize = 5;
        const std::int64_t displacement =
            static_cast<std::int64_t>(destination)
            - static_cast<std::int64_t>(address)
            - static_cast<std::int64_t>(InstructionSize);
        if (displacement < std::numeric_limits<std::int32_t>::min()
            || displacement > std::numeric_limits<std::int32_t>::max())
        {
            return false;
        }

        std::array<unsigned char, InstructionSize> instruction{};
        instruction[0] = opcode;
        const std::int32_t relative =
            static_cast<std::int32_t>(displacement);
        std::memcpy(
            instruction.data() + 1,
            &relative,
            sizeof(relative));
        return dfl::memory_patch::WriteBytes(
            address,
            instruction.data(),
            instruction.size());
    }
}

namespace dfl::memory_patch
{
    bool WriteBytes(
        std::uintptr_t address,
        const void* bytes,
        std::size_t size) noexcept
    {
        if (size != 0 && bytes == nullptr)
        {
            return false;
        }
        return ChangeMemory(address, bytes, 0, size);
    }

    bool Fill(
        std::uintptr_t address,
        unsigned char value,
        std::size_t count) noexcept
    {
        if (count == 0)
        {
            return true;
        }
        return ChangeMemory(address, nullptr, value, count);
    }

    bool WriteNops(std::uintptr_t address, std::size_t count) noexcept
    {
        return Fill(address, 0x90, count);
    }

    bool WriteBreakpoints(std::uintptr_t address, std::size_t count) noexcept
    {
        return Fill(address, 0xCC, count);
    }

    bool WriteRelativeJump(
        std::uintptr_t address,
        std::uintptr_t destination) noexcept
    {
        return WriteRelativeBranch(address, destination, 0xE9);
    }

    bool WriteRelativeCall(
        std::uintptr_t address,
        std::uintptr_t destination) noexcept
    {
        return WriteRelativeBranch(address, destination, 0xE8);
    }
}
