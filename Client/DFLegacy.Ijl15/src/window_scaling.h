#pragma once

namespace dfl::window_scaling
{
    // Change only the outer window dimensions. The 2008DF D3D initialization
    // calls at 0x0040B766/0x0040B76B remain fixed at 640x480.
    bool Install() noexcept;
}
