#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <cstddef>
#include <cstring>

#include "ijl15_config.h"

namespace
{
    constexpr int BuiltinWidth = 640;
    constexpr int BuiltinHeight = 480;
    constexpr int DefaultWindowWidth = 640;
    constexpr int DefaultWindowHeight = 480;
    constexpr int MaximumWindowDimension = 8192;
    constexpr char ConfigFileName[] = "Config.ini";

    const dfl::ijl15_config::Settings DefaultSettings{
        DefaultWindowWidth,
        DefaultWindowHeight,
        true,
        true,
        dfl::ijl15_config::ScaleFilterMode::Native};

    dfl::ijl15_config::Settings CurrentSettings = DefaultSettings;
    bool ConfigLoaded = false;

    bool IsTrue(const char* value) noexcept
    {
        return _stricmp(value, "1") == 0
            || _stricmp(value, "true") == 0
            || _stricmp(value, "yes") == 0
            || _stricmp(value, "on") == 0;
    }

    bool IsFalse(const char* value) noexcept
    {
        return _stricmp(value, "0") == 0
            || _stricmp(value, "false") == 0
            || _stricmp(value, "no") == 0
            || _stricmp(value, "off") == 0;
    }

    dfl::ijl15_config::ScaleFilterMode ReadScaleFilter(
        const char* path,
        dfl::ijl15_config::ScaleFilterMode defaultValue) noexcept
    {
        char value[32]{};
        const DWORD length = GetPrivateProfileStringA(
            "Rendering",
            "ScaleFilter",
            "",
            value,
            static_cast<DWORD>(sizeof(value)),
            path);
        if (length == 0)
        {
            return defaultValue;
        }

        if (_stricmp(value, "native") == 0
            || _stricmp(value, "system") == 0
            || _stricmp(value, "auto") == 0
            || _stricmp(value, "off") == 0
            || std::strcmp(value, "0") == 0)
        {
            return dfl::ijl15_config::ScaleFilterMode::Native;
        }
        if (_stricmp(value, "linear") == 0
            || _stricmp(value, "bilinear") == 0
            || std::strcmp(value, "2") == 0)
        {
            return dfl::ijl15_config::ScaleFilterMode::Linear;
        }
        if (_stricmp(value, "point") == 0
            || _stricmp(value, "nearest") == 0
            || _stricmp(value, "sharp") == 0
            || std::strcmp(value, "1") == 0)
        {
            return dfl::ijl15_config::ScaleFilterMode::Point;
        }
        if (_stricmp(value, "anisotropic") == 0
            || _stricmp(value, "aniso") == 0
            || std::strcmp(value, "3") == 0)
        {
            return dfl::ijl15_config::ScaleFilterMode::Anisotropic;
        }
        return defaultValue;
    }

    bool ReadBoolean(
        const char* path,
        const char* section,
        const char* key,
        bool defaultValue) noexcept
    {
        char value[32]{};
        const DWORD length = GetPrivateProfileStringA(
            section,
            key,
            "",
            value,
            static_cast<DWORD>(sizeof(value)),
            path);
        if (length == 0)
        {
            return defaultValue;
        }
        if (IsTrue(value))
        {
            return true;
        }
        if (IsFalse(value))
        {
            return false;
        }
        return defaultValue;
    }

    bool BuildConfigPath(char* path, std::size_t capacity) noexcept
    {
        if (path == nullptr || capacity == 0)
        {
            return false;
        }

        char executablePath[MAX_PATH]{};
        const DWORD length = GetModuleFileNameA(
            nullptr,
            executablePath,
            static_cast<DWORD>(sizeof(executablePath)));
        if (length == 0 || length >= sizeof(executablePath))
        {
            return false;
        }

        char* separator = std::strrchr(executablePath, '\\');
        if (separator == nullptr)
        {
            separator = std::strrchr(executablePath, '/');
        }
        if (separator == nullptr)
        {
            return false;
        }

        *separator = '\0';
        const std::size_t directoryLength = std::strlen(executablePath);
        const std::size_t fileNameLength = std::strlen(ConfigFileName);
        if (directoryLength + 1 + fileNameLength + 1 > capacity)
        {
            return false;
        }

        std::memcpy(path, executablePath, directoryLength);
        path[directoryLength] = '\\';
        std::memcpy(
            path + directoryLength + 1,
            ConfigFileName,
            fileNameLength + 1);
        return true;
    }

    int ReadWindowDimension(
        const char* path,
        const char* key,
        int fallback) noexcept
    {
        const int value = static_cast<int>(GetPrivateProfileIntA(
            "Resolution",
            key,
            fallback,
            path));
        return std::clamp(value, 1, MaximumWindowDimension);
    }
}

namespace dfl::ijl15_config
{
    bool Load() noexcept
    {
        if (ConfigLoaded)
        {
            return true;
        }

        CurrentSettings = DefaultSettings;
        char path[MAX_PATH * 2]{};
        if (BuildConfigPath(path, sizeof(path)))
        {
            CurrentSettings.WindowWidth = ReadWindowDimension(
                path,
                "Width",
                DefaultWindowWidth);
            CurrentSettings.WindowHeight = ReadWindowDimension(
                path,
                "Height",
                DefaultWindowHeight);
            CurrentSettings.EnablePerformanceOverlay = ReadBoolean(
                path,
                "Features",
                "EnablePerformanceOverlay",
                DefaultSettings.EnablePerformanceOverlay);
            CurrentSettings.EnableMouseWheel = ReadBoolean(
                path,
                "Features",
                "EnableMouseWheel",
                DefaultSettings.EnableMouseWheel);
            CurrentSettings.ScaleFilter = ReadScaleFilter(
                path,
                DefaultSettings.ScaleFilter);
        }

        // This module only stretches the 640x480 client surface. Do not
        // allow the external window size to become smaller than that surface.
        CurrentSettings.WindowWidth = std::max(
            CurrentSettings.WindowWidth,
            BuiltinWidth);
        CurrentSettings.WindowHeight = std::max(
            CurrentSettings.WindowHeight,
            BuiltinHeight);
        CurrentSettings.WindowWidth = std::min(
            CurrentSettings.WindowWidth,
            MaximumWindowDimension);
        CurrentSettings.WindowHeight = std::min(
            CurrentSettings.WindowHeight,
            MaximumWindowDimension);

        ConfigLoaded = true;
        return true;
    }

    const Settings& Get() noexcept
    {
        Load();
        return CurrentSettings;
    }
}
