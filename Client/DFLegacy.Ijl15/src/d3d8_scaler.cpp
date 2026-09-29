#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <cstddef>
#include <cstdint>
#include <cstring>

#include "d3d8_scaler.h"
#include "ijl15_config.h"
#include "memory_patch.h"

namespace
{
    static_assert(sizeof(void*) == sizeof(std::uint32_t));

    // These addresses are from the fixed-base 2008DF DNF.exe image. The
    // process gate in dnf_patches.cpp rejects a rebased or different image.
    constexpr std::uintptr_t DnfImageBase = 0x00400000;
    constexpr std::uintptr_t Direct3DCreate8Iat = 0x00F397D8;

    constexpr std::size_t D3D8VtableEntries = 16;
    constexpr std::size_t DeviceVtableEntries = 104;
    constexpr std::size_t CreateDeviceIndex = 15;
    constexpr std::size_t SetTextureStageStateIndex = 63;

    // D3DTEXTURESTAGESTATETYPE values from d3d8types.h.
    constexpr DWORD TextureMagFilter = 16;
    constexpr DWORD TextureMinFilter = 17;
    constexpr DWORD TextureMipFilter = 18;

    // D3DTEXTUREFILTERTYPE values from d3d8types.h.
    constexpr DWORD FilterPoint = 1;
    constexpr DWORD FilterLinear = 2;
    constexpr DWORD FilterAnisotropic = 3;

    using Direct3DCreate8Function = void* (WINAPI*)(UINT sdkVersion);
    using CreateDeviceFunction = HRESULT (WINAPI*)(
        void* d3d8,
        UINT adapter,
        UINT deviceType,
        HWND focusWindow,
        DWORD behaviorFlags,
        void* presentationParameters,
        void** device);
    using SetTextureStageStateFunction = HRESULT (WINAPI*)(
        void* device,
        DWORD stage,
        DWORD type,
        DWORD value);

    Direct3DCreate8Function OriginalDirect3DCreate8 = nullptr;
    CreateDeviceFunction OriginalCreateDevice = nullptr;
    SetTextureStageStateFunction OriginalSetTextureStageState = nullptr;
    bool IatHookInstalled = false;

    void* FunctionAddress(void* function) noexcept
    {
        return function;
    }

    bool ReplaceObjectVtable(
        void* object,
        void** replacement) noexcept
    {
        if (object == nullptr || replacement == nullptr)
        {
            return false;
        }

        void** const vtableStorage = reinterpret_cast<void**>(object);
        DWORD oldProtection = 0;
        if (!VirtualProtect(
                vtableStorage,
                sizeof(void*),
                PAGE_READWRITE,
                &oldProtection))
        {
            return false;
        }
        *vtableStorage = replacement;
        DWORD ignoredProtection = 0;
        const BOOL restored = VirtualProtect(
            vtableStorage,
            sizeof(void*),
            oldProtection,
            &ignoredProtection);
        // The pointer has already been replaced. Do not report failure after
        // a successful write, because the caller would free the live clone
        // and leave the COM object with a dangling vtable.
        (void)restored;
        return true;
    }

    void** CloneVtable(
        void* object,
        std::size_t entryCount) noexcept
    {
        if (object == nullptr || entryCount == 0)
        {
            return nullptr;
        }

        void** const original = *reinterpret_cast<void***>(object);
        if (original == nullptr)
        {
            return nullptr;
        }

        const std::size_t byteCount = entryCount * sizeof(void*);
        void** const copy = static_cast<void**>(HeapAlloc(
            GetProcessHeap(),
            HEAP_ZERO_MEMORY,
            byteCount));
        if (copy == nullptr)
        {
            return nullptr;
        }

        std::memcpy(copy, original, byteCount);
        return copy;
    }

    DWORD FilterValue(
        dfl::ijl15_config::ScaleFilterMode mode) noexcept
    {
        switch (mode)
        {
        case dfl::ijl15_config::ScaleFilterMode::Point:
            return FilterPoint;
        case dfl::ijl15_config::ScaleFilterMode::Anisotropic:
            return FilterAnisotropic;
        case dfl::ijl15_config::ScaleFilterMode::Native:
        case dfl::ijl15_config::ScaleFilterMode::Linear:
        default:
            return FilterLinear;
        }
    }

    HRESULT WINAPI SetTextureStageStateProxy(
        void* device,
        DWORD stage,
        DWORD type,
        DWORD value)
    {
        if (OriginalSetTextureStageState == nullptr)
        {
            return E_FAIL;
        }

        const auto mode = dfl::ijl15_config::Get().ScaleFilter;
        if (stage == 0
            && (type == TextureMagFilter
                || type == TextureMinFilter
                || type == TextureMipFilter))
        {
            if (mode != dfl::ijl15_config::ScaleFilterMode::Native)
            {
                value = FilterValue(mode);
            }
        }

        HRESULT result = OriginalSetTextureStageState(
            device,
            stage,
            type,
            value);
        if (FAILED(result)
            && mode == dfl::ijl15_config::ScaleFilterMode::Anisotropic
            && stage == 0
            && (type == TextureMagFilter
                || type == TextureMinFilter
                || type == TextureMipFilter))
        {
            // Old D3D8 drivers may not expose anisotropic filtering. Keep
            // the client usable by falling back to the guaranteed linear
            // sampler instead of propagating the optional failure.
            result = OriginalSetTextureStageState(
                device,
                stage,
                type,
                FilterLinear);
        }
        return result;
    }

    bool InstallDeviceHook(void* device) noexcept
    {
        if (device == nullptr)
        {
            return false;
        }

        void** const original = *reinterpret_cast<void***>(device);
        if (original == nullptr)
        {
            return false;
        }
        if (original[SetTextureStageStateIndex]
            == FunctionAddress(reinterpret_cast<void*>(
                &SetTextureStageStateProxy)))
        {
            return true;
        }

        void** const replacement = CloneVtable(
            device,
            DeviceVtableEntries);
        if (replacement == nullptr)
        {
            return false;
        }

        OriginalSetTextureStageState =
            reinterpret_cast<SetTextureStageStateFunction>(
                original[SetTextureStageStateIndex]);
        replacement[SetTextureStageStateIndex] =
            FunctionAddress(reinterpret_cast<void*>(
                &SetTextureStageStateProxy));
        if (!ReplaceObjectVtable(device, replacement))
        {
            HeapFree(GetProcessHeap(), 0, replacement);
            OriginalSetTextureStageState = nullptr;
            return false;
        }
        return true;
    }

    HRESULT WINAPI CreateDeviceProxy(
        void* d3d8,
        UINT adapter,
        UINT deviceType,
        HWND focusWindow,
        DWORD behaviorFlags,
        void* presentationParameters,
        void** device)
    {
        if (OriginalCreateDevice == nullptr)
        {
            return E_FAIL;
        }

        const HRESULT result = OriginalCreateDevice(
            d3d8,
            adapter,
            deviceType,
            focusWindow,
            behaviorFlags,
            presentationParameters,
            device);
        if (SUCCEEDED(result)
            && device != nullptr
            && *device != nullptr)
        {
            if (!InstallDeviceHook(*device))
            {
                OutputDebugStringA(
                    "DFLegacy.Ijl15: D3D8 device filter hook was not installed.\n");
            }
        }
        return result;
    }

    void HookD3D8Object(void* d3d8) noexcept
    {
        if (d3d8 == nullptr)
        {
            return;
        }

        void** const original = *reinterpret_cast<void***>(d3d8);
        if (original == nullptr
            || original[CreateDeviceIndex]
                == FunctionAddress(reinterpret_cast<void*>(
                    &CreateDeviceProxy)))
        {
            return;
        }

        void** const replacement = CloneVtable(
            d3d8,
            D3D8VtableEntries);
        if (replacement == nullptr)
        {
            return;
        }

        OriginalCreateDevice = reinterpret_cast<CreateDeviceFunction>(
            original[CreateDeviceIndex]);
        replacement[CreateDeviceIndex] =
            FunctionAddress(reinterpret_cast<void*>(
                &CreateDeviceProxy));
        if (!ReplaceObjectVtable(d3d8, replacement))
        {
            HeapFree(GetProcessHeap(), 0, replacement);
            OriginalCreateDevice = nullptr;
        }
    }

    void* WINAPI Direct3DCreate8Proxy(UINT sdkVersion)
    {
        if (OriginalDirect3DCreate8 == nullptr)
        {
            return nullptr;
        }

        void* const d3d8 = OriginalDirect3DCreate8(sdkVersion);
        HookD3D8Object(d3d8);
        return d3d8;
    }
}

namespace dfl::d3d8_scaler
{
    bool Install() noexcept
    {
        if (dfl::ijl15_config::Get().ScaleFilter
            == dfl::ijl15_config::ScaleFilterMode::Native)
        {
            return true;
        }
        if (IatHookInstalled)
        {
            return true;
        }

        HMODULE const executable = GetModuleHandleW(nullptr);
        if (executable == nullptr
            || reinterpret_cast<std::uintptr_t>(executable) != DnfImageBase)
        {
            return false;
        }

        auto* const iat = reinterpret_cast<Direct3DCreate8Function*>(
            Direct3DCreate8Iat);
        if (iat == nullptr || *iat == nullptr)
        {
            return false;
        }
        if (*iat == &Direct3DCreate8Proxy)
        {
            IatHookInstalled = true;
            return true;
        }

        OriginalDirect3DCreate8 = *iat;
        const auto proxy = &Direct3DCreate8Proxy;
        if (!dfl::memory_patch::WriteBytes(
                Direct3DCreate8Iat,
                &proxy,
                sizeof(proxy)))
        {
            OriginalDirect3DCreate8 = nullptr;
            return false;
        }

        IatHookInstalled = true;
        OutputDebugStringA(
            "DFLegacy.Ijl15: D3D8 scale filter hook installed.\n");
        return true;
    }
}
