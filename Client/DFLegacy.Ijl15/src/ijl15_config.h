#pragma once

namespace dfl::ijl15_config
{
    enum class ScaleFilterMode
    {
        Native,
        Linear,
        Point,
        Anisotropic,
    };

    struct Settings
    {
        int WindowWidth;
        int WindowHeight;
        bool EnablePerformanceOverlay;
        bool EnableMouseWheel;
        ScaleFilterMode ScaleFilter;
    };

    // Load once from Config.ini beside the client executable. Missing or
    // invalid values fall back to the defaults below.
    bool Load() noexcept;

    const Settings& Get() noexcept;
}
