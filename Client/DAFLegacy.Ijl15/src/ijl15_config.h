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
        bool KeepInventoryTabOnListRefresh;
        bool SendInventorySortTab;
        // Storage-window "upgrade" button: with this switch on, the module
        // keeps the button's control rectangle anchored where the button is
        // drawn (next to the sort button) and detects the click itself, then
        // runs the client's own routines that open the Cera shop and select
        // the storage upgrade kit of the current stage. The full-storage
        // guard is kept, so a maxed storage still shows MAX and stays inert.
        bool UnblockStorageUpgradeButton;
        // Optional explicit button rectangle "x,y,w,h" in window client
        // coordinates, used when the layout cannot be derived from the sort
        // button (leave empty for the derived placement).
        std::array<char, 64> StorageUpgradeButtonRect;
        bool BypassCeraPurchaseRestrictions;
        std::array<bool, CeraPurchaseGateCount> BypassCeraPurchaseGates;
        bool ForcePlainTextLogs;
        bool DisableType16ChatMessageSound;
        bool RedirectClientOutputPaths;

        // Local font registration: with the switch enabled the module
        // registers LocalFontFile (relative to the game executable) as a
        // private font before the engine creates its own font objects, and
        // redirects the engine's hard-coded family name to LocalFontFamily so
        // GDI resolves that family to the local file. An empty family keeps
        // the engine's own name, which only works for a family that is not
        // also installed system-wide.
        bool RegisterLocalFonts;
        std::array<char, 260> LocalFontFile;
        std::array<char, 64> LocalFontFamily;
        // Which of the engine's two text fonts follow LocalFontFamily:
        // "small" (the general 12px UI font), "large" (the 18px font used by
        // the large-text window component built by sub_815B20) or "both".
        std::array<char, 16> LocalFontFamilyTarget;
    };

    // Load once from Config.ini beside the client executable. Missing or
    // invalid values fall back to the defaults below.
    bool Load() noexcept;

    const Settings& Get() noexcept;
}
