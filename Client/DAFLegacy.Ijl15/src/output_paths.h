#pragma once

#include <cstddef>

namespace dfl::output_paths
{
    // Output locations are kept relative to the client's current working
    // directory. Whether the client's own path resolver is taken over at all
    // is a runtime switch: [Features] RedirectClientOutputPaths in Config.ini
    // (ijl15_config). No resolver hook is installed while it is disabled.
    bool InstallHook() noexcept;

    // Pure path policy used by the hook and by compatibility tests. The output
    // buffer always receives a relative path when this returns true.
    bool BuildRelativePath(
        const char* input,
        char* output,
        std::size_t outputCapacity) noexcept;
}
