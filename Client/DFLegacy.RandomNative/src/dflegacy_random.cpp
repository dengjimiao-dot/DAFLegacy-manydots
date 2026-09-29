#include "dflegacy_random.h"

#include <algorithm>
#include <immintrin.h>
#include <intrin.h>

namespace
{
    bool detect_rdseed() noexcept
    {
        int registers[4]{};
        __cpuidex(registers, 0, 0);
        if (registers[0] < 7)
        {
            return false;
        }

        __cpuidex(registers, 7, 0);
        constexpr int rdseed_bit = 1 << 18;
        return (registers[1] & rdseed_bit) != 0;
    }
}

extern "C" int __cdecl dflegacy_rdseed_supported() noexcept
{
    static const bool supported = detect_rdseed();
    return supported ? 1 : 0;
}

extern "C" int __cdecl dflegacy_rdseed64(
    const std::uint32_t retry_count,
    std::uint64_t* const value) noexcept
{
    if (value == nullptr || dflegacy_rdseed_supported() == 0)
    {
        return 0;
    }

    const auto attempts = std::max<std::uint32_t>(1, retry_count);
    for (std::uint32_t attempt = 0; attempt < attempts; ++attempt)
    {
        unsigned __int64 candidate = 0;
        if (_rdseed64_step(&candidate) != 0)
        {
            *value = candidate;
            return 1;
        }

        _mm_pause();
    }

    return 0;
}

