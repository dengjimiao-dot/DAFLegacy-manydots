#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>

#include "ijl15_config.h"
#include "memory_patch.h"
#include "window_scaling.h"

namespace
{
    struct ImmediatePatch
    {
        std::uintptr_t Address;
        std::uint32_t OriginalValue;
        std::uint32_t ReplacementValue;
        const unsigned char* Instruction;
        std::size_t InstructionSize;
    };

    constexpr std::array<unsigned char, 7> MainWidthInstruction{
        0xC7, 0x46, 0x2C, 0x20, 0x03, 0x00, 0x00};
    constexpr std::array<unsigned char, 7> MainHeightInstruction{
        0xC7, 0x46, 0x30, 0x58, 0x02, 0x00, 0x00};
    constexpr std::array<unsigned char, 5> OuterWindowHeightInstruction{
        0x68, 0xE0, 0x01, 0x00, 0x00};
    constexpr std::array<unsigned char, 5> OuterWindowWidthInstruction{
        0x68, 0x80, 0x02, 0x00, 0x00};

    constexpr std::uintptr_t MainWidthImmediate = 0x008F7B43;
    constexpr std::uintptr_t MainHeightImmediate = 0x008F7B4F;
    constexpr std::uintptr_t OuterWindowHeightImmediate = 0x0040B73E;
    constexpr std::uintptr_t OuterWindowWidthImmediate = 0x0040B743;

    // Do not add the following two internal D3D arguments to this table:
    // 0x0040B766 (640) and 0x0040B76B (480) are deliberately left intact.

    bool IsCurrentValue(
        std::uintptr_t address,
        std::uint32_t value) noexcept
    {
        std::uint32_t current = 0;
        std::memcpy(
            &current,
            reinterpret_cast<const void*>(address),
            sizeof(current));
        return current == value;
    }

    bool PatchImmediate(const ImmediatePatch& patch) noexcept
    {
        const auto instructionAddress = patch.Address
            - (patch.InstructionSize == 7 ? 3 : 1);
        if (std::memcmp(
                reinterpret_cast<const void*>(instructionAddress),
                patch.Instruction,
                patch.InstructionSize) != 0)
        {
            return false;
        }

        if (IsCurrentValue(patch.Address, patch.ReplacementValue))
        {
            return true;
        }
        if (!IsCurrentValue(patch.Address, patch.OriginalValue))
        {
            return false;
        }

        return dfl::memory_patch::WriteBytes(
            patch.Address,
            &patch.ReplacementValue,
            sizeof(patch.ReplacementValue));
    }

    void RestoreImmediate(const ImmediatePatch& patch) noexcept
    {
        dfl::memory_patch::WriteBytes(
            patch.Address,
            &patch.OriginalValue,
            sizeof(patch.OriginalValue));
    }
}

namespace dfl::window_scaling
{
    bool Install() noexcept
    {
        const auto& settings = dfl::ijl15_config::Get();
        const std::array<ImmediatePatch, 4> patches{
            ImmediatePatch{
                MainWidthImmediate,
                800,
                static_cast<std::uint32_t>(settings.WindowWidth),
                MainWidthInstruction.data(),
                MainWidthInstruction.size()},
            ImmediatePatch{
                MainHeightImmediate,
                600,
                static_cast<std::uint32_t>(settings.WindowHeight),
                MainHeightInstruction.data(),
                MainHeightInstruction.size()},
            ImmediatePatch{
                OuterWindowHeightImmediate,
                480,
                static_cast<std::uint32_t>(settings.WindowHeight),
                OuterWindowHeightInstruction.data(),
                OuterWindowHeightInstruction.size()},
            ImmediatePatch{
                OuterWindowWidthImmediate,
                640,
                static_cast<std::uint32_t>(settings.WindowWidth),
                OuterWindowWidthInstruction.data(),
                OuterWindowWidthInstruction.size()}};

        std::array<bool, patches.size()> changed{};
        for (std::size_t index = 0; index < patches.size(); ++index)
        {
            const auto& patch = patches[index];
            const bool wasReplacement = IsCurrentValue(
                patch.Address,
                patch.ReplacementValue);
            if (!PatchImmediate(patch))
            {
                for (std::size_t rollback = 0; rollback < index; ++rollback)
                {
                    if (changed[rollback])
                    {
                        RestoreImmediate(patches[rollback]);
                    }
                }
                OutputDebugStringA(
                    "DFLegacy.Ijl15: window scaling bytes do not match 2008DF.\n");
                return false;
            }
            changed[index] = !wasReplacement;
        }

        OutputDebugStringA(
            "DFLegacy.Ijl15: outer window scaling configured; internal render remains 640x480.\n");
        return true;
    }
}
