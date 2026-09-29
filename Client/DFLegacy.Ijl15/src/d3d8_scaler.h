#pragma once

namespace dfl::d3d8_scaler
{
    // Install the optional D3D8 sampler policy used while the client scales
    // its fixed 640x480 surface into the configured outer window.
    bool Install() noexcept;
}
