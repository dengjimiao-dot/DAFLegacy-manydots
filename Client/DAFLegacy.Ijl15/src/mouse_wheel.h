#pragma once

namespace dfl::mouse_wheel
{
    // Capture WM_MOUSEWHEEL before the 2008DF window procedure drops it and
    // drive the common A7D690 scrollbar model used by the CNScroll controls.
    bool InstallHook() noexcept;

    // Used by the patch coordinator when a later optional hook fails.
    bool RestoreHook() noexcept;
}
