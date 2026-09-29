#include "dflegacy_random.h"

#include <cassert>
#include <cstdint>

int main()
{
    const auto supported = dflegacy_rdseed_supported();
    assert(supported == 0 || supported == 1);

    std::uint64_t value = 0;
    if (supported == 0)
    {
        assert(dflegacy_rdseed64(8, &value) == 0);
        return 0;
    }

    bool generated = false;
    for (int attempt = 0; attempt < 100 && !generated; ++attempt)
    {
        generated = dflegacy_rdseed64(8, &value) != 0;
    }

    assert(generated);
    return 0;
}

