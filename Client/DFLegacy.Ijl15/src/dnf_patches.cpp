#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cwchar>

#include "dnf_patches.h"
#include "d3d8_scaler.h"
#include "external_string_table.h"
#include "ijl15_config.h"
#include "message_audio.h"
#include "memory_patch.h"
#include "performance_overlay.h"
#include "mouse_wheel.h"
#include "window_scaling.h"

namespace
{
    constexpr std::uintptr_t DnfImageBase = 0x00400000;

    // 2008DF's client-page builder (0x00820280) pushes these two string
    // pointers before passing the URL through its local formatter. Keep the
    // original operands intact and redirect only the pointers at runtime.
    constexpr std::uintptr_t ClientDiamondUrlOperand = 0x00820335;
    constexpr std::uintptr_t ClientPayUrlOperand = 0x0082034D;
    constexpr std::uint32_t OriginalClientDiamondUrl = 0x00A81C34;
    constexpr std::uint32_t OriginalClientPayUrl = 0x00A81C0C;

    // CNRDStackable::getUseMode at 0x00457150 does not expose an action for
    // [cera booster] and returns mode 2 for generic [etc] items. Redirect
    // both supported cases to mode 1 so stock sub_45FDF0 emits command 47.
    constexpr std::uintptr_t StackableUseModeFunction = 0x00457150;
    constexpr std::size_t StackableItemIdOffset = 0x0C;
    constexpr std::size_t StackableTypeOffset = 0xAC;
    constexpr std::array<unsigned char, 9> OriginalStackableUseModePrologue{
        0x56,
        0x8B,
        0xF1,
        0x8B,
        0x0D,
        0xF0,
        0xD6,
        0xB5,
        0x00};

    // sub_80D4E0 inserts the first mail into an empty list, but the stock
    // client marks only the inbox view dirty. State-three mail is inserted
    // into the archive list through the same path, leaving a populated model
    // behind an empty archive tab until an unrelated operation marks it dirty.
    constexpr std::uintptr_t MailboxFirstInsertDirtyWrite = 0x0080D569;
    constexpr std::uintptr_t InboxDirtyFlag = 0x00BEDD04;
    constexpr std::uintptr_t ArchiveDirtyFlag = 0x00BEDD05;
    constexpr std::array<unsigned char, 7> OriginalMailboxDirtyWrite{
        0xC6,
        0x05,
        0x04,
        0xDD,
        0xBE,
        0x00,
        0x01};

    constexpr char LocalClientDiamondUrl[] =
        "http://127.0.0.1:8081/client/client_diamond.htm";
    constexpr char LocalClientPayUrl[] =
        "http://127.0.0.1:8081/client/client_pay.htm";

    static_assert(
        sizeof(std::uintptr_t) == sizeof(std::uint32_t),
        "DFLegacy.Ijl15 must be built for x86");

    bool __stdcall IsConfiguredEtcStackable(const void* item) noexcept
    {
        if (item == nullptr)
        {
            return false;
        }

        const auto* bytes = static_cast<const unsigned char*>(item);
        std::uint32_t itemId = 0;
        std::uint32_t stackableType = 0;
        std::memcpy(
            &itemId,
            bytes + StackableItemIdOffset,
            sizeof(itemId));
        std::memcpy(
            &stackableType,
            bytes + StackableTypeOffset,
            sizeof(stackableType));
        return dfl::dnf_patches::ShouldUseStackableMode1(
            itemId,
            stackableType);
    }

    __declspec(naked) void EtcStackableUseModeHook()
    {
        __asm
        {
            // Preserve the original this pointer while the helper runs.
            push ecx
            push ecx
            call IsConfiguredEtcStackable
            test al, al
            pop ecx
            jnz configured_etc_item

            // Recreate the nine displaced bytes, then resume at 0x00457159.
            push esi
            mov esi, ecx
            mov ecx, dword ptr ds:[0B5D6F0h]
            push 00457159h
            ret

        configured_etc_item:
            mov eax, 1
            ret
        }
    }

    bool BuildEtcStackableUseModeHookBytes(
        std::array<unsigned char, 9>& bytes) noexcept
    {
        constexpr std::size_t JumpSize = 5;
        const auto hookAddress = reinterpret_cast<std::uintptr_t>(
            &EtcStackableUseModeHook);
        const auto displacement = static_cast<std::uint32_t>(
            hookAddress - StackableUseModeFunction - JumpSize);

        bytes.fill(0x90);
        bytes[0] = 0xE9;
        std::memcpy(
            bytes.data() + 1,
            &displacement,
            sizeof(displacement));
        return true;
    }

    void __cdecl MarkMailboxListsDirty() noexcept
    {
        *reinterpret_cast<volatile unsigned char*>(InboxDirtyFlag) = 1;
        *reinterpret_cast<volatile unsigned char*>(ArchiveDirtyFlag) = 1;
    }

    bool BuildMailboxDirtyHookBytes(
        std::array<unsigned char, 7>& bytes) noexcept
    {
        constexpr std::size_t CallSize = 5;
        const auto hookAddress = reinterpret_cast<std::uintptr_t>(
            &MarkMailboxListsDirty);
        const auto displacement = static_cast<std::uint32_t>(
            hookAddress - MailboxFirstInsertDirtyWrite - CallSize);

        bytes.fill(0x90);
        bytes[0] = 0xE8;
        std::memcpy(
            bytes.data() + 1,
            &displacement,
            sizeof(displacement));
        return true;
    }

    struct CodePatch
    {
        bool Enabled;
        std::uintptr_t Address;
        std::array<unsigned char, 6> Expected;
        std::array<unsigned char, 6> Replacement;
    };

    constexpr std::array<CodePatch, 37> Patches = { {
        {
            dfl::dnf_patches::DisableDebuggerDetection,
            0x0040AEE0,
            {0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68},
            {0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3}
        },
        {
            dfl::dnf_patches::AllowMultipleClientInstances,
            // FindWindowA: always continue with creation of another game window.
            0x008F7AF6,
            {0x74, 0x12, 0x50, 0xFF, 0x15, 0x08},
            {0xEB, 0x12, 0x50, 0xFF, 0x15, 0x08}
        },
        {
            dfl::dnf_patches::AllowMultipleClientInstances,
            // CreateMutexA/GetLastError: treat an existing mutex as success.
            0x008A859C,
            {0x75, 0x23, 0x39, 0x7D, 0xEC, 0x72},
            {0xEB, 0x23, 0x39, 0x7D, 0xEC, 0x72}
        },
        {
            dfl::dnf_patches::DisableStartupMinimize,
            // Shell_TrayWnd FindWindowA: skip the minimize command.
            0x0040D5BE,
            {0x74, 0x32, 0x57, 0x68, 0x9F, 0x01},
            {0xEB, 0x32, 0x57, 0x68, 0x9F, 0x01}
        },
        {
            dfl::dnf_patches::DisableStartupMinimize,
            // Disable the static startup event that minimizes existing windows.
            0x00991820,
            {0x6A, 0x01, 0xB9, 0xF4, 0xD6, 0xB5},
            {0xC3, 0x90, 0xB9, 0xF4, 0xD6, 0xB5}
        },
        {
            true,
            0x00419969,
            {0x8B, 0x8D, 0xC0, 0xFE, 0xFF, 0xFF},
            // mov ecx, 1; nop: select the adult blood and piece particles.
            {0xB9, 0x01, 0x00, 0x00, 0x00, 0x90}
        },
        {
            true,
            0x007A3EC8,
            {0xC6, 0x40, 0x10, 0x00, 0xE8, 0xA9},
            // Keep the Creature tab visible while the Avatar inventory is selected.
            {0xC6, 0x40, 0x10, 0x01, 0xE8, 0xA9}
        },
        {
            true,
            0x007ABC30,
            {0xC6, 0x40, 0x10, 0x00, 0xE8, 0x41},
            // Keep the Creature tab visible while the main inventory is selected.
            {0xC6, 0x40, 0x10, 0x01, 0xE8, 0x41}
        },
        {
            dfl::dnf_patches::SinglePermanentAvatarPeriodOption,
            // Use the third period button as the popup's open/close state.
            0x00868BE0,
            {0x8B, 0x83, 0xCC, 0x07, 0x00, 0x00},
            {0x8B, 0x83, 0xD4, 0x07, 0x00, 0x00}
        },
        {
            dfl::dnf_patches::SinglePermanentAvatarPeriodOption,
            // Begin layout at period button three, which retains attribute 3.
            0x00868C27,
            {0x8D, 0xB3, 0xCC, 0x07, 0x00, 0x00},
            {0x8D, 0xB3, 0xD4, 0x07, 0x00, 0x00}
        },
        {
            dfl::dnf_patches::SinglePermanentAvatarPeriodOption,
            // Stop after positioning that button so it occupies the first row.
            0x00868C71,
            {0x83, 0xFF, 0x48, 0x7C, 0xB9, 0x5F},
            {0x83, 0xFF, 0x48, 0x90, 0x90, 0x5F}
        },
        {
            true,
            // Replace group-3 resource 88 with the pet-tab resource 18.
            0x00866577,
            {0xC7, 0x45, 0x9C, 0x58, 0x00, 0x00},
            {0xC7, 0x45, 0x9C, 0x12, 0x00, 0x00}
        },
        {
            true,
            // Replace group-3 resource 89 with the pet-tab resource 19.
            0x0086657E,
            {0xC7, 0x45, 0xA0, 0x59, 0x00, 0x00},
            {0xC7, 0x45, 0xA0, 0x13, 0x00, 0x00}
        },
        {
            true,
            // Replace group-3 resource 91 with the pet-tab resource 21.
            0x00866585,
            {0xC7, 0x45, 0xA4, 0x5B, 0x00, 0x00},
            {0xC7, 0x45, 0xA4, 0x15, 0x00, 0x00}
        },
        {
            true,
            // Replace group-3 resource 93 with the pet-tab resource 23.
            0x0086658C,
            {0xC7, 0x45, 0xA8, 0x5D, 0x00, 0x00},
            {0xC7, 0x45, 0xA8, 0x17, 0x00, 0x00}
        },
        {
            true,
            // Replace group-3 resource 90 with the pet-tab resource 20.
            0x00866593,
            {0xC7, 0x45, 0xAC, 0x5A, 0x00, 0x00},
            {0xC7, 0x45, 0xAC, 0x14, 0x00, 0x00}
        },
        {
            true,
            // Replace group-3 resource 92 with the pet-tab resource 22.
            0x0086659A,
            {0xC7, 0x45, 0xB0, 0x5C, 0x00, 0x00},
            {0xC7, 0x45, 0xB0, 0x16, 0x00, 0x00}
        },
        {
            true,
            // The stock constructor hides group 3 unconditionally; keep the pet tab visible.
            0x008666BA,
            {0xC6, 0x40, 0x10, 0x00, 0x8B, 0x5D},
            {0xC6, 0x40, 0x10, 0x01, 0x8B, 0x5D}
        },
        {
            true,
            // Chinese item-tab text is wider than the original Korean resource; move it left 10px.
            0x00A83F10,
            {0x89, 0x01, 0x00, 0x00, 0x08, 0x00},
            {0x7F, 0x01, 0x00, 0x00, 0x08, 0x00}
        },
        {
            true,
            // Always enter the 2008DF performance call site. The hook below
            // decides whether to run the native renderer and always draws
            // the IJL15 FPS/Committed overlay.
            0x0083F992,
            {0x75, 0x07, 0x8B, 0xCF, 0xE8, 0xF5},
            {0x90, 0x90, 0x8B, 0xCF, 0xE8, 0xF5}
        },
        {
            dfl::dnf_patches::ForcePlainTextLogs,
            // DNF.trc logger: skip the rotate/xor transform call.
            0x008CE742,
            {0xE8, 0xB9, 0x0F, 0x03, 0x00, 0x8B},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x8B}
        },
        {
            dfl::dnf_patches::ForcePlainTextLogs,
            // DNF.trc rotation header: keep the following stack cleanup byte.
            0x008CE801,
            {0xE8, 0xFA, 0x0E, 0x03, 0x00, 0x83},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x83}
        },
        {
            dfl::dnf_patches::ForcePlainTextLogs,
            // DNF.trc rotation header: keep the following stack cleanup byte.
            0x008CE859,
            {0xE8, 0xA2, 0x0E, 0x03, 0x00, 0x83},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x83}
        },
        {
            dfl::dnf_patches::ForcePlainTextLogs,
            // DNF.trc rotation header: keep the following stack cleanup byte.
            0x008CE8E1,
            {0xE8, 0x1A, 0x0E, 0x03, 0x00, 0x83},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x83}
        },
        {
            dfl::dnf_patches::ForcePlainTextLogs,
            // DNF.trc rotation header: keep the following stack cleanup byte.
            0x008CE938,
            {0xE8, 0xC3, 0x0D, 0x03, 0x00, 0x83},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x83}
        },
        {
            dfl::dnf_patches::ForcePlainTextLogs,
            // NeopleEngine.LOG writer: skip the rotate/xor transform call.
            0x008FF935,
            {0xE8, 0xC6, 0xFD, 0xFF, 0xFF, 0x57},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x57}
        },
        {
            dfl::dnf_patches::RemoveCustomPopupAnnouncementTitle,
            // NOTI 126/131 message type zero first formats the received body
            // through client string 640, which adds the announcement title.
            // Pass the original 0x100-byte receive buffer to popup 44 instead.
            0x0041CB21,
            {0x8D, 0x95, 0x1C, 0xF1, 0xFF, 0xFF},
            {0x8D, 0x95, 0x44, 0xF5, 0xFF, 0xFF}
        },
        {
            dfl::dnf_patches::ShouldBypassCeraPurchaseGate(
                dfl::dnf_patches::CeraPurchaseGate::CharacterLevel),
            // sub_85F100: retain the job check, but accept every level result.
            0x0085F182,
            {0x74, 0x09, 0xC6, 0x86, 0xAA, 0x34},
            {0x90, 0x90, 0xC6, 0x86, 0xAA, 0x34}
        },
        {
            dfl::dnf_patches::ShouldBypassCeraPurchaseGate(
                dfl::dnf_patches::CeraPurchaseGate::LegacySaleWindow),
            // Direct selection: skip commodity 27's expired sale window.
            0x008601E3,
            {0x75, 0x54, 0x8A, 0x4D, 0x0C, 0x3A},
            {0xEB, 0x54, 0x8A, 0x4D, 0x0C, 0x3A}
        },
        {
            dfl::dnf_patches::ShouldBypassCeraPurchaseGate(
                dfl::dnf_patches::CeraPurchaseGate::LegacySaleWindow),
            // Direct selection: send commodities 63-67/69-73 to normal setup.
            // Gift mode still follows the stock gift-eligibility path above.
            0x00860257,
            {0xE8, 0xF4, 0x0E, 0x0A, 0x00, 0x84},
            {0xE9, 0x72, 0x00, 0x00, 0x00, 0x84}
        },
        {
            dfl::dnf_patches::ShouldBypassCeraPurchaseGate(
                dfl::dnf_patches::CeraPurchaseGate::LegacySaleWindow),
            // Cart insertion: skip commodity 27's expired sale window.
            0x00863F74,
            {0x75, 0x4F, 0xE8, 0xD5, 0xD1, 0x09},
            {0xEB, 0x4F, 0xE8, 0xD5, 0xD1, 0x09}
        },
        {
            dfl::dnf_patches::ShouldBypassCeraPurchaseGate(
                dfl::dnf_patches::CeraPurchaseGate::PerCharacterLimit),
            // Cart insertion: skip the special package date and ownership gates.
            0x00863FE0,
            {0xE8, 0x6B, 0xD1, 0x09, 0x00, 0x84},
            {0xE9, 0x79, 0x00, 0x00, 0x00, 0x84}
        },
        {
            dfl::dnf_patches::ShouldBypassCeraPurchaseGate(
                dfl::dnf_patches::CeraPurchaseGate::LegacySaleWindow),
            // Alternate selection route: skip commodity 27's sale window.
            0x00865AA8,
            {0x75, 0x65, 0xE8, 0xA1, 0xB6, 0x09},
            {0xEB, 0x65, 0xE8, 0xA1, 0xB6, 0x09}
        },
        {
            dfl::dnf_patches::ShouldBypassCeraPurchaseGate(
                dfl::dnf_patches::CeraPurchaseGate::LegacySaleWindow),
            // Alternate selection route: accept the expired special packages.
            0x00865B26,
            {0xE8, 0x25, 0xB6, 0x09, 0x00, 0x84},
            {0xE9, 0x48, 0x00, 0x00, 0x00, 0x84}
        },
        {
            dfl::dnf_patches::ShouldBypassCeraPurchaseGate(
                dfl::dnf_patches::CeraPurchaseGate::PerCharacterLimit),
            // Purchase-button route: ignore prior ownership of the package set.
            0x008654ED,
            {0x74, 0xC7, 0x6A, 0x00, 0x6A, 0x00},
            {0xEB, 0xC7, 0x6A, 0x00, 0x6A, 0x00}
        },
        {
            dfl::dnf_patches::ShouldBypassCeraPurchaseGate(
                dfl::dnf_patches::CeraPurchaseGate::LegacySaleWindow),
            // Final confirmation: keep the normal prompt and skip commodity 27's
            // expired date and duplicate-purchase prompt selection.
            0x00869603,
            {0x33, 0xC9, 0x8B, 0xC7, 0x8B, 0x50},
            {0xE9, 0x9A, 0x00, 0x00, 0x00, 0x50}
        },
        {
            dfl::dnf_patches::ShouldBypassCeraPurchaseGate(
                dfl::dnf_patches::CeraPurchaseGate::PerCharacterLimit),
            // Final confirmation: skip the special package ownership scan.
            0x00869711,
            {0x33, 0xD2, 0x8B, 0xCF, 0x8B, 0x41},
            {0xE9, 0x25, 0x00, 0x00, 0x00, 0x41}
        }
    } };

    constexpr std::uintptr_t PerformanceOverlayGate = 0x0083F992;

    bool IsPatchEnabled(const CodePatch& patch) noexcept
    {
        if (patch.Address == PerformanceOverlayGate)
        {
            return dfl::ijl15_config::Get().EnablePerformanceOverlay;
        }
        return patch.Enabled;
    }

    enum class PatchState
    {
        Original,
        Applied,
        Mismatch
    };

    enum class PointerPatchState
    {
        Original,
        Applied,
        Mismatch
    };

    PatchState InspectEtcStackableUseModeHook() noexcept
    {
        const auto* target = reinterpret_cast<const unsigned char*>(
            StackableUseModeFunction);
        if (std::memcmp(
                target,
                OriginalStackableUseModePrologue.data(),
                OriginalStackableUseModePrologue.size()) == 0)
        {
            return PatchState::Original;
        }

        std::array<unsigned char, 9> replacement{};
        if (BuildEtcStackableUseModeHookBytes(replacement)
            && std::memcmp(
                target,
                replacement.data(),
                replacement.size()) == 0)
        {
            return PatchState::Applied;
        }
        return PatchState::Mismatch;
    }

    PatchState InspectMailboxDirtyHook() noexcept
    {
        const auto* target = reinterpret_cast<const unsigned char*>(
            MailboxFirstInsertDirtyWrite);
        if (std::memcmp(
                target,
                OriginalMailboxDirtyWrite.data(),
                OriginalMailboxDirtyWrite.size()) == 0)
        {
            return PatchState::Original;
        }

        std::array<unsigned char, 7> replacement{};
        if (BuildMailboxDirtyHookBytes(replacement)
            && std::memcmp(
                target,
                replacement.data(),
                replacement.size()) == 0)
        {
            return PatchState::Applied;
        }
        return PatchState::Mismatch;
    }

    bool WriteEtcStackableUseModeHook() noexcept
    {
        std::array<unsigned char, 9> replacement{};
        return BuildEtcStackableUseModeHookBytes(replacement)
            && dfl::memory_patch::WriteBytes(
                StackableUseModeFunction,
                replacement.data(),
                replacement.size());
    }

    bool RestoreEtcStackableUseModeFunction() noexcept
    {
        return dfl::memory_patch::WriteBytes(
            StackableUseModeFunction,
            OriginalStackableUseModePrologue.data(),
            OriginalStackableUseModePrologue.size());
    }

    bool WriteMailboxDirtyHook() noexcept
    {
        std::array<unsigned char, 7> replacement{};
        return BuildMailboxDirtyHookBytes(replacement)
            && dfl::memory_patch::WriteBytes(
                MailboxFirstInsertDirtyWrite,
                replacement.data(),
                replacement.size());
    }

    bool RestoreMailboxDirtyWrite() noexcept
    {
        return dfl::memory_patch::WriteBytes(
            MailboxFirstInsertDirtyWrite,
            OriginalMailboxDirtyWrite.data(),
            OriginalMailboxDirtyWrite.size());
    }

    PointerPatchState InspectClientPageUrl(
        std::uintptr_t operandAddress,
        std::uint32_t originalAddress,
        const char* replacement) noexcept
    {
        unsigned char opcode = 0;
        std::memcpy(
            &opcode,
            reinterpret_cast<const void*>(operandAddress - 1),
            sizeof(opcode));
        if (opcode != 0x68) // push imm32
        {
            return PointerPatchState::Mismatch;
        }

        std::uint32_t currentAddress = 0;
        std::memcpy(
            &currentAddress,
            reinterpret_cast<const void*>(operandAddress),
            sizeof(currentAddress));

        const auto replacementAddress = static_cast<std::uint32_t>(
            reinterpret_cast<std::uintptr_t>(replacement));
        if (currentAddress == replacementAddress)
        {
            return PointerPatchState::Applied;
        }
        if (currentAddress == originalAddress)
        {
            return PointerPatchState::Original;
        }
        return PointerPatchState::Mismatch;
    }

    bool WriteClientPageUrl(
        std::uintptr_t operandAddress,
        const char* replacement) noexcept
    {
        const auto replacementAddress = static_cast<std::uint32_t>(
            reinterpret_cast<std::uintptr_t>(replacement));
        return dfl::memory_patch::WriteBytes(
            operandAddress,
            &replacementAddress,
            sizeof(replacementAddress));
    }

    bool RestoreClientPageUrl(
        std::uintptr_t operandAddress,
        std::uint32_t originalAddress) noexcept
    {
        return dfl::memory_patch::WriteBytes(
            operandAddress,
            &originalAddress,
            sizeof(originalAddress));
    }

    bool IsDnfImage(HMODULE executable) noexcept
    {
        if (reinterpret_cast<std::uintptr_t>(executable) != DnfImageBase)
        {
            return false;
        }

        wchar_t path[MAX_PATH]{};
        const DWORD length = GetModuleFileNameW(executable, path, MAX_PATH);
        if (length == 0 || length >= MAX_PATH)
        {
            return false;
        }

        const wchar_t* fileName = std::wcsrchr(path, L'\\');
        fileName = fileName == nullptr ? path : fileName + 1;
        if (_wcsicmp(fileName, L"DNF.exe") != 0)
        {
            return false;
        }

        const auto* base = reinterpret_cast<const unsigned char*>(executable);
        const auto* dosHeader =
            reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE
            || dosHeader->e_lfanew < static_cast<LONG>(sizeof(IMAGE_DOS_HEADER))
            || dosHeader->e_lfanew > 0x1000)
        {
            return false;
        }

        const auto* ntHeaders = reinterpret_cast<const IMAGE_NT_HEADERS32*>(
            base + dosHeader->e_lfanew);
        if (ntHeaders->Signature != IMAGE_NT_SIGNATURE
            || ntHeaders->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC)
        {
            return false;
        }

        for (const CodePatch& patch : Patches)
        {
            if (!IsPatchEnabled(patch))
            {
                continue;
            }
            const std::uintptr_t endAddress =
                patch.Address + patch.Replacement.size();
            if (endAddress < patch.Address
                || endAddress - DnfImageBase
            > ntHeaders->OptionalHeader.SizeOfImage)
            {
                return false;
            }
        }

        const auto clientUrlOperandFitsImage =
            [imageSize = ntHeaders->OptionalHeader.SizeOfImage](
                std::uintptr_t address) noexcept
        {
            constexpr std::uintptr_t PointerSize = sizeof(std::uint32_t);
            const std::uintptr_t imageEnd = DnfImageBase + imageSize;
            return address >= DnfImageBase
                && address <= imageEnd
                && PointerSize <= imageEnd - address;
        };
        if (!clientUrlOperandFitsImage(ClientDiamondUrlOperand)
            || !clientUrlOperandFitsImage(ClientPayUrlOperand))
        {
            return false;
        }

        if (dfl::dnf_patches::EnableCustomStackableUseMode1)
        {
            const std::uintptr_t hookEnd = StackableUseModeFunction
                + OriginalStackableUseModePrologue.size();
            const std::uintptr_t imageEnd = DnfImageBase
                + ntHeaders->OptionalHeader.SizeOfImage;
            if (StackableUseModeFunction < DnfImageBase
                || hookEnd < StackableUseModeFunction
                || hookEnd > imageEnd)
            {
                return false;
            }
        }

        if (dfl::dnf_patches::FixMailboxArchiveRefresh)
        {
            const std::uintptr_t hookEnd = MailboxFirstInsertDirtyWrite
                + OriginalMailboxDirtyWrite.size();
            const std::uintptr_t imageEnd = DnfImageBase
                + ntHeaders->OptionalHeader.SizeOfImage;
            if (MailboxFirstInsertDirtyWrite < DnfImageBase
                || hookEnd < MailboxFirstInsertDirtyWrite
                || hookEnd > imageEnd)
            {
                return false;
            }
        }

        return true;
    }

    PatchState Inspect(const CodePatch& patch) noexcept
    {
        const auto* target =
            reinterpret_cast<const unsigned char*>(patch.Address);
        if (std::memcmp(
            target,
            patch.Replacement.data(),
            patch.Replacement.size()) == 0)
        {
            return PatchState::Applied;
        }
        if (std::memcmp(
            target,
            patch.Expected.data(),
            patch.Expected.size()) == 0)
        {
            return PatchState::Original;
        }
        return PatchState::Mismatch;
    }

}

namespace dfl::dnf_patches
{
    bool IsTargetProcess() noexcept
    {
        return IsDnfImage(GetModuleHandleW(nullptr));
    }

    bool Apply() noexcept
    {
        dfl::ijl15_config::Load();

        std::array<PatchState, Patches.size()> states{};
        for (std::size_t index = 0; index < Patches.size(); ++index)
        {
            if (!IsPatchEnabled(Patches[index]))
            {
                states[index] = PatchState::Applied;
                continue;
            }
            states[index] = Inspect(Patches[index]);
            if (states[index] == PatchState::Mismatch)
            {
                OutputDebugStringA(
                    "DFLegacy.Ijl15: DNF patch bytes do not match.\n");
                return false;
            }
        }

        const PatchState etcStackableUseModeState =
            dfl::dnf_patches::EnableCustomStackableUseMode1
                ? InspectEtcStackableUseModeHook()
                : PatchState::Applied;
        if (etcStackableUseModeState == PatchState::Mismatch)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: [etc] stackable use-mode bytes do not match.\n");
            return false;
        }

        const PatchState mailboxDirtyHookState =
            dfl::dnf_patches::FixMailboxArchiveRefresh
                ? InspectMailboxDirtyHook()
                : PatchState::Applied;
        if (mailboxDirtyHookState == PatchState::Mismatch)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: mailbox dirty-write bytes do not match 2008DF.\n");
            return false;
        }

        const external_string_table::HookState stringTableHookState =
            dfl::dnf_patches::UseExternalDnfStringTable
                ? external_string_table::InspectHook()
                : external_string_table::HookState::Installed;
        if (stringTableHookState
            == external_string_table::HookState::Mismatch)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: dnf.str extraction call does not match 2008DF.\n");
            return false;
        }

        const PointerPatchState diamondUrlState = InspectClientPageUrl(
            ClientDiamondUrlOperand,
            OriginalClientDiamondUrl,
            LocalClientDiamondUrl);
        const PointerPatchState payUrlState = InspectClientPageUrl(
            ClientPayUrlOperand,
            OriginalClientPayUrl,
            LocalClientPayUrl);
        if (diamondUrlState == PointerPatchState::Mismatch
            || payUrlState == PointerPatchState::Mismatch)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: client page URL bytes do not match.\n");
            return false;
        }

        bool diamondUrlChanged = false;
        bool payUrlChanged = false;
        if (diamondUrlState == PointerPatchState::Original)
        {
            if (!WriteClientPageUrl(
                    ClientDiamondUrlOperand,
                    LocalClientDiamondUrl))
            {
                RestoreClientPageUrl(
                    ClientDiamondUrlOperand,
                    OriginalClientDiamondUrl);
                OutputDebugStringA(
                    "DFLegacy.Ijl15: failed to redirect diamond URL.\n");
                return false;
            }
            diamondUrlChanged = true;
        }
        if (payUrlState == PointerPatchState::Original)
        {
            if (!WriteClientPageUrl(ClientPayUrlOperand, LocalClientPayUrl))
            {
                RestoreClientPageUrl(
                    ClientPayUrlOperand,
                    OriginalClientPayUrl);
                if (diamondUrlChanged)
                {
                    RestoreClientPageUrl(
                        ClientDiamondUrlOperand,
                        OriginalClientDiamondUrl);
                }
                OutputDebugStringA(
                    "DFLegacy.Ijl15: failed to redirect pay URL.\n");
                return false;
            }
            payUrlChanged = true;
        }

        std::size_t appliedCount = 0;
        for (std::size_t index = 0; index < Patches.size(); ++index)
        {
            if (!IsPatchEnabled(Patches[index])
                || states[index] == PatchState::Applied)
            {
                continue;
            }

            if (!memory_patch::WriteBytes(
                Patches[index].Address,
                Patches[index].Replacement.data(),
                Patches[index].Replacement.size()))
            {
                for (std::size_t rollback = 0; rollback < index; ++rollback)
                {
                    if (states[rollback] == PatchState::Original)
                    {
                        memory_patch::WriteBytes(
                            Patches[rollback].Address,
                            Patches[rollback].Expected.data(),
                            Patches[rollback].Expected.size());
                    }
                }
                if (payUrlChanged)
                {
                    RestoreClientPageUrl(
                        ClientPayUrlOperand,
                        OriginalClientPayUrl);
                }
                if (diamondUrlChanged)
                {
                    RestoreClientPageUrl(
                        ClientDiamondUrlOperand,
                        OriginalClientDiamondUrl);
                }
                OutputDebugStringA(
                    "DFLegacy.Ijl15: failed to apply DNF patches.\n");
                return false;
            }
            ++appliedCount;
        }

        bool mailboxDirtyHookChanged = false;
        if (dfl::dnf_patches::FixMailboxArchiveRefresh
            && mailboxDirtyHookState == PatchState::Original)
        {
            if (!WriteMailboxDirtyHook())
            {
                RestoreMailboxDirtyWrite();
                for (std::size_t rollback = 0;
                    rollback < Patches.size();
                    ++rollback)
                {
                    if (IsPatchEnabled(Patches[rollback])
                        && states[rollback] == PatchState::Original)
                    {
                        memory_patch::WriteBytes(
                            Patches[rollback].Address,
                            Patches[rollback].Expected.data(),
                            Patches[rollback].Expected.size());
                    }
                }
                if (payUrlChanged)
                {
                    RestoreClientPageUrl(
                        ClientPayUrlOperand,
                        OriginalClientPayUrl);
                }
                if (diamondUrlChanged)
                {
                    RestoreClientPageUrl(
                        ClientDiamondUrlOperand,
                        OriginalClientDiamondUrl);
                }
                OutputDebugStringA(
                    "DFLegacy.Ijl15: failed to install mailbox refresh hook.\n");
                return false;
            }
            mailboxDirtyHookChanged = true;
        }

        bool etcStackableUseModeChanged = false;
        if (dfl::dnf_patches::EnableCustomStackableUseMode1
            && etcStackableUseModeState == PatchState::Original)
        {
            if (!WriteEtcStackableUseModeHook())
            {
                RestoreEtcStackableUseModeFunction();
                for (std::size_t rollback = 0;
                    rollback < Patches.size();
                    ++rollback)
                {
                    if (IsPatchEnabled(Patches[rollback])
                        && states[rollback] == PatchState::Original)
                    {
                        memory_patch::WriteBytes(
                            Patches[rollback].Address,
                            Patches[rollback].Expected.data(),
                            Patches[rollback].Expected.size());
                    }
                }
                if (payUrlChanged)
                {
                    RestoreClientPageUrl(
                        ClientPayUrlOperand,
                        OriginalClientPayUrl);
                }
                if (diamondUrlChanged)
                {
                    RestoreClientPageUrl(
                        ClientDiamondUrlOperand,
                        OriginalClientDiamondUrl);
                }
                if (mailboxDirtyHookChanged)
                {
                    RestoreMailboxDirtyWrite();
                }
                OutputDebugStringA(
                    "DFLegacy.Ijl15: failed to install [etc] stackable use-mode hook.\n");
                return false;
            }
            etcStackableUseModeChanged = true;
        }

        bool stringTableHookChanged = false;
        if (dfl::dnf_patches::UseExternalDnfStringTable
            && stringTableHookState
                == external_string_table::HookState::Original)
        {
            if (!external_string_table::InstallHook())
            {
                for (std::size_t rollback = 0;
                    rollback < Patches.size();
                    ++rollback)
                {
                    if (IsPatchEnabled(Patches[rollback])
                        && states[rollback] == PatchState::Original)
                    {
                        memory_patch::WriteBytes(
                            Patches[rollback].Address,
                            Patches[rollback].Expected.data(),
                            Patches[rollback].Expected.size());
                    }
                }
                if (etcStackableUseModeChanged)
                {
                    RestoreEtcStackableUseModeFunction();
                }
                if (mailboxDirtyHookChanged)
                {
                    RestoreMailboxDirtyWrite();
                }
                if (payUrlChanged)
                {
                    RestoreClientPageUrl(
                        ClientPayUrlOperand,
                        OriginalClientPayUrl);
                }
                if (diamondUrlChanged)
                {
                    RestoreClientPageUrl(
                        ClientDiamondUrlOperand,
                        OriginalClientDiamondUrl);
                }
                OutputDebugStringA(
                    "DFLegacy.Ijl15: failed to install the external dnf.str hook.\n");
                return false;
            }
            stringTableHookChanged = true;
        }

        if (dfl::ijl15_config::Get().EnablePerformanceOverlay
            && !dfl::performance_overlay::InstallCallHook())
        {
            for (std::size_t rollback = 0; rollback < Patches.size(); ++rollback)
            {
                if (IsPatchEnabled(Patches[rollback])
                    && states[rollback] == PatchState::Original)
                {
                    memory_patch::WriteBytes(
                        Patches[rollback].Address,
                        Patches[rollback].Expected.data(),
                        Patches[rollback].Expected.size());
                }
            }
            if (etcStackableUseModeChanged)
            {
                RestoreEtcStackableUseModeFunction();
            }
            if (mailboxDirtyHookChanged)
            {
                RestoreMailboxDirtyWrite();
            }
            if (stringTableHookChanged)
            {
                external_string_table::RestoreOriginalHook();
            }
            if (payUrlChanged)
            {
                RestoreClientPageUrl(
                    ClientPayUrlOperand,
                    OriginalClientPayUrl);
            }
            if (diamondUrlChanged)
            {
                RestoreClientPageUrl(
                    ClientDiamondUrlOperand,
                    OriginalClientDiamondUrl);
            }
            OutputDebugStringA(
                "DFLegacy.Ijl15: failed to install FPS/memory renderer.\n");
            return false;
        }

        if (dfl::dnf_patches::DisableType16ChatMessageSound
            && !dfl::message_audio::InstallType16ChatSoundHook())
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: failed to install Type16 chat-sound hook.\n");
            return false;
        }

        // This is deliberately installed after the fatal code hooks. If a
        // different 2008DF build has incompatible window bytes, retain the
        // native 800x600 startup size instead of disabling the JPEG proxy.
        if (!dfl::window_scaling::Install())
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: window scaling was not installed; using native size.\n");
        }

        // 2008DF is a D3D8 client. The D3D9/XBRZ hook used by the newer
        // client cannot be reused here, so install the compatible sampler
        // policy before the client creates its D3D8 device.
        if (dfl::ijl15_config::Get().ScaleFilter
                != dfl::ijl15_config::ScaleFilterMode::Native
            && !dfl::d3d8_scaler::Install())
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: D3D8 scale filter was not installed; using native filtering.\n");
        }

        // Install this last so a failed path hook cannot leave a live DNF
        // jump pointing at an IJL15 module that DllMain subsequently rejects.
        // A path-hook failure is non-fatal: all original client paths remain
        // intact and the DLL can still provide the JPEG compatibility layer.
        if (dfl::output_paths::RedirectClientOutputPaths
            && !dfl::output_paths::InstallHook())
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: relative output path hook was not installed.\n");
        }

        // Install the wheel path only after all fatal patch checks above have
        // succeeded. A failure is non-fatal: the original mouse input and
        // all other IJL15 hooks remain available when this optional UI hook
        // does not match.
        if (dfl::ijl15_config::Get().EnableMouseWheel
            && !dfl::mouse_wheel::InstallHook())
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: mouse-wheel scroll hook was not installed.\n");
        }

        if (appliedCount != 0
            || diamondUrlChanged
            || payUrlChanged
            || etcStackableUseModeChanged
            || mailboxDirtyHookChanged
            || stringTableHookChanged)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: DNF process patches applied.\n");
        }
        return true;
    }
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH
        && dfl::dnf_patches::IsTargetProcess())
    {
        return dfl::dnf_patches::Apply() ? TRUE : FALSE;
    }
    return TRUE;
}
