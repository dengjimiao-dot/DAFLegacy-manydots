#pragma once

#include <cstdint>

#if defined(DAFLEGACY_RANDOM_EXPORTS)
#define DAFLEGACY_RANDOM_EXPORT __declspec(dllexport)
#elif defined(_WIN32)
#define DAFLEGACY_RANDOM_EXPORT __declspec(dllimport)
#else
#define DAFLEGACY_RANDOM_EXPORT
#endif

extern "C"
{
    DAFLEGACY_RANDOM_EXPORT int __cdecl daflegacy_rdseed_supported() noexcept;

    DAFLEGACY_RANDOM_EXPORT int __cdecl daflegacy_rdseed64(
        std::uint32_t retry_count,
        std::uint64_t* value) noexcept;
}
