#pragma once

namespace dfl::dungeon_killer_mode
{
    // Frame-pump hook behind Config.ini's EnableDungeonKillerMode switch:
    // while enabled, every frame re-runs the client's native "//kill" loop -
    // scene-type gated, walking the current map's object list and killing
    // every monster through the client's own kill dispatcher - so the room
    // monsters die on entry and the deaths follow the native reporting flow.
    // While disabled nothing is touched.
    bool InstallHook() noexcept;
}
