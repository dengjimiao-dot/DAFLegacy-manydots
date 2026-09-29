#pragma once

#include <cstddef>

namespace dfl::output_paths
{
    // Keep output locations relative to the client's current working directory.
    inline constexpr bool RedirectClientOutputPaths = true;

    // The 2008DF client uses this resolver for logs, crash artifacts, and the
    // screenshot directory. Non-output paths continue through the original
    // client implementation after the trampoline is installed.
    bool InstallHook() noexcept;

    // Pure path policy used by the hook and by compatibility tests. The output
    // buffer always receives a relative path when this returns true.
    bool BuildRelativePath(
        const char* input,
        char* output,
        std::size_t outputCapacity) noexcept;
}
