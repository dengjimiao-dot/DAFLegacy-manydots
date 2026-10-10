#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <cstddef>
#include <cstring>

#include "font_resources.h"
#include "ijl15_config.h"

namespace
{
    constexpr std::size_t MaximumFontPath = 512;

    // Builds "<directory of the game executable>\<configured file>". A
    // configured absolute path is kept as-is, so a client copy can point at a
    // font outside the client directory without rebuilding the module.
    bool BuildFontPath(
        const char* configuredFile,
        char* path,
        std::size_t capacity) noexcept
    {
        if (configuredFile == nullptr || configuredFile[0] == '\0'
            || path == nullptr || capacity == 0)
        {
            return false;
        }

        const std::size_t fileLength = std::strlen(configuredFile);
        const bool isAbsolute = fileLength >= 2
            && configuredFile[1] == ':';
        if (isAbsolute)
        {
            if (fileLength + 1 > capacity)
            {
                return false;
            }

            std::memcpy(path, configuredFile, fileLength + 1);
            return true;
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

        separator[1] = '\0';
        const std::size_t directoryLength = std::strlen(executablePath);
        const char* relativeFile = configuredFile;
        while (*relativeFile == '\\' || *relativeFile == '/')
        {
            ++relativeFile;
        }

        const std::size_t relativeLength = std::strlen(relativeFile);
        if (directoryLength + relativeLength + 1 > capacity)
        {
            return false;
        }

        std::memcpy(path, executablePath, directoryLength);
        std::memcpy(
            path + directoryLength,
            relativeFile,
            relativeLength + 1);
        return true;
    }
}

namespace dfl::font_resources
{
    bool RegisterLocalFonts() noexcept
    {
        const auto& settings = dfl::ijl15_config::Get();
        if (!settings.RegisterLocalFonts)
        {
            return false;
        }

        char path[MaximumFontPath]{};
        if (!BuildFontPath(
                settings.LocalFontFile.data(),
                path,
                sizeof(path)))
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: local font path is invalid; keeping system fonts.\n");
            return false;
        }

        // FR_PRIVATE keeps the registration inside this process: the client
        // resolves the family name it asks for against the local file without
        // changing the machine-wide font table and without a WM_FONTCHANGE
        // broadcast while the loader lock is held.
        const int registered = AddFontResourceExA(path, FR_PRIVATE, nullptr);
        if (registered <= 0)
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: local font file was not registered; keeping system fonts.\n");
            return false;
        }

        return true;
    }
}
