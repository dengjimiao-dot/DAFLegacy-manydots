#pragma once

namespace dfl::performance_overlay
{
    // Redirect the 2008DF performance-draw call to the dual native/IJL15 hook.
    bool InstallCallHook() noexcept;
}
