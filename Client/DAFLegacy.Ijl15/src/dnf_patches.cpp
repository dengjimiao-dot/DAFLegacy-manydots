#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>

#include "dnf_patches.h"
#include "d3d8_scaler.h"
#include "dungeon_killer_mode.h"
#include "external_string_table.h"
#include "font_resources.h"
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

    // The engine creates its two text fonts (sub_40B630, sizes 12 and 18)
    // with the hard-coded family name "SimSun" at 0x00998C6C. A font that is
    // registered for this process only does not win over an installed family
    // with the same name, so the family operand is redirected to the family of
    // the locally registered file instead (see PENDING P-42).
    // 0x40BA71 belongs to the 12px creation (the general UI font, referenced
    // by more than two hundred call sites); 0x40BAB7 belongs to the 18px
    // creation, which only the large-text window component built by
    // sub_815B20 consumes.
    constexpr std::uintptr_t TextFontSmallNameOperand = 0x0040BA71;
    constexpr std::uintptr_t TextFontLargeNameOperand = 0x0040BAB7;
    constexpr std::uint32_t OriginalTextFontName = 0x00998C6C;

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

    // NOTI 13 (ENUM_NOTIPACKET_ITEM_LIST) rebuilds the bag by inserting every
    // record with the fourth argument of sub_45E280 set to one. Inside
    // sub_45E280 that flag makes the main container (space 0) rewrite the bag
    // window's page-tab strip (window+0x80, current tab at +0x3C) to the tab
    // that owns the inserted item's slot. A full-list refresh therefore leaves
    // the tab on the slot range of the last inserted item instead of keeping
    // the tab the player selected. Clear the flag so the rebuild stays silent;
    // the incremental path (NOTI 14) keeps its own flag and its auto-switch.
    constexpr std::uintptr_t MainListInsertFlag = 0x0041D1FB;
    constexpr std::array<unsigned char, 6> OriginalMainListInsertFlag{
        0x6A,
        0x01,
        0x8B,
        0x8D,
        0x98,
        0xEF};

    // sub_45FF70 appends the one-byte item space to the CMD 20 body. Extend
    // that append to two bytes and add the current page tab as the second
    // byte, so the server can sort only the selected tab: the bag window's tab
    // strip for space 0, the pet-grid view for space 7. The warehouse (space 2)
    // has no page tabs, and a view that cannot be resolved writes 0xFF, which
    // the server reads as "whole item space". The legacy three-byte body keeps
    // working for unpatched clients.
    constexpr std::uintptr_t BagWindowPointer = 0x00B5DCE8;
    constexpr std::size_t BagWindowTabStripOffset = 0x80;
    constexpr std::size_t TabStripCurrentTabOffset = 0x3C;
    constexpr std::uint8_t SortAllSections = 0xFF;
    constexpr std::uintptr_t InventorySortPayloadAppend = 0x0046007C;
    constexpr std::uintptr_t InventorySortPayloadResume = 0x00460084;
    constexpr std::array<unsigned char, 8> OriginalInventorySortAppend{
        0x6A,
        0x01,
        0x8D,
        0x45,
        0xFC,
        0x50,
        0x8B,
        0xCE};

    // The bag window constructor (sub_79B740) clears the pet-grid view object
    // once while the item windows are built at game init, so its address stays
    // valid for the whole session. That view keeps its current page tab at
    // +0x90 (0..2: creature / artifact / creature stackable), selected by
    // sub_7A6A50 and mapped to the pet section starts 0/140/189 through
    // sub_7A8D30 - the same boundaries the server uses for space 7.
    constexpr std::uintptr_t PetGridViewInit = 0x007A6110;
    constexpr std::uintptr_t PetGridViewResume = 0x007A6115;
    constexpr std::size_t PetGridTabOffset = 0x90;
    constexpr std::uint32_t PetGridTabCount = 3;
    constexpr std::array<unsigned char, 5> OriginalPetGridViewInit{
        0x33,
        0xC0,
        0x89,
        0x41,
        0x10};

    // Storage window (CNRDStorageWindow) frame function sub_823C80 prologue,
    // displaced by the upgrade-button hook.
    constexpr std::array<unsigned char, 6> OriginalStorageUpgradeTickPrologue{
        0x55,
        0x8B,
        0xEC,
        0x83,
        0xEC,
        0x38};

    constexpr char LocalClientDiamondUrl[] =
        "http://127.0.0.1:8081/client/client_diamond.htm";
    constexpr char LocalClientPayUrl[] =
        "http://127.0.0.1:8081/client/client_pay.htm";

    static_assert(
        sizeof(std::uintptr_t) == sizeof(std::uint32_t),
        "DAFLegacy.Ijl15 must be built for x86");

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

    const void* PetGridComponent = nullptr;

    extern "C" void __cdecl CapturePetGridComponent(const void* component) noexcept
    {
        PetGridComponent = component;
    }

    extern "C" unsigned char __cdecl CurrentPetInventorySection() noexcept
    {
        const auto* grid = static_cast<const unsigned char*>(PetGridComponent);
        if (grid == nullptr)
        {
            return SortAllSections;
        }

        std::uint32_t tab = 0;
        std::memcpy(&tab, grid + PetGridTabOffset, sizeof(tab));
        return tab < PetGridTabCount
            ? static_cast<unsigned char>(tab)
            : SortAllSections;
    }

    __declspec(naked) void PetGridViewHook()
    {
        __asm
        {
            // Remember the pet-grid view while the bag window builds it once.
            push eax
            push ecx
            call CapturePetGridComponent
            pop ecx
            pop eax

            // Recreate the displaced clear stores, then resume right after.
            xor eax, eax
            mov [ecx + 10h], eax
            push 007A6115h
            ret
        }
    }

    bool BuildPetGridViewHookBytes(
        std::array<unsigned char, 5>& bytes) noexcept
    {
        constexpr std::size_t JumpSize = 5;
        const auto hookAddress = reinterpret_cast<std::uintptr_t>(
            &PetGridViewHook);
        const auto displacement = static_cast<std::uint32_t>(
            hookAddress - PetGridViewInit - JumpSize);

        bytes[0] = 0xE9;
        std::memcpy(
            bytes.data() + 1,
            &displacement,
            sizeof(displacement));
        return true;
    }

    __declspec(naked) void InventorySortTabHook()
    {
        __asm
        {
            // The space byte already sits in the first payload slot of the
            // stack local; default the second slot to "whole item space".
            push eax
            push edx
            mov byte ptr[ebp - 3], 0FFh
            cmp byte ptr[ebp - 4], 0
            jnz check_pet_space

            // Main bag: the bag window's tab strip keeps the page tab.
            mov edx, dword ptr ds:[0B5DCE8h]
            test edx, edx
            jz sort_tab_ready
            mov edx, dword ptr[edx + 80h]
            test edx, edx
            jz sort_tab_ready
            mov eax, dword ptr[edx + 3Ch]
            cmp eax, 3
            ja sort_tab_ready
            mov byte ptr[ebp - 3], al
            jmp sort_tab_ready

            // Pet inventory: the pet-grid view keeps the page tab.
        check_pet_space:
            cmp byte ptr[ebp - 4], 7
            jnz sort_tab_ready
            call CurrentPetInventorySection
            mov byte ptr[ebp - 3], al

        sort_tab_ready:
            pop edx
            pop eax
            // Recreate the displaced append sequence with a two-byte payload,
            // then resume at the sub_8B2450 call.
            push 2
            lea eax, [ebp - 4]
            push eax
            mov ecx, esi
            push 00460084h
            ret
        }
    }

    bool BuildInventorySortTabHookBytes(
        std::array<unsigned char, 8>& bytes) noexcept
    {
        constexpr std::size_t JumpSize = 5;
        const auto hookAddress = reinterpret_cast<std::uintptr_t>(
            &InventorySortTabHook);
        const auto displacement = static_cast<std::uint32_t>(
            hookAddress - InventorySortPayloadAppend - JumpSize);

        bytes.fill(0x90);
        bytes[0] = 0xE9;
        std::memcpy(
            bytes.data() + 1,
            &displacement,
            sizeof(displacement));
        return true;
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

    // ---- Storage-window "upgrade" button (see PENDING P-49) ----
    //
    // The client wires the button (element 2 of the window's control array,
    // CNRDStorageWindow tick sub_823C80) to the Cera shop: the branch at
    // 0x823D39 opens the shop window (sub_864B60) and selects the storage
    // upgrade kit of the current stage (sub_8659E0). In this build the
    // control's hit rectangle does not sit where the button is drawn, so a
    // click never reaches it. The hook below runs one tiny step per window
    // frame: it keeps the control's rectangle anchored next to the sort
    // button (the layout offsets come from the client's own construction
    // data), and it detects the click itself instead of relying on the
    // client's control dispatch. Both actions use the client's own routines,
    // so the button is drawn with its own art and behaves like the stock
    // button, still without sending any packet.

    constexpr std::uintptr_t StorageUpgradeTickFunction = 0x00823C80;
    constexpr std::uintptr_t StorageUpgradeTickResume = 0x00823C86;
    constexpr std::uintptr_t ControlMoveFunction = 0x008F69A0;
    constexpr std::uintptr_t CashShopSetStateFunction = 0x00864B60;
    constexpr std::uintptr_t CashShopSelectUpgradeKitFunction = 0x008659E0;
    constexpr std::uintptr_t MousePositionXFunction = 0x008FC710;
    constexpr std::uintptr_t MousePositionYFunction = 0x008FC720;
    constexpr std::uintptr_t IsInGameFunction = 0x00901150;
    constexpr std::uintptr_t DecryptFieldFunction = 0x00402030;
    constexpr std::uintptr_t StorageUpgradeStageField = 0x00BEE728;

    constexpr std::size_t WindowOwnerOffset = 0x10;
    constexpr std::size_t WindowButtonArrayOffset = 0x38;
    constexpr std::size_t ButtonStride = 0x88;
    constexpr std::size_t ButtonIdOffset = 0x0C;
    constexpr std::size_t ButtonRectXOffset = 0x14;
    constexpr std::size_t ButtonRectYOffset = 0x18;
    constexpr std::size_t ButtonRectWidthOffset = 0x1C;
    constexpr std::size_t ButtonRectHeightOffset = 0x20;
    constexpr std::size_t ShopStateOffset = 0x10;
    constexpr std::size_t CashShopFrameOffset = 0xF24;
    constexpr int StorageUpgradeStageMax = 7;
    // sub_825780 builds the sort button at x=223 and the upgrade button at
    // x=18 on the same row; keep that offset when the layout moves them.
    constexpr int UpgradeButtonOffsetFromSortX = 18 - 223;

    using ControlMove = void(__thiscall*)(void*, int, int);
    using ShopSetState = void(__thiscall*)(void*, int);
    using ShopCommand = void(__thiscall*)(void*);
    using QueryMouse = int(__cdecl*)();
    using IsInGame = unsigned char(__cdecl*)();
    using DecryptField = int(__thiscall*)(const void*);

    bool UpgradeButtonMouseWasDown = false;
    bool UpgradeButtonProbeShown = false;
    int UpgradeButtonRect[4] = { 0, 0, 0, 0 };
    bool UpgradeButtonRectConfigured = false;
    bool UpgradeButtonRectParsed = false;
    const void* UpgradeButtonWeightsOwner = nullptr;

    int CurrentStorageUpgradeStage() noexcept
    {
        if (reinterpret_cast<IsInGame>(IsInGameFunction)() != 0)
        {
            const auto* field = *reinterpret_cast<const void* const*>(
                StorageUpgradeStageField);
            return field != nullptr
                ? reinterpret_cast<DecryptField>(DecryptFieldFunction)(field)
                : StorageUpgradeStageMax;
        }
        return *reinterpret_cast<const int*>(StorageUpgradeStageField);
    }

    void ParseUpgradeButtonRect() noexcept
    {
        UpgradeButtonRectParsed = true;
        UpgradeButtonRectConfigured = false;
        const auto& settings = dfl::ijl15_config::Get();
        const char* text = settings.StorageUpgradeButtonRect.data();
        if (text[0] == '\0')
        {
            return;
        }

        int values[4]{};
        int index = 0;
        const char* cursor = text;
        while (index < 4 && *cursor != '\0')
        {
            while (*cursor == ' ' || *cursor == ',')
            {
                ++cursor;
            }
            if (*cursor < '0' || *cursor > '9')
            {
                return;
            }
            int value = 0;
            while (*cursor >= '0' && *cursor <= '9')
            {
                value = value * 10 + (*cursor - '0');
                ++cursor;
            }
            values[index++] = value;
        }
        if (index != 4)
        {
            return;
        }
        for (int slot = 0; slot < 4; ++slot)
        {
            UpgradeButtonRect[slot] = values[slot];
        }
        UpgradeButtonRectConfigured = true;
    }

    extern "C" void __cdecl MaintainStorageUpgradeButton(void* window) noexcept
    {
        if (window == nullptr)
        {
            return;
        }
        auto* win = static_cast<unsigned char*>(window);
        auto* owner = *reinterpret_cast<unsigned char**>(win + WindowOwnerOffset);
        auto* buttons = *reinterpret_cast<unsigned char**>(win + WindowButtonArrayOffset);
        if (owner == nullptr || buttons == nullptr)
        {
            return;
        }
        auto* shop = *reinterpret_cast<unsigned char**>(owner + CashShopFrameOffset);
        if (shop == nullptr)
        {
            return;
        }

        if (!UpgradeButtonRectParsed)
        {
            ParseUpgradeButtonRect();
        }

        auto* sortButton = buttons + ButtonStride;
        auto* upgradeButton = buttons + ButtonStride * 2;
        int x = 0;
        int y = 0;
        int width = 0;
        int height = 0;
        if (UpgradeButtonRectConfigured)
        {
            x = UpgradeButtonRect[0];
            y = UpgradeButtonRect[1];
            width = UpgradeButtonRect[2];
            height = UpgradeButtonRect[3];
        }
        else
        {
            x = *reinterpret_cast<int*>(sortButton + ButtonRectXOffset)
                + UpgradeButtonOffsetFromSortX;
            y = *reinterpret_cast<int*>(sortButton + ButtonRectYOffset);
            width = *reinterpret_cast<int*>(upgradeButton + ButtonRectWidthOffset);
            height = *reinterpret_cast<int*>(upgradeButton + ButtonRectHeightOffset);
        }
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (*reinterpret_cast<int*>(upgradeButton + ButtonRectXOffset) != x
            || *reinterpret_cast<int*>(upgradeButton + ButtonRectYOffset) != y)
        {
            reinterpret_cast<ControlMove>(ControlMoveFunction)(
                upgradeButton,
                x,
                y);
        }
        *reinterpret_cast<int*>(upgradeButton + ButtonRectWidthOffset) = width;
        *reinterpret_cast<int*>(upgradeButton + ButtonRectHeightOffset) = height;

        const bool mouseDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        const bool clicked = mouseDown && !UpgradeButtonMouseWasDown;
        UpgradeButtonMouseWasDown = mouseDown;
        const int mouseX = reinterpret_cast<QueryMouse>(MousePositionXFunction)();
        const int mouseY = reinterpret_cast<QueryMouse>(MousePositionYFunction)();
        const bool inside =
            mouseX >= x && mouseX < x + width
            && mouseY >= y && mouseY < y + height;


        if (!clicked || !inside)
        {
            return;
        }
        if (*reinterpret_cast<const int*>(shop + ShopStateOffset) != 0)
        {
            return;
        }
        if (CurrentStorageUpgradeStage() >= StorageUpgradeStageMax)
        {
            return;
        }

        reinterpret_cast<ShopSetState>(CashShopSetStateFunction)(shop, 1);
        reinterpret_cast<ShopCommand>(CashShopSelectUpgradeKitFunction)(shop);
    }

    __declspec(naked) void StorageUpgradeButtonHook()
    {
        __asm
        {
            // Preserve the registers the maintainer may clobber.
            push eax
            push ecx
            push edx
            push ecx
            call MaintainStorageUpgradeButton
            add esp, 4
            pop edx
            pop ecx
            pop eax

            // Recreate the displaced prologue, then resume at 0x00823C86.
            push ebp
            mov ebp, esp
            sub esp, 38h
            push 00823C86h
            ret
        }
    }

    bool BuildStorageUpgradeButtonHookBytes(
        std::array<unsigned char, 6>& bytes) noexcept
    {
        constexpr std::size_t JumpSize = 5;
        const auto hookAddress = reinterpret_cast<std::uintptr_t>(
            &StorageUpgradeButtonHook);
        const auto displacement = static_cast<std::uint32_t>(
            hookAddress - StorageUpgradeTickFunction - JumpSize);

        bytes.fill(0x90);
        bytes[0] = 0xE9;
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

    std::array<CodePatch, 38> BuildPatches() noexcept
    {
        const auto& settings = dfl::ijl15_config::Get();
        return { {
        {
            settings.DisableDebuggerDetection,
            0x0040AEE0,
            {0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68},
            {0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3}
        },
        {
            settings.AllowMultipleClientInstances,
            // FindWindowA: always continue with creation of another game window.
            0x008F7AF6,
            {0x74, 0x12, 0x50, 0xFF, 0x15, 0x08},
            {0xEB, 0x12, 0x50, 0xFF, 0x15, 0x08}
        },
        {
            settings.AllowMultipleClientInstances,
            // CreateMutexA/GetLastError: treat an existing mutex as success.
            0x008A859C,
            {0x75, 0x23, 0x39, 0x7D, 0xEC, 0x72},
            {0xEB, 0x23, 0x39, 0x7D, 0xEC, 0x72}
        },
        {
            settings.DisableStartupMinimize,
            // Shell_TrayWnd FindWindowA: skip the minimize command.
            0x0040D5BE,
            {0x74, 0x32, 0x57, 0x68, 0x9F, 0x01},
            {0xEB, 0x32, 0x57, 0x68, 0x9F, 0x01}
        },
        {
            settings.DisableStartupMinimize,
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
            settings.SinglePermanentAvatarPeriodOption,
            // Use the third period button as the popup's open/close state.
            0x00868BE0,
            {0x8B, 0x83, 0xCC, 0x07, 0x00, 0x00},
            {0x8B, 0x83, 0xD4, 0x07, 0x00, 0x00}
        },
        {
            settings.SinglePermanentAvatarPeriodOption,
            // Begin layout at period button three, which retains attribute 3.
            0x00868C27,
            {0x8D, 0xB3, 0xCC, 0x07, 0x00, 0x00},
            {0x8D, 0xB3, 0xD4, 0x07, 0x00, 0x00}
        },
        {
            settings.SinglePermanentAvatarPeriodOption,
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
            settings.KeepInventoryTabOnListRefresh,
            // Full-list rebuild: pass a zero insert flag so the rebuilt list
            // keeps the bag page tab on the range the player selected.
            MainListInsertFlag,
            OriginalMainListInsertFlag,
            {0x6A, 0x00, 0x8B, 0x8D, 0x98, 0xEF}
        },
        {
            true,
            // Chinese item-tab text is wider than the original Korean resource; move it left 10px.
            0x00A83F10,
            {0x89, 0x01, 0x00, 0x00, 0x08, 0x00},
            {0x7F, 0x01, 0x00, 0x00, 0x08, 0x00}
        },
        {
            settings.EnablePerformanceOverlay,
            // Always enter the 2008DF performance call site. The hook below
            // decides whether to run the native renderer and always draws
            // the IJL15 FPS/Committed overlay.
            0x0083F992,
            {0x75, 0x07, 0x8B, 0xCF, 0xE8, 0xF5},
            {0x90, 0x90, 0x8B, 0xCF, 0xE8, 0xF5}
        },
        {
            settings.ForcePlainTextLogs,
            // DNF.trc logger: skip the rotate/xor transform call.
            0x008CE742,
            {0xE8, 0xB9, 0x0F, 0x03, 0x00, 0x8B},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x8B}
        },
        {
            settings.ForcePlainTextLogs,
            // DNF.trc rotation header: keep the following stack cleanup byte.
            0x008CE801,
            {0xE8, 0xFA, 0x0E, 0x03, 0x00, 0x83},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x83}
        },
        {
            settings.ForcePlainTextLogs,
            // DNF.trc rotation header: keep the following stack cleanup byte.
            0x008CE859,
            {0xE8, 0xA2, 0x0E, 0x03, 0x00, 0x83},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x83}
        },
        {
            settings.ForcePlainTextLogs,
            // DNF.trc rotation header: keep the following stack cleanup byte.
            0x008CE8E1,
            {0xE8, 0x1A, 0x0E, 0x03, 0x00, 0x83},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x83}
        },
        {
            settings.ForcePlainTextLogs,
            // DNF.trc rotation header: keep the following stack cleanup byte.
            0x008CE938,
            {0xE8, 0xC3, 0x0D, 0x03, 0x00, 0x83},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x83}
        },
        {
            settings.ForcePlainTextLogs,
            // NeopleEngine.LOG writer: skip the rotate/xor transform call.
            0x008FF935,
            {0xE8, 0xC6, 0xFD, 0xFF, 0xFF, 0x57},
            {0x90, 0x90, 0x90, 0x90, 0x90, 0x57}
        },
        {
            settings.RemoveCustomPopupAnnouncementTitle,
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
    }

    bool IsPatchEnabled(const CodePatch& patch) noexcept
    {
        return patch.Enabled;
    }

    bool ValidatePatchRanges(
        const std::array<CodePatch, 38>& patches) noexcept
    {
        const auto& settings = dfl::ijl15_config::Get();
        const auto* base = reinterpret_cast<const unsigned char*>(DnfImageBase);
        const auto* dosHeader = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE)
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

        const std::uintptr_t imageEnd = DnfImageBase
            + ntHeaders->OptionalHeader.SizeOfImage;
        const auto rangeFitsImage =
            [imageEnd](std::uintptr_t address, std::size_t size) noexcept
        {
            return address >= DnfImageBase
                && address <= imageEnd
                && size <= imageEnd - address;
        };
        const auto pointerFitsImage =
            [imageEnd](std::uintptr_t address) noexcept
        {
            constexpr std::uintptr_t PointerSize = sizeof(std::uint32_t);
            return address >= DnfImageBase
                && address <= imageEnd
                && PointerSize <= imageEnd - address;
        };

        for (const CodePatch& patch : patches)
        {
            if (!IsPatchEnabled(patch))
            {
                continue;
            }
            if (!rangeFitsImage(patch.Address, patch.Replacement.size()))
            {
                return false;
            }
        }

        if (!pointerFitsImage(ClientDiamondUrlOperand)
            || !pointerFitsImage(ClientPayUrlOperand))
        {
            return false;
        }

        if (!pointerFitsImage(TextFontSmallNameOperand)
            || !pointerFitsImage(TextFontLargeNameOperand))
        {
            return false;
        }

        if (settings.EnableCustomStackableUseMode1
            && !rangeFitsImage(
                StackableUseModeFunction,
                OriginalStackableUseModePrologue.size()))
        {
            return false;
        }

        if (settings.FixMailboxArchiveRefresh
            && !rangeFitsImage(
                MailboxFirstInsertDirtyWrite,
                OriginalMailboxDirtyWrite.size()))
        {
            return false;
        }

        if (settings.SendInventorySortTab
            && !rangeFitsImage(
                InventorySortPayloadAppend,
                OriginalInventorySortAppend.size()))
        {
            return false;
        }

        if (settings.SendInventorySortTab
            && !rangeFitsImage(
                PetGridViewInit,
                OriginalPetGridViewInit.size()))
        {
            return false;
        }

        return true;
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

    PatchState InspectInventorySortTabHook() noexcept
    {
        const auto* target = reinterpret_cast<const unsigned char*>(
            InventorySortPayloadAppend);
        if (std::memcmp(
                target,
                OriginalInventorySortAppend.data(),
                OriginalInventorySortAppend.size()) == 0)
        {
            return PatchState::Original;
        }

        std::array<unsigned char, 8> replacement{};
        if (BuildInventorySortTabHookBytes(replacement)
            && std::memcmp(
                target,
                replacement.data(),
                replacement.size()) == 0)
        {
            return PatchState::Applied;
        }
        return PatchState::Mismatch;
    }

    bool WriteInventorySortTabHook() noexcept
    {
        std::array<unsigned char, 8> replacement{};
        return BuildInventorySortTabHookBytes(replacement)
            && dfl::memory_patch::WriteBytes(
                InventorySortPayloadAppend,
                replacement.data(),
                replacement.size());
    }

    bool RestoreInventorySortAppend() noexcept
    {
        return dfl::memory_patch::WriteBytes(
            InventorySortPayloadAppend,
            OriginalInventorySortAppend.data(),
            OriginalInventorySortAppend.size());
    }

    PatchState InspectPetGridViewHook() noexcept
    {
        const auto* target = reinterpret_cast<const unsigned char*>(
            PetGridViewInit);
        if (std::memcmp(
                target,
                OriginalPetGridViewInit.data(),
                OriginalPetGridViewInit.size()) == 0)
        {
            return PatchState::Original;
        }

        std::array<unsigned char, 5> replacement{};
        if (BuildPetGridViewHookBytes(replacement)
            && std::memcmp(
                target,
                replacement.data(),
                replacement.size()) == 0)
        {
            return PatchState::Applied;
        }
        return PatchState::Mismatch;
    }

    PatchState InspectStorageUpgradeButtonHook() noexcept
    {
        const auto* target = reinterpret_cast<const unsigned char*>(
            StorageUpgradeTickFunction);
        if (std::memcmp(
                target,
                OriginalStorageUpgradeTickPrologue.data(),
                OriginalStorageUpgradeTickPrologue.size()) == 0)
        {
            return PatchState::Original;
        }

        std::array<unsigned char, 6> replacement{};
        if (BuildStorageUpgradeButtonHookBytes(replacement)
            && std::memcmp(
                target,
                replacement.data(),
                replacement.size()) == 0)
        {
            return PatchState::Applied;
        }
        return PatchState::Mismatch;
    }

    bool WriteStorageUpgradeButtonHook() noexcept
    {
        std::array<unsigned char, 6> replacement{};
        return BuildStorageUpgradeButtonHookBytes(replacement)
            && dfl::memory_patch::WriteBytes(
                StorageUpgradeTickFunction,
                replacement.data(),
                replacement.size());
    }

    bool RestoreStorageUpgradeTickPrologue() noexcept
    {
        return dfl::memory_patch::WriteBytes(
            StorageUpgradeTickFunction,
            OriginalStorageUpgradeTickPrologue.data(),
            OriginalStorageUpgradeTickPrologue.size());
    }

    bool WritePetGridViewHook() noexcept
    {
        std::array<unsigned char, 5> replacement{};
        return BuildPetGridViewHookBytes(replacement)
            && dfl::memory_patch::WriteBytes(
                PetGridViewInit,
                replacement.data(),
                replacement.size());
    }

    bool RestorePetGridViewInit() noexcept
    {
        return dfl::memory_patch::WriteBytes(
            PetGridViewInit,
            OriginalPetGridViewInit.data(),
            OriginalPetGridViewInit.size());
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
        const auto& settings = dfl::ijl15_config::Get();

        // Register the local font before the client reaches its own font
        // creation, so GDI resolves the family it asks for against the file
        // shipped in the client directory. A failure is non-fatal: the client
        // keeps using the installed system fonts.
        const bool localFontRegistered =
            dfl::font_resources::RegisterLocalFonts();
        const std::array<CodePatch, 38> patches = BuildPatches();
        if (!ValidatePatchRanges(patches))
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: patch addresses are outside the client image.\n");
            return false;
        }

        std::array<PatchState, patches.size()> states{};
        for (std::size_t index = 0; index < patches.size(); ++index)
        {
            if (!IsPatchEnabled(patches[index]))
            {
                states[index] = PatchState::Applied;
                continue;
            }
            states[index] = Inspect(patches[index]);
            if (states[index] == PatchState::Mismatch)
            {
                OutputDebugStringA(
                    "DAFLegacy.Ijl15: DNF patch bytes do not match.\n");
                return false;
            }
        }

        const PatchState etcStackableUseModeState =
            settings.EnableCustomStackableUseMode1
                ? InspectEtcStackableUseModeHook()
                : PatchState::Applied;
        if (etcStackableUseModeState == PatchState::Mismatch)
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: [etc] stackable use-mode bytes do not match.\n");
            return false;
        }

        const PatchState mailboxDirtyHookState =
            settings.FixMailboxArchiveRefresh
                ? InspectMailboxDirtyHook()
                : PatchState::Applied;
        if (mailboxDirtyHookState == PatchState::Mismatch)
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: mailbox dirty-write bytes do not match 2008DF.\n");
            return false;
        }

        const PatchState inventorySortTabHookState =
            settings.SendInventorySortTab
                ? InspectInventorySortTabHook()
                : PatchState::Applied;
        if (inventorySortTabHookState == PatchState::Mismatch)
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: inventory sort payload bytes do not match 2008DF.\n");
            return false;
        }

        const PatchState petGridViewHookState =
            settings.SendInventorySortTab
                ? InspectPetGridViewHook()
                : PatchState::Applied;
        if (petGridViewHookState == PatchState::Mismatch)
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: pet-grid view bytes do not match 2008DF.\n");
            return false;
        }

        const PatchState storageUpgradeButtonHookState =
            settings.UnblockStorageUpgradeButton
                ? InspectStorageUpgradeButtonHook()
                : PatchState::Applied;
        if (storageUpgradeButtonHookState == PatchState::Mismatch)
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: storage-upgrade tick bytes do not match 2008DF.\n");
            return false;
        }

        const external_string_table::HookState stringTableHookState =
            settings.UseExternalDnfStringTable
                ? external_string_table::InspectHook()
                : external_string_table::HookState::Installed;
        if (stringTableHookState
            == external_string_table::HookState::Mismatch)
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: dnf.str extraction call does not match 2008DF.\n");
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
                "DAFLegacy.Ijl15: client page URL bytes do not match.\n");
            return false;
        }

        bool diamondUrlChanged = false;
        bool payUrlChanged = false;        if (diamondUrlState == PointerPatchState::Original)
        {
            if (!WriteClientPageUrl(
                    ClientDiamondUrlOperand,
                    LocalClientDiamondUrl))
            {
                RestoreClientPageUrl(
                    ClientDiamondUrlOperand,
                    OriginalClientDiamondUrl);
                OutputDebugStringA(
                    "DAFLegacy.Ijl15: failed to redirect diamond URL.\n");
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
                    "DAFLegacy.Ijl15: failed to redirect pay URL.\n");
                return false;
            }
            payUrlChanged = true;
        }

        // Point the engine's text-font creations at the family of the locally
        // registered file. Only the sizes selected by LocalFontFamilyTarget
        // are redirected: the 18px font has a single consumer (the large-text
        // window component built by sub_815B20) while the 12px font drives the
        // rest of the interface. This runs only when the registration
        // succeeded, because an unregistered family would make GDI substitute
        // an arbitrary face. Both a byte mismatch and a failed write are
        // non-fatal here: the font is cosmetic and must not disable the
        // protocol patches.
        bool textFontFamilyChanged = false;
        if (localFontRegistered && settings.LocalFontFamily[0] != '\0')
        {
            const bool wantsSmall = _stricmp(
                    settings.LocalFontFamilyTarget.data(), "small") == 0
                || _stricmp(
                    settings.LocalFontFamilyTarget.data(), "both") == 0;
            const bool wantsLarge = _stricmp(
                    settings.LocalFontFamilyTarget.data(), "large") == 0
                || _stricmp(
                    settings.LocalFontFamilyTarget.data(), "both") == 0;
            if (!wantsSmall && !wantsLarge)
            {
                OutputDebugStringA(
                    "DAFLegacy.Ijl15: text font family target is unknown; keeping the stock family.\n");
            }
            else
            {
                const PointerPatchState smallFontFamilyState =
                    InspectClientPageUrl(
                        TextFontSmallNameOperand,
                        OriginalTextFontName,
                        settings.LocalFontFamily.data());
                const PointerPatchState largeFontFamilyState =
                    InspectClientPageUrl(
                        TextFontLargeNameOperand,
                        OriginalTextFontName,
                        settings.LocalFontFamily.data());
                if ((wantsSmall
                        && smallFontFamilyState == PointerPatchState::Mismatch)
                    || (wantsLarge
                        && largeFontFamilyState == PointerPatchState::Mismatch))
                {
                    OutputDebugStringA(
                        "DAFLegacy.Ijl15: text font family operands do not match; keeping the stock family.\n");
                }
                else
                {
                    if (wantsSmall
                        && smallFontFamilyState == PointerPatchState::Original)
                    {
                        textFontFamilyChanged = WriteClientPageUrl(
                            TextFontSmallNameOperand,
                            settings.LocalFontFamily.data());
                    }
                    if (wantsLarge
                        && largeFontFamilyState == PointerPatchState::Original)
                    {
                        textFontFamilyChanged = WriteClientPageUrl(
                            TextFontLargeNameOperand,
                            settings.LocalFontFamily.data())
                            || textFontFamilyChanged;
                    }
                    if (!textFontFamilyChanged)
                    {
                        OutputDebugStringA(
                            "DAFLegacy.Ijl15: text font family was not redirected.\n");
                    }
                }
            }
        }

        std::size_t appliedCount = 0;
        for (std::size_t index = 0; index < patches.size(); ++index)
        {
            if (!IsPatchEnabled(patches[index])
                || states[index] == PatchState::Applied)
            {
                continue;
            }

            if (!memory_patch::WriteBytes(
                patches[index].Address,
                patches[index].Replacement.data(),
                patches[index].Replacement.size()))
            {
                for (std::size_t rollback = 0; rollback < index; ++rollback)
                {
                    if (states[rollback] == PatchState::Original)
                    {
                        memory_patch::WriteBytes(
                            patches[rollback].Address,
                            patches[rollback].Expected.data(),
                            patches[rollback].Expected.size());
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
                    "DAFLegacy.Ijl15: failed to apply DNF patches.\n");
                return false;
            }
            ++appliedCount;
        }

        bool mailboxDirtyHookChanged = false;
        if (settings.FixMailboxArchiveRefresh
            && mailboxDirtyHookState == PatchState::Original)
        {
            if (!WriteMailboxDirtyHook())
            {
                RestoreMailboxDirtyWrite();
                for (std::size_t rollback = 0;
                    rollback < patches.size();
                    ++rollback)
                {
                    if (IsPatchEnabled(patches[rollback])
                        && states[rollback] == PatchState::Original)
                    {
                        memory_patch::WriteBytes(
                            patches[rollback].Address,
                            patches[rollback].Expected.data(),
                            patches[rollback].Expected.size());
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
                    "DAFLegacy.Ijl15: failed to install mailbox refresh hook.\n");
                return false;
            }
            mailboxDirtyHookChanged = true;
        }

        bool etcStackableUseModeChanged = false;
        if (settings.EnableCustomStackableUseMode1
            && etcStackableUseModeState == PatchState::Original)
        {
            if (!WriteEtcStackableUseModeHook())
            {
                RestoreEtcStackableUseModeFunction();
                for (std::size_t rollback = 0;
                    rollback < patches.size();
                    ++rollback)
                {
                    if (IsPatchEnabled(patches[rollback])
                        && states[rollback] == PatchState::Original)
                    {
                        memory_patch::WriteBytes(
                            patches[rollback].Address,
                            patches[rollback].Expected.data(),
                            patches[rollback].Expected.size());
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
                    "DAFLegacy.Ijl15: failed to install [etc] stackable use-mode hook.\n");
                return false;
            }
            etcStackableUseModeChanged = true;
        }

        bool stringTableHookChanged = false;
        if (settings.UseExternalDnfStringTable
            && stringTableHookState
                == external_string_table::HookState::Original)
        {
            if (!external_string_table::InstallHook())
            {
                for (std::size_t rollback = 0;
                    rollback < patches.size();
                    ++rollback)
                {
                    if (IsPatchEnabled(patches[rollback])
                        && states[rollback] == PatchState::Original)
                    {
                        memory_patch::WriteBytes(
                            patches[rollback].Address,
                            patches[rollback].Expected.data(),
                            patches[rollback].Expected.size());
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
                    "DAFLegacy.Ijl15: failed to install the external dnf.str hook.\n");
                return false;
            }
            stringTableHookChanged = true;
        }

        if (dfl::ijl15_config::Get().EnablePerformanceOverlay
            && !dfl::performance_overlay::InstallCallHook())
        {
            for (std::size_t rollback = 0; rollback < patches.size(); ++rollback)
            {
                if (IsPatchEnabled(patches[rollback])
                    && states[rollback] == PatchState::Original)
                {
                    memory_patch::WriteBytes(
                        patches[rollback].Address,
                        patches[rollback].Expected.data(),
                        patches[rollback].Expected.size());
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
                "DAFLegacy.Ijl15: failed to install FPS/memory renderer.\n");
            return false;
        }

        if (settings.DisableType16ChatMessageSound
            && !dfl::message_audio::InstallType16ChatSoundHook())
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: failed to install Type16 chat-sound hook.\n");
            return false;
        }

        // This is deliberately installed after the fatal code hooks. If a
        // different 2008DF build has incompatible window bytes, retain the
        // native 800x600 startup size instead of disabling the JPEG proxy.
        if (!dfl::window_scaling::Install())
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: window scaling was not installed; using native size.\n");
        }

        // 2008DF is a D3D8 client. The D3D9/XBRZ hook used by the newer
        // client cannot be reused here, so install the compatible sampler
        // policy before the client creates its D3D8 device.
        if (dfl::ijl15_config::Get().ScaleFilter
                != dfl::ijl15_config::ScaleFilterMode::Native
            && !dfl::d3d8_scaler::Install())
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: D3D8 scale filter was not installed; using native filtering.\n");
        }

        // Install this last so a failed path hook cannot leave a live DNF
        // jump pointing at an IJL15 module that DllMain subsequently rejects.
        // A path-hook failure is non-fatal: all original client paths remain
        // intact and the DLL can still provide the JPEG compatibility layer.
        if (settings.RedirectClientOutputPaths
            && !dfl::output_paths::InstallHook())
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: relative output path hook was not installed.\n");
        }

        // Install the wheel path only after all fatal patch checks above have
        // succeeded. A failure is non-fatal: the original mouse input and
        // all other IJL15 hooks remain available when this optional UI hook
        // does not match.
        if (dfl::ijl15_config::Get().EnableMouseWheel
            && !dfl::mouse_wheel::InstallHook())
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: mouse-wheel scroll hook was not installed.\n");
        }

        // Non-fatal as well: without the pump hook the stock frame pump runs
        // untouched and the killer-mode flag simply keeps its native
        // behaviour (the //allkill toggle still works).
        if (dfl::ijl15_config::Get().EnableDungeonKillerMode
            && !dfl::dungeon_killer_mode::InstallHook())
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: dungeon killer-mode sync was not installed.\n");
        }

        // Installed after every fatal check and write: a failure here leaves
        // the stock three-byte sort request in place, which the server still
        // accepts as a whole-space sort.
        bool inventorySortTabChanged = false;
        if (settings.SendInventorySortTab
            && inventorySortTabHookState == PatchState::Original)
        {
            if (!WriteInventorySortTabHook())
            {
                RestoreInventorySortAppend();
                OutputDebugStringA(
                    "DAFLegacy.Ijl15: inventory sort tab hook was not installed.\n");
            }
            else
            {
                inventorySortTabChanged = true;
            }
        }

        if (settings.SendInventorySortTab
            && petGridViewHookState == PatchState::Original)
        {
            if (!WritePetGridViewHook())
            {
                // The payload hook stays useful for the bag: without a cached
                // pet-grid view the pet space reports 0xFF, i.e. whole-space.
                RestorePetGridViewInit();
                OutputDebugStringA(
                    "DAFLegacy.Ijl15: pet-grid view hook was not installed.\n");
            }
        }

        if (settings.UnblockStorageUpgradeButton
            && storageUpgradeButtonHookState == PatchState::Original)
        {
            if (!WriteStorageUpgradeButtonHook())
            {
                RestoreStorageUpgradeTickPrologue();
                OutputDebugStringA(
                    "DAFLegacy.Ijl15: storage-upgrade button hook was not installed.\n");
            }
        }

        if (appliedCount != 0
            || diamondUrlChanged
            || payUrlChanged
            || etcStackableUseModeChanged
            || mailboxDirtyHookChanged
            || inventorySortTabChanged
            || localFontRegistered
            || textFontFamilyChanged
            || stringTableHookChanged)
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: DNF process patches applied.\n");
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
