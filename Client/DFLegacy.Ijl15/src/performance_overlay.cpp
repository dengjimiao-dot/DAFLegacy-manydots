#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <psapi.h>

#include <cstdint>
#include <cstdio>
#include <cstring>

#include "memory_patch.h"
#include "performance_overlay.h"

namespace
{
    constexpr std::uintptr_t DnfImageBase = 0x00400000;
    constexpr std::uintptr_t PerformanceDrawCall = 0x0083F996;
    constexpr std::uintptr_t OriginalPerformanceRenderer = 0x00836990;
    constexpr std::uintptr_t NativeMonitoringFlagOffset = 0x2F0;
    constexpr int OverlayX = 440;
    constexpr int FpsY = 35;
    constexpr int MemoryY = 48;
    // The 2008DF text renderer consumes colors as AABBGGRR. This encodes
    // the desired chat/channel RGB color E3C699 without the blue swap.
    constexpr std::uint32_t OverlayTextColor = 0xFF99C6E3u;

    using FpsFunction = double(__cdecl*)();
    using OriginalPerformanceFunction = int(__thiscall*)(void*);
    using BeginTextFunction = void(__thiscall*)(void*, const void*);
    using DrawTextFunction = int(__thiscall*)(
        void*,
        int,
        int,
        int,
        char*);

    // This is the 12-byte state consumed by dword_BF1648's +0x4C method.
    // It mirrors the object initialized at the start of sub_836990.
    struct TextState
    {
        std::uint8_t flags0;
        std::uint8_t flags1;
        std::int16_t color;
        std::int16_t mode;
        std::uint8_t flags2;
        std::uint8_t flags3;
        std::uint32_t font;
    };
    static_assert(sizeof(TextState) == 12, "Unexpected client text state size");

    double ReadFps() noexcept
    {
        const auto getFps = reinterpret_cast<FpsFunction>(0x00433610);
        return getFps();
    }

    void FormatCommittedMemory(char* buffer, std::size_t bufferSize) noexcept
    {
        PROCESS_MEMORY_COUNTERS_EX counters{};
        counters.cb = sizeof(counters);
        const BOOL success = GetProcessMemoryInfo(
            GetCurrentProcess(),
            reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters),
            sizeof(counters));
        if (success != FALSE)
        {
            constexpr double Megabyte = 1048576.0;
            const double committed =
                static_cast<double>(counters.PagefileUsage) / Megabyte;
            _snprintf_s(
                buffer,
                bufferSize,
                _TRUNCATE,
                "Committed : %.1f MB",
                committed);
            return;
        }

        _snprintf_s(
            buffer,
            bufferSize,
            _TRUNCATE,
            "Committed : ERROR");
    }

    int __fastcall DrawFpsAndMemory(void* performanceObject, void*) noexcept
    {
        // The original call site checks [this + 0x2F0] before entering
        // sub_836990. The outer gate is bypassed so IJL15 always runs, but
        // the native renderer is still selected by its original flag here.
        if (performanceObject != nullptr
            && *reinterpret_cast<const std::uint8_t*>(
                reinterpret_cast<const std::uint8_t*>(performanceObject)
                + NativeMonitoringFlagOffset) == 1)
        {
            const auto originalRenderer =
                reinterpret_cast<OriginalPerformanceFunction>(
                    OriginalPerformanceRenderer);
            originalRenderer(performanceObject);
        }

        // The call site passes the performance object in ECX. The renderer
        // used by sub_836990 is the separate global at 0x00BF1648.
        void* const renderer = *reinterpret_cast<void**>(0x00BF1648);
        if (renderer == nullptr)
        {
            return 0;
        }

        auto** vtable = *reinterpret_cast<void***>(renderer);
        if (vtable == nullptr)
        {
            return 0;
        }

        const auto beginText = reinterpret_cast<BeginTextFunction>(
            vtable[0x4C / sizeof(void*)]);
        const auto drawText = reinterpret_cast<DrawTextFunction>(
            vtable[0x54 / sizeof(void*)]);
        if (beginText == nullptr || drawText == nullptr)
        {
            return 0;
        }

        TextState state{};
        state.flags1 = 1;
        state.color = -1;
        state.mode = 1;
        state.font = *reinterpret_cast<const std::uint32_t*>(0x00B5B384);
        beginText(renderer, &state);

        char fpsText[64]{};
        _snprintf_s(
            fpsText,
            _TRUNCATE,
            "FPS : %d",
            static_cast<int>(ReadFps()));

        char memoryText[64]{};
        FormatCommittedMemory(memoryText, sizeof(memoryText));

        const int fpsResult = drawText(
            renderer,
            OverlayX,
            FpsY,
            static_cast<int>(OverlayTextColor),
            fpsText);
        drawText(
            renderer,
            OverlayX,
            MemoryY,
            static_cast<int>(OverlayTextColor),
            memoryText);
        return fpsResult;
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

namespace dfl::performance_overlay
{
    bool InstallCallHook() noexcept
    {
        std::uintptr_t currentDestination = 0;
        if (!GetCallDestination(PerformanceDrawCall, currentDestination))
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: performance call site is not E8.\n");
            return false;
        }

        const auto hookAddress = reinterpret_cast<std::uintptr_t>(
            &DrawFpsAndMemory);
        if (currentDestination == hookAddress)
        {
            return true;
        }
        if (currentDestination != OriginalPerformanceRenderer)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: performance call target does not match 2008DF.\n");
            return false;
        }
        if (!dfl::memory_patch::WriteRelativeCall(
                PerformanceDrawCall,
                hookAddress))
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: failed to install performance call hook.\n");
            return false;
        }
        return true;
    }
}
