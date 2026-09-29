#pragma once

#include <cstdint>

#if defined(DFLEGACY_RANDOM_EXPORTS)
#define DFLEGACY_RANDOM_EXPORT __declspec(dllexport)
#elif defined(_WIN32)
#define DFLEGACY_RANDOM_EXPORT __declspec(dllimport)
#else
#define DFLEGACY_RANDOM_EXPORT
#endif

extern "C"
{
    DFLEGACY_RANDOM_EXPORT int __cdecl dflegacy_rdseed_supported() noexcept;

    DFLEGACY_RANDOM_EXPORT int __cdecl dflegacy_rdseed64(
        std::uint32_t retry_count,
        std::uint64_t* value) noexcept;
}
