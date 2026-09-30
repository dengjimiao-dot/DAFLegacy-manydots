#pragma once

#include <cstddef>
#include <cstdint>

namespace dfl::memory_patch
{
    bool WriteBytes(
        std::uintptr_t address,
        const void* bytes,
        std::size_t size) noexcept;

    bool Fill(
        std::uintptr_t address,
        unsigned char value,
        std::size_t count) noexcept;

    bool WriteNops(std::uintptr_t address, std::size_t count) noexcept;
    bool WriteBreakpoints(std::uintptr_t address, std::size_t count) noexcept;

    bool WriteRelativeJump(
        std::uintptr_t address,
        std::uintptr_t destination) noexcept;

    bool WriteRelativeCall(
        std::uintptr_t address,
        std::uintptr_t destination) noexcept;
}
