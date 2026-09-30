#pragma once

#include <array>
#include <cstddef>

namespace dfl::ijl15_config
{
    enum class ScaleFilterMode
    {
        Native,
        Linear,
        Point,
        Anisotropic,
    };

    // Purchase-gate switches for the Cera shop patches, applied together with
    // the master BypassCeraPurchaseRestrictions switch. The byte patches index
    // Settings::BypassCeraPurchaseGates by this enum.
    enum class CeraPurchaseGate
    {
        LegacySaleWindow = 0,
        PerCharacterLimit,
        CharacterLevel,
        ClientInventoryCapacity,
        JobCompatibility,
        CurrencyBalance,
        RequiredOption,
        GiftEligibility,
    };
    constexpr std::size_t CeraPurchaseGateCount = 8;

    struct Settings
    {
        int WindowWidth;
        int WindowHeight;
        bool EnablePerformanceOverlay;
        bool EnableMouseWheel;
        ScaleFilterMode ScaleFilter;

        // Feature switches for the DNF.exe byte patches and hooks. Defaults
        // (ijl15_config.cpp) keep the behaviour that was hard-coded before the
        // switches became configurable through Config.ini.
        bool DisableDebuggerDetection;
        bool AllowMultipleClientInstances;
        bool DisableStartupMinimize;
        bool SinglePermanentAvatarPeriodOption;
        bool EnableCustomStackableUseMode1;
        bool RemoveCustomPopupAnnouncementTitle;
        bool UseExternalDnfStringTable;
        bool FixMailboxArchiveRefresh;
        bool BypassCeraPurchaseRestrictions;
        std::array<bool, CeraPurchaseGateCount> BypassCeraPurchaseGates;
        bool ForcePlainTextLogs;
        bool DisableType16ChatMessageSound;
        bool RedirectClientOutputPaths;
    };

    // Load once from Config.ini beside the client executable. Missing or
    // invalid values fall back to the defaults below.
    bool Load() noexcept;

    const Settings& Get() noexcept;
}
