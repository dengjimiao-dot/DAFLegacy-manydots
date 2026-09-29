#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

#include "memory_patch.h"
#include "mouse_wheel.h"

namespace
{
    constexpr std::uintptr_t DispatchMessageIat = 0x0099846C;
    constexpr std::uintptr_t NativeMouseInput = 0x008FCAA0;

    constexpr std::uintptr_t ScrollInitFunction = 0x008F49F0;
    constexpr std::size_t ScrollInitStolenPrologueSize = 6;
    constexpr std::array<unsigned char, ScrollInitStolenPrologueSize>
        OriginalScrollInitPrologue{
            0x55,
            0x8B,
            0xEC,
            0x56,
            0x8B,
            0xF1 };

    // The CNScroll wrapper has several accessors around 0x8F30xx, but those
    // accessors address a different list-data layout.  The scrollbar model
    // used by the 2008DF UI has vtable A7D690 and stores the current/max
    // positions at +0x74/+0x78.  Both arrow clicks and the real thumb-drag
    // path ultimately call sub_8EE350 on this model.
    constexpr std::uintptr_t ScrollModelVtable = 0x00A7D690;
    // Track/thumb child objects created by the 2008DF list controls use this
    // vtable. Only this known child layout has the visibility flag at +4;
    // other child pointers are different UI object types.
    constexpr std::uintptr_t ScrollControlVtable = 0x00A8EFE4;
    constexpr std::uintptr_t ScrollModelSetFunction = 0x008EE350;
    constexpr std::size_t ScrollModelReadableSize = 0x84;
    constexpr std::size_t ScrollModelCurrentOffset = 0x74;
    constexpr std::size_t ScrollModelMaximumOffset = 0x78;
    constexpr std::size_t ScrollModelTrackOffset = 0x6C;
    constexpr std::size_t ScrollModelThumbOffset = 0x70;

    // These are the same coordinate offsets used by sub_8FCAA0 when it
    // converts a WM_MOUSE message into the client's game-window coordinates.
    constexpr std::uintptr_t MouseCoordinateXOffset = 0x00CFD210;
    constexpr std::uintptr_t MouseCoordinateYOffset = 0x00CFD214;

    constexpr std::size_t MaximumScrollEntries = 256;
    constexpr int StandardWheelDelta = 120;
    // 2008DF places vertical CNScroll controls at the edge of a list. The
    // control rectangle itself is only about 11 pixels wide, so wheel input
    // from the adjacent list needs a separate viewport hit region.
    constexpr int MaximumListViewportWidth = 320;
    constexpr int MaximumVerticalScrollWidth = 16;
    constexpr int MinimumVerticalScrollHeight = 16;

    using DispatchMessageFunction = LRESULT(WINAPI*)(const MSG*);
    using ScrollInitFunctionType = int(__thiscall*)(
        void*,
        std::uintptr_t,
        std::uintptr_t,
        std::uintptr_t);
    using ScrollModelSetFunctionType = int(__thiscall*)(void*, int);
    using NativeMouseInputFunction = char(__cdecl*)(
        unsigned int,
        std::uintptr_t,
        std::uintptr_t);

    struct ScrollEntry
    {
        void* Object;
    };

    static_assert(sizeof(void*) == sizeof(std::uint32_t));

    DispatchMessageFunction OriginalDispatchMessage = nullptr;
    ScrollInitFunctionType OriginalScrollInit = nullptr;
    unsigned char* ScrollInitTrampoline = nullptr;
    bool DispatchHookInstalled = false;
    bool ScrollInitHookInstalled = false;

    std::array<ScrollEntry, MaximumScrollEntries> ScrollEntries{};
    int WheelAccumulator = 0;

    bool BuildRelativeJump(
        std::uintptr_t address,
        std::uintptr_t destination,
        unsigned char* output) noexcept
    {
        if (output == nullptr)
        {
            return false;
        }

        constexpr std::int64_t BranchSize = 5;
        const std::int64_t displacement =
            static_cast<std::int64_t>(destination)
            - static_cast<std::int64_t>(address)
            - BranchSize;
        if (displacement < std::numeric_limits<std::int32_t>::min()
            || displacement > std::numeric_limits<std::int32_t>::max())
        {
            return false;
        }

        output[0] = 0xE9;
        const auto relative = static_cast<std::int32_t>(displacement);
        std::memcpy(output + 1, &relative, sizeof(relative));
        return true;
    }

    bool IsReadable(
        const void* address,
        std::size_t size) noexcept
    {
        if (address == nullptr || size == 0)
        {
            return false;
        }

        MEMORY_BASIC_INFORMATION information{};
        if (VirtualQuery(
            address,
            &information,
            sizeof(information)) != sizeof(information))
        {
            return false;
        }

        if (information.State != MEM_COMMIT
            || (information.Protect & PAGE_GUARD) != 0
            || (information.Protect & PAGE_NOACCESS) != 0)
        {
            return false;
        }

        const auto regionStart = reinterpret_cast<std::uintptr_t>(
            information.BaseAddress);
        const auto regionEnd = regionStart + information.RegionSize;
        const auto addressValue = reinterpret_cast<std::uintptr_t>(address);
        if (addressValue < regionStart
            || addressValue > regionEnd
            || size > regionEnd - addressValue)
        {
            return false;
        }
        return true;
    }

    template<typename T>
    bool ReadValue(
        std::uintptr_t address,
        T& value) noexcept
    {
        if (!IsReadable(reinterpret_cast<const void*>(address), sizeof(T)))
        {
            return false;
        }
        std::memcpy(&value, reinterpret_cast<const void*>(address), sizeof(T));
        return true;
    }

    bool GetIatValue(
        DispatchMessageFunction& function) noexcept
    {
        std::uintptr_t value = 0;
        if (!ReadValue(DispatchMessageIat, value))
        {
            return false;
        }
        function = reinterpret_cast<DispatchMessageFunction>(value);
        return function != nullptr;
    }

    void RemoveScrollEntry(std::size_t index) noexcept
    {
        ScrollEntries[index].Object = nullptr;
    }

    bool IsScrollModelCandidate(void* object) noexcept
    {
        if (!IsReadable(object, ScrollModelReadableSize))
        {
            return false;
        }

        std::uintptr_t vtable = 0;
        if (!ReadValue(
            reinterpret_cast<std::uintptr_t>(object),
            vtable)
            || vtable != ScrollModelVtable)
        {
            return false;
        }
        return true;
    }

    bool IsScrollObjectUsable(void* object) noexcept
    {
        if (!IsScrollModelCandidate(object))
        {
            return false;
        }

        unsigned char active = 0;
        if (!ReadValue(
            reinterpret_cast<std::uintptr_t>(object) + 4,
            active)
            || active == 0)
        {
            return false;
        }

        std::int32_t current = 0;
        std::int32_t maximum = 0;
        const auto base = reinterpret_cast<std::uintptr_t>(object);
        if (!ReadValue(base + ScrollModelCurrentOffset, current)
            || !ReadValue(base + ScrollModelMaximumOffset, maximum)
            || current < 0
            || maximum < 0
            || current > maximum)
        {
            return false;
        }

        return true;
    }

    bool IsScrollModelVisible(void* object) noexcept
    {
        if (!IsScrollModelCandidate(object))
        {
            return false;
        }

        const auto base = reinterpret_cast<std::uintptr_t>(object);
        bool foundKnownChild = false;
        bool hasVisibleKnownChild = false;
        constexpr std::array<std::size_t, 2> ChildOffsets{
            ScrollModelTrackOffset,
            ScrollModelThumbOffset };
        for (const std::size_t childOffset : ChildOffsets)
        {
            std::uintptr_t child = 0;
            if (!ReadValue(base + childOffset, child) || child == 0)
            {
                continue;
            }

            std::uintptr_t childVtable = 0;
            if (!ReadValue(child, childVtable))
            {
                continue;
            }

            // A nested A7D690 object is the duplicate model observed in the
            // overlapping controls. It must not win over the real list.
            if (childVtable == ScrollModelVtable)
            {
                return false;
            }

            // Do not interpret +4 for arbitrary child objects. Only the
            // known A8EFE4 track/thumb layout has the active/visible byte at
            // that offset; other valid list models use different layouts.
            if (childVtable != ScrollControlVtable)
            {
                continue;
            }

            unsigned char active = 0;
            if (!ReadValue(child + 4, active))
            {
                continue;
            }
            foundKnownChild = true;
            hasVisibleKnownChild = hasVisibleKnownChild || active != 0;
        }

        // If there are no known-layout children, retain the model. This is
        // required for controls whose children are initialized lazily or use
        // another UI class layout.
        return !foundKnownChild || hasVisibleKnownChild;
    }

    bool CanScrollByOne(
        void* object,
        int direction) noexcept
    {
        if (!IsScrollObjectUsable(object))
        {
            return false;
        }

        const auto base = reinterpret_cast<std::uintptr_t>(object);
        std::int32_t current = 0;
        std::int32_t maximum = 0;
        if (!ReadValue(base + ScrollModelCurrentOffset, current)
            || !ReadValue(base + ScrollModelMaximumOffset, maximum))
        {
            return false;
        }

        return direction < 0 ? current > 0 : current < maximum;
    }

    void RegisterScrollObject(void* object) noexcept
    {
        // The initializer's fourth argument is the common A7D690 model.  Do
        // not retain the CNScroll wrapper: its +0x6C/+0x74 fields are child
        // controls and are not the position pair used by 0x8EE350.
        if (!IsScrollModelCandidate(object))
        {
            return;
        }

        for (const ScrollEntry& entry : ScrollEntries)
        {
            if (entry.Object == object)
            {
                return;
            }
        }

        for (ScrollEntry& entry : ScrollEntries)
        {
            if (entry.Object == nullptr)
            {
                entry.Object = object;
                return;
            }
        }
    }

    int ReadCoordinateOffset(std::uintptr_t address) noexcept
    {
        std::int32_t value = 0;
        return ReadValue(address, value) ? value : 0;
    }

    bool GetScrollBounds(
        void* object,
        int& left,
        int& top,
        int& width,
        int& height) noexcept
    {
        const auto* bytes = static_cast<const unsigned char*>(object);
        std::int32_t objectLeft = 0;
        std::int32_t objectTop = 0;
        std::int32_t objectWidth = 0;
        std::int32_t objectHeight = 0;
        std::memcpy(&objectLeft, bytes + 0x14, sizeof(objectLeft));
        std::memcpy(&objectTop, bytes + 0x18, sizeof(objectTop));
        std::memcpy(&objectWidth, bytes + 0x1C, sizeof(objectWidth));
        std::memcpy(&objectHeight, bytes + 0x20, sizeof(objectHeight));

        // Reject uninitialized or corrupt layout values before using them as
        // a hit rectangle. These limits cover the 800x600 2008DF client and
        // leave room for the window's coordinate offsets.
        if (objectLeft < -4096 || objectLeft > 8192
            || objectTop < -4096 || objectTop > 8192
            || objectWidth <= 0 || objectWidth > 8192
            || objectHeight <= 0 || objectHeight > 8192)
        {
            return false;
        }

        left = objectLeft;
        top = objectTop;
        width = objectWidth;
        height = objectHeight;
        return true;
    }

    bool HitTest(
        void* object,
        int x,
        int y) noexcept
    {
        int left = 0;
        int top = 0;
        int width = 0;
        int height = 0;
        if (!GetScrollBounds(object, left, top, width, height))
        {
            return false;
        }

        return x >= left
            && x < left + width
            && y >= top
            && y < top + height;
    }

    bool GetListViewportBounds(
        void* object,
        int& left,
        int& top,
        int& width,
        int& height) noexcept
    {
        int scrollLeft = 0;
        int scrollTop = 0;
        int scrollWidth = 0;
        int scrollHeight = 0;
        if (!GetScrollBounds(
            object,
            scrollLeft,
            scrollTop,
            scrollWidth,
            scrollHeight)
            || scrollWidth > MaximumVerticalScrollWidth
            || scrollHeight < MinimumVerticalScrollHeight)
        {
            return false;
        }

        // Registered 2008DF vertical bars are either at the left edge of a
        // list (x <= 32) or at its right edge. Extend only toward the list,
        // never across the bar, so adjacent panels do not steal events.
        if (scrollLeft <= 32)
        {
            left = scrollLeft;
            width = scrollWidth + MaximumListViewportWidth;
        }
        else
        {
            left = scrollLeft - MaximumListViewportWidth;
            if (left < 0)
            {
                left = 0;
            }
            width = scrollLeft + scrollWidth - left;
        }

        top = scrollTop;
        height = scrollHeight;
        return width > 0 && height > 0;
    }

    int HorizontalDistanceToScrollBar(
        void* object,
        int x) noexcept
    {
        int left = 0;
        int top = 0;
        int width = 0;
        int height = 0;
        if (!GetScrollBounds(object, left, top, width, height))
        {
            return std::numeric_limits<int>::max();
        }

        if (x < left)
        {
            return left - x;
        }
        if (x >= left + width)
        {
            return x - (left + width - 1);
        }
        return 0;
    }

    bool SetScrollByOne(
        void* object,
        int direction) noexcept
    {
        if (!IsScrollObjectUsable(object))
        {
            return false;
        }

        const auto base = reinterpret_cast<std::uintptr_t>(object);
        std::int32_t current = 0;
        std::int32_t maximum = 0;
        if (!ReadValue(base + ScrollModelCurrentOffset, current)
            || !ReadValue(base + ScrollModelMaximumOffset, maximum))
        {
            return false;
        }

        int next = current;
        if (direction < 0)
        {
            if (current == 0)
            {
                return false;
            }
            next = current - 1;
        }
        else
        {
            if (current >= maximum)
            {
                return false;
            }
            next = current + 1;
        }

        const auto setFunction = reinterpret_cast<
            ScrollModelSetFunctionType>(ScrollModelSetFunction);
        setFunction(object, next);
        return next != current;
    }

    bool ScrollAtCursor(
        int x,
        int y,
        int direction) noexcept
    {
        void* contentObject = nullptr;
        bool contentVisible = false;
        bool contentMovable = false;
        int contentDistance = std::numeric_limits<int>::max();

        for (std::size_t index = ScrollEntries.size(); index != 0; --index)
        {
            ScrollEntry& entry = ScrollEntries[index - 1];
            void* const object = entry.Object;
            if (object == nullptr)
            {
                continue;
            }
            if (!IsScrollObjectUsable(object))
            {
                RemoveScrollEntry(index - 1);
                continue;
            }

            // Preserve the native control hit first. A direct hit has
            // precedence over any wider list viewport.
            if (HitTest(object, x, y)
                && IsScrollModelVisible(object))
            {
                // A hit control owns this wheel event even when it is already
                // at an end stop. This prevents a hidden/underlying control
                // from moving when the visible control cannot move further.
                SetScrollByOne(object, direction);
                return true;
            }

            int viewportLeft = 0;
            int viewportTop = 0;
            int viewportWidth = 0;
            int viewportHeight = 0;
            if (!GetListViewportBounds(
                object,
                viewportLeft,
                viewportTop,
                viewportWidth,
                viewportHeight)
                || x < viewportLeft
                || x >= viewportLeft + viewportWidth
                || y < viewportTop
                || y >= viewportTop + viewportHeight)
            {
                continue;
            }

            const bool visible = IsScrollModelVisible(object);
            const bool movable = CanScrollByOne(object, direction);
            const int distance = HorizontalDistanceToScrollBar(object, x);
            const bool betterCandidate =
                contentObject == nullptr
                || (visible && !contentVisible)
                || (visible == contentVisible
                    && movable && !contentMovable)
                || (visible == contentVisible
                    && movable == contentMovable
                    && distance < contentDistance);
            if (betterCandidate)
            {
                contentObject = object;
                contentVisible = visible;
                contentMovable = movable;
                contentDistance = distance;
            }
        }

        if (contentObject != nullptr)
        {
            // A list-content hit owns the event just like a direct bar hit.
            // The native setter clamps end stops; don't pass the event to an
            // overlapping/underlying list when this one cannot move.
            SetScrollByOne(contentObject, direction);
            return true;
        }

        return false;
    }

    bool HandleWheelMessage(const MSG& message) noexcept
    {
        const int delta = static_cast<short>(HIWORD(message.wParam));
        if (delta == 0)
        {
            return false;
        }

        // WM_MOUSEWHEEL stores the cursor position in screen coordinates,
        // while the 2008DF mouse dispatcher receives client coordinates. The
        // scroll-control rectangles use that same client-space convention;
        // convert before applying the client's viewport offsets.
        POINT cursor{
            static_cast<LONG>(static_cast<short>(LOWORD(message.lParam))),
            static_cast<LONG>(static_cast<short>(HIWORD(message.lParam))) };
        if (message.hwnd == nullptr || !ScreenToClient(message.hwnd, &cursor))
        {
            return false;
        }

        const int x = static_cast<int>(cursor.x)
            + ReadCoordinateOffset(MouseCoordinateXOffset);
        const int y = static_cast<int>(cursor.y)
            + ReadCoordinateOffset(MouseCoordinateYOffset);

        WheelAccumulator += delta;
        bool handled = false;
        int iterations = 0;
        while (WheelAccumulator >= StandardWheelDelta
            && iterations < 16)
        {
            handled = ScrollAtCursor(x, y, -1) || handled;
            WheelAccumulator -= StandardWheelDelta;
            ++iterations;
        }
        while (WheelAccumulator <= -StandardWheelDelta
            && iterations < 16)
        {
            handled = ScrollAtCursor(x, y, 1) || handled;
            WheelAccumulator += StandardWheelDelta;
            ++iterations;
        }

        return handled;
    }

    int __fastcall ScrollInitHook(
        void* object,
        void*,
        std::uintptr_t arg0,
        std::uintptr_t arg1,
        std::uintptr_t arg2) noexcept
    {
        int result = 0;
        const auto original = reinterpret_cast<ScrollInitFunctionType>(
            OriginalScrollInit);
        if (original != nullptr)
        {
            result = original(object, arg0, arg1, arg2);
        }
        RegisterScrollObject(reinterpret_cast<void*>(arg2));
        return result;
    }

    LRESULT WINAPI DispatchMessageHook(const MSG* message) noexcept
    {
        if (message != nullptr && message->message == WM_MOUSEWHEEL)
        {
            const bool handled = HandleWheelMessage(*message);
            if (!handled)
            {
                // Keep compatibility with any 2008DF UI callback that may
                // already understand 0x20A in a particular window. The
                // normal WndProc still receives the original message below.
                const auto nativeInput = reinterpret_cast<
                    NativeMouseInputFunction>(NativeMouseInput);
                nativeInput(
                    WM_MOUSEWHEEL,
                    message->wParam,
                    message->lParam);
            }
        }

        return OriginalDispatchMessage == nullptr
            ? 0
            : OriginalDispatchMessage(message);
    }

    bool InstallScrollInitHook() noexcept
    {
        if (ScrollInitHookInstalled)
        {
            return true;
        }

        auto* const target = reinterpret_cast<unsigned char*>(
            ScrollInitFunction);
        if (std::memcmp(
            target,
            OriginalScrollInitPrologue.data(),
            OriginalScrollInitPrologue.size()) != 0)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: CNScroll initializer bytes do not match 2008DF.\n");
            return false;
        }

        constexpr std::size_t TrampolineSize =
            ScrollInitStolenPrologueSize + 5;
        auto* const trampoline = static_cast<unsigned char*>(VirtualAlloc(
            nullptr,
            TrampolineSize,
            MEM_COMMIT | MEM_RESERVE,
            PAGE_EXECUTE_READWRITE));
        if (trampoline == nullptr)
        {
            return false;
        }

        std::memcpy(
            trampoline,
            target,
            ScrollInitStolenPrologueSize);
        if (!BuildRelativeJump(
            reinterpret_cast<std::uintptr_t>(trampoline)
            + ScrollInitStolenPrologueSize,
            ScrollInitFunction + ScrollInitStolenPrologueSize,
            trampoline + ScrollInitStolenPrologueSize))
        {
            VirtualFree(trampoline, 0, MEM_RELEASE);
            return false;
        }
        FlushInstructionCache(
            GetCurrentProcess(),
            trampoline,
            TrampolineSize);

        std::array<unsigned char, ScrollInitStolenPrologueSize> replacement{};
        if (!BuildRelativeJump(
            ScrollInitFunction,
            reinterpret_cast<std::uintptr_t>(&ScrollInitHook),
            replacement.data()))
        {
            VirtualFree(trampoline, 0, MEM_RELEASE);
            return false;
        }
        for (std::size_t index = 5;
            index < replacement.size();
            ++index)
        {
            replacement[index] = 0x90;
        }

        if (!dfl::memory_patch::WriteBytes(
            ScrollInitFunction,
            replacement.data(),
            replacement.size()))
        {
            VirtualFree(trampoline, 0, MEM_RELEASE);
            return false;
        }

        ScrollInitTrampoline = trampoline;
        OriginalScrollInit = reinterpret_cast<ScrollInitFunctionType>(
            trampoline);
        ScrollInitHookInstalled = true;
        return true;
    }

    bool InstallDispatchMessageHook() noexcept
    {
        DispatchMessageFunction current = nullptr;
        if (!GetIatValue(current))
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: DispatchMessageA IAT entry is unavailable.\n");
            return false;
        }

        const auto hook = reinterpret_cast<DispatchMessageFunction>(
            &DispatchMessageHook);
        if (current == hook)
        {
            DispatchHookInstalled = true;
            return true;
        }

        OriginalDispatchMessage = current;
        const std::uintptr_t hookAddress = reinterpret_cast<std::uintptr_t>(
            hook);
        if (!dfl::memory_patch::WriteBytes(
            DispatchMessageIat,
            &hookAddress,
            sizeof(hookAddress)))
        {
            OriginalDispatchMessage = nullptr;
            return false;
        }

        DispatchHookInstalled = true;
        return true;
    }
}

namespace dfl::mouse_wheel
{
    bool InstallHook() noexcept
    {
        if (DispatchHookInstalled && ScrollInitHookInstalled)
        {
            return true;
        }

        if (!InstallScrollInitHook())
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: failed to install CNScroll registry hook.\n");
            return false;
        }

        if (!InstallDispatchMessageHook())
        {
            if (ScrollInitTrampoline != nullptr)
            {
                dfl::memory_patch::WriteBytes(
                    ScrollInitFunction,
                    OriginalScrollInitPrologue.data(),
                    OriginalScrollInitPrologue.size());
                VirtualFree(ScrollInitTrampoline, 0, MEM_RELEASE);
                ScrollInitTrampoline = nullptr;
                OriginalScrollInit = nullptr;
                ScrollInitHookInstalled = false;
            }
            OutputDebugStringA(
                "DFLegacy.Ijl15: failed to install DispatchMessageA wheel hook.\n");
            return false;
        }

        OutputDebugStringA(
            "DFLegacy.Ijl15: mouse-wheel CNScroll hook installed.\n");
        return true;
    }

    bool RestoreHook() noexcept
    {
        bool success = true;
        if (DispatchHookInstalled && OriginalDispatchMessage != nullptr)
        {
            DispatchMessageFunction current = nullptr;
            GetIatValue(current);
            if (current == reinterpret_cast<DispatchMessageFunction>(
                &DispatchMessageHook))
            {
                const std::uintptr_t original = reinterpret_cast<
                    std::uintptr_t>(OriginalDispatchMessage);
                success = dfl::memory_patch::WriteBytes(
                    DispatchMessageIat,
                    &original,
                    sizeof(original)) && success;
            }
        }

        if (ScrollInitHookInstalled)
        {
            success = dfl::memory_patch::WriteBytes(
                ScrollInitFunction,
                OriginalScrollInitPrologue.data(),
                OriginalScrollInitPrologue.size()) && success;
        }

        if (ScrollInitTrampoline != nullptr)
        {
            VirtualFree(ScrollInitTrampoline, 0, MEM_RELEASE);
        }
        ScrollInitTrampoline = nullptr;
        OriginalScrollInit = nullptr;
        OriginalDispatchMessage = nullptr;
        ScrollInitHookInstalled = false;
        DispatchHookInstalled = false;
        WheelAccumulator = 0;
        for (ScrollEntry& entry : ScrollEntries)
        {
            entry.Object = nullptr;
        }
        return success;
    }
}
