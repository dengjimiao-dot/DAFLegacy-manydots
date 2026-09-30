#include "daflegacy_random.h"

#include <cassert>
#include <cstdint>

int main()
{
    const auto supported = daflegacy_rdseed_supported();
    assert(supported == 0 || supported == 1);

    std::uint64_t value = 0;
    if (supported == 0)
    {
        assert(daflegacy_rdseed64(8, &value) == 0);
        return 0;
    }

    bool generated = false;
    for (int attempt = 0; attempt < 100 && !generated; ++attempt)
    {
        generated = daflegacy_rdseed64(8, &value) != 0;
    }

    assert(generated);
    return 0;
}

