#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <array>
#include <cstdarg>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "dungeon_killer_mode.h"
#include "ijl15_config.h"
#include "memory_patch.h"

namespace
{
    // Hook site: the per-frame pump sub_433B00 ends with two unconditional
    // calls. The first one, the frame-time accounting tick sub_433660, is
    // displaced: 0x00433C0D is its only call site, its return value is dead
    // there (the next statement overwrites eax), and the redirect below keeps
    // the original call inside the hook stub.
    constexpr std::uintptr_t FramePumpTickCall = 0x00433C0D;
    constexpr std::uintptr_t FrameTimeTickFunction = 0x00433660;

    // Scene gate and kill dispatch, matching the client's own "//kill"
    // command (sub_846BA0 branch 0x0084824D): sub_402030(gameContext + 0x10)
    // is the current scene type and 3/6/7 are the map types the command
    // accepts (1 is the town); the world manager dword_B5DCE0 vtable slot
    // +0x80 returns the current map object whose object list sits at +0xB0;
    // items answer their category through vtable slot +0x2C and 0x211 marks
    // the monster class; sub_843AC0 is the kill dispatcher (local death
    // routine or a CMD 42 report per monster state). Kills are only ever
    // dispatched for objects read from the live container list, so the
    // pointer is always valid - recording spawn pointers and dispatching them
    // later froze the client once the player killed a recorded monster during
    // the wait and the recorded pointer dangled.
    //
    // An object is identified by its (pointer, entity id) pair - the id comes
    // from the same vtable slot +56 getter sub_843AC0 writes into the CMD 42
    // body. The pair, not the pointer alone, is the identity: entity ids are
    // per-room indexes and the client reuses monster addresses across room
    // swaps. The per-room reset below re-establishes a clean identity space
    // per room.
    constexpr std::uintptr_t GameObjectPointer = 0x00B5D6F0;
    constexpr std::size_t GameObjectSceneTypeOffset = 0x10;
    constexpr std::uintptr_t WorldManagerPointer = 0x00B5DCE0;
    constexpr std::uintptr_t ProtectedModeGate = 0x00CFD44C;
    constexpr std::array<int, 3> KillableSceneTypes{ 3, 6, 7 };
    constexpr std::size_t WorldMapQueryVtableOffset = 0x80;
    constexpr std::size_t MapObjectListOffset = 0xB0;
    constexpr std::size_t ItemCategoryVtableOffset = 0x2C;
    constexpr std::size_t ItemIdVtableOffset = 0x38;
    constexpr std::size_t ItemLivenessVtableOffset = 0x8C;
    constexpr int MonsterCategoryQuery = 0x211;

    // Room load gate. Dispatching a kill while the client is still loading a
    // room freezes it (observed: the second room froze while its load was in
    // flight; the first room was only held back by the scene-type gate and
    // therefore worked), so the walk waits for two client-native signals:
    //  - the CMD 40 (finish-loading) send sites below: the client announces a
    //    finished load by sending it; the hook only marks the time, it never
    //    sends anything;
    //  - the room-push call site below (the NOTI 29 case looking the room up
    //    through sub_405750): each room push clears the load mark and starts
    //    the fallback timer.
    // Dispatch is allowed once the load mark is RoomLoadConfirmMarginMs old
    // (the first room's dispatch was proven safe 3 seconds after its
    // finish-loading send), or, for paths that never send the mark, once the
    // room push is RoomLoadFallbackMs old. When the gate opens for a room,
    // the tracking table is cleared once - the room's monsters are all new -
    // and the walk dispatches everything it finds alive.
    constexpr std::uintptr_t RoomLookupCallSite = 0x0041F98B;
    constexpr std::uintptr_t RoomLookupFunction = 0x00405750;
    constexpr std::uintptr_t FinishLoadingSendSites[] = {
        0x0089B711,
        0x008A2EB5,
    };
    constexpr std::uintptr_t SendPacketFunction = 0x008B2650;
    // The kill sweep only covers the room's own population: after the
    // gate opens, newly sighted monsters are dispatched for
    // DispatchWindowMs, then only tracked. Entities that spawn later
    // belong to fight choreography - the multi-fight boss's dummy bodies
    // above all - and killing those made the server settle a full kill
    // (drops and quest items) per spawn while the boss itself kept
    // fighting (observed on the real client).
    constexpr unsigned long DispatchWindowMs = 1500;
    constexpr unsigned long RoomLoadConfirmMarginMs = 3000;
    constexpr unsigned long RoomLoadFallbackMs = 8000;
    volatile LONG RoomPushTick = 0;
    volatile LONG RoomLoadConfirmedTick = 0;
    DWORD DispatchWindowUntil = 0;

    // Diagnostic log (temporary, for field triage): appended under the
    // client's Logs\ directory (the same place output_paths redirects the
    // client's own logs to), one line per notable decision. Only the frame
    // loop writes it, so no locking is needed.
    constexpr char KillerLogDirectory[] = "Logs";
    constexpr char KillerLogFileName[] = "DAFLegacy.Ijl15.killer.log";

    void LogKillerEvent(const char* format, ...) noexcept
    {
        char message[256]{};
        va_list arguments;
        va_start(arguments, format);
        const int written = _vsnprintf_s(
            message,
            sizeof(message),
            _TRUNCATE,
            format,
            arguments);
        va_end(arguments);
        if (written <= 0)
        {
            return;
        }

        // <executable directory>\Logs\DAFLegacy.Ijl15.killer.log
        char path[MAX_PATH]{};
        const DWORD length = GetModuleFileNameA(
            nullptr,
            path,
            static_cast<DWORD>(sizeof(path)));
        if (length == 0 || length >= sizeof(path))
        {
            return;
        }
        char* separator = std::strrchr(path, '\\');
        if (separator == nullptr)
        {
            return;
        }
        *separator = '\0';
        const std::size_t directoryLength =
            static_cast<std::size_t>(separator - path);
        constexpr std::size_t LogsLength = sizeof(KillerLogDirectory) - 1;
        constexpr std::size_t FileNameLength = sizeof(KillerLogFileName);
        if (directoryLength + 1 + LogsLength + 1 + FileNameLength
            > sizeof(path))
        {
            return;
        }

        char* cursor = path + directoryLength;
        *cursor++ = '\\';
        std::memcpy(cursor, KillerLogDirectory, LogsLength);
        cursor += LogsLength;
        *cursor = '\0';
        CreateDirectoryA(path, nullptr);
        *cursor++ = '\\';
        std::memcpy(cursor, KillerLogFileName, FileNameLength);

        HANDLE file = CreateFileA(
            path,
            FILE_APPEND_DATA,
            FILE_SHARE_READ,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return;
        }
        DWORD bytesWritten = 0;
        WriteFile(
            file,
            message,
            static_cast<DWORD>(std::strlen(message)),
            &bytesWritten,
            nullptr);
        CloseHandle(file);
    }

    using ProtectedRead = int(__thiscall*)(const void*);
    using MapQuery = void*(__thiscall*)(void*);
    using ContainerCount = int(__thiscall*)(const void*);
    using ContainerAt = void*(__thiscall*)(const void*, unsigned int);
    using ItemCategory = int(__thiscall*)(void*, int);
    using ItemId = int(__thiscall*)(void*);
    using ItemLiveness = int(__thiscall*)(void*);
    using KillDispatch = int(__cdecl*)(void*);
    using RoomLookup = void*(__thiscall*)(void*, unsigned int, unsigned int);
    using SendPacket = void(__thiscall*)(void*, unsigned int);

    bool IsKillableSceneType(int sceneType) noexcept
    {
        for (const int killableSceneType : KillableSceneTypes)
        {
            if (sceneType == killableSceneType)
            {
                return true;
            }
        }
        return false;
    }

    // Continuity table: one slot per (pointer, id) pair seen in the current
    // room. Rooms hold at most 255 monsters and slots absent from a scan are
    // freed, so the fixed capacity is not exhausted in normal play; on the
    // unlikely overflow slot 0 is recycled and the server's own stale-report
    // guard absorbs the possible duplicate.
    struct TrackedMonster
    {
        const void* item;
        unsigned short id;
        std::uint8_t state;
        std::uint32_t seenFrame;
    };
    constexpr std::size_t TrackedCapacity = 256;
    constexpr std::uint8_t TrackedStateAlive = 1;
    constexpr std::uint8_t TrackedStateDeadListed = 2;
    TrackedMonster TrackedMonsters[TrackedCapacity]{};
    std::uint32_t ScanFrame = 0;
    bool RoomStateValid = false;
    LONG LastGateResetPushTick = -1;

    void ResetTrackedMonsters() noexcept
    {
        for (std::size_t index = 0; index < TrackedCapacity; ++index)
        {
            TrackedMonsters[index].item = nullptr;
            TrackedMonsters[index].state = 0;
        }
    }

    std::ptrdiff_t FindTrackedMonster(
        const void* item,
        unsigned short id) noexcept
    {
        for (std::size_t index = 0; index < TrackedCapacity; ++index)
        {
            const TrackedMonster& tracked = TrackedMonsters[index];
            if (tracked.state != 0
                && tracked.item == item
                && tracked.id == id)
            {
                return static_cast<std::ptrdiff_t>(index);
            }
        }
        return -1;
    }

    std::ptrdiff_t TakeFreeTrackedSlot() noexcept
    {
        for (std::size_t index = 0; index < TrackedCapacity; ++index)
        {
            if (TrackedMonsters[index].state == 0)
            {
                return static_cast<std::ptrdiff_t>(index);
            }
        }
        return 0;
    }

    void FreeUnseenTrackedSlots() noexcept
    {
        for (std::size_t index = 0; index < TrackedCapacity; ++index)
        {
            TrackedMonster& tracked = TrackedMonsters[index];
            if (tracked.state != 0
                && tracked.seenFrame != ScanFrame)
            {
                tracked.item = nullptr;
                tracked.state = 0;
            }
        }
    }

    // Room-push hook, network-layer context: the NOTI 29 case looked the
    // incoming room up; a room push starts, so the previous room's load mark
    // no longer applies. Flag-only, then the original call runs.
    void* __fastcall RoomLookupAndClearLoadMark(
        void* thisPtr,
        void*,
        unsigned int roomX,
        unsigned int roomY)
    {
        InterlockedExchange(&RoomLoadConfirmedTick, 0);
        InterlockedExchange(&RoomPushTick, static_cast<LONG>(GetTickCount()));
        return reinterpret_cast<RoomLookup>(RoomLookupFunction)(
            thisPtr,
            roomX,
            roomY);
    }

    // Finish-loading hook, network-layer context: the client announces a
    // finished room load by sending CMD 40. Mark the time (flag-only) and
    // run the original send unchanged.
    void __fastcall FinishLoadingAndMarkLoad(
        void* connection,
        void*,
        unsigned int command)
    {
        reinterpret_cast<SendPacket>(SendPacketFunction)(connection, command);
        InterlockedExchange(
            &RoomLoadConfirmedTick,
            static_cast<LONG>(GetTickCount()));
    }

    // The load gate: true once the finish-loading mark has had its margin to
    // settle (or the fallback elapsed), false while a room is still loading -
    // dispatching into a load freezes the client.
    bool RoomLoadGateOpen() noexcept
    {
        const DWORD now = GetTickCount();
        const LONG confirmedTick = RoomLoadConfirmedTick;
        if (confirmedTick != 0
            && now - static_cast<DWORD>(confirmedTick)
                >= RoomLoadConfirmMarginMs)
        {
            return true;
        }
        const LONG pushTick = RoomPushTick;
        return pushTick != 0
            && now - static_cast<DWORD>(pushTick) >= RoomLoadFallbackMs;
    }

    // One guarded pass per frame. Every pointer is checked before use: the
    // checksummed reader sub_402030 fires the tamper callback on unregistered
    // addresses, so the walk must not run before the game context exists.
    void __cdecl KillNewlyGeneratedMonsters() noexcept
    {
        if (*reinterpret_cast<const unsigned char*>(ProtectedModeGate) == 0)
        {
            if (RoomStateValid)
            {
                ResetTrackedMonsters();
                RoomStateValid = false;
                LastGateResetPushTick = -1;
            }
            return;
        }

        const auto& settings = dfl::ijl15_config::Get();
        if (!settings.EnableDungeonKillerMode)
        {
            return;
        }

        const auto* game = *reinterpret_cast<void* const*>(GameObjectPointer);
        const int sceneType = game != nullptr
            ? reinterpret_cast<ProtectedRead>(0x00402030)(
                static_cast<const unsigned char*>(game)
                + GameObjectSceneTypeOffset)
            : 0;
        if (game == nullptr || !IsKillableSceneType(sceneType))
        {
            // Out of the killable scenes: forget the population so the next
            // dungeon starts from an empty tracking table.
            if (RoomStateValid)
            {
                ResetTrackedMonsters();
                RoomStateValid = false;
                LastGateResetPushTick = -1;
            }
            return;
        }

        // Never dispatch into a room that is still loading: the kill report
        // freezes the client mid-load (see the load-gate comment above).
        if (!RoomLoadGateOpen())
        {
            return;
        }

        // First open frame for this room push: the room's monsters are all
        // new, whatever the table still holds from the previous room is
        // stale. Clearing here is what makes every room's monsters dispatch
        // again even though the client recycles addresses and reuses the
        // per-room entity ids from the previous room.
        const LONG pushTick = RoomPushTick;
        if (pushTick != LastGateResetPushTick)
        {
            LastGateResetPushTick = pushTick;
            ResetTrackedMonsters();
            RoomStateValid = true;
            DispatchWindowUntil = GetTickCount() + DispatchWindowMs;
            LogKillerEvent("gate open: push=%ld\n", pushTick);
        }

        void* world = *reinterpret_cast<void* const*>(WorldManagerPointer);
        if (world == nullptr)
        {
            return;
        }
        auto** worldVtable = *reinterpret_cast<void***>(world);
        if (worldVtable == nullptr
            || worldVtable[WorldMapQueryVtableOffset / sizeof(void*)]
                == nullptr)
        {
            return;
        }
        const auto mapQuery = reinterpret_cast<MapQuery>(
            worldVtable[WorldMapQueryVtableOffset / sizeof(void*)]);

        void* map = mapQuery(world);
        if (map == nullptr)
        {
            return;
        }
        void* container = *reinterpret_cast<void* const*>(
            static_cast<const unsigned char*>(map) + MapObjectListOffset);
        if (container == nullptr)
        {
            return;
        }
        const int count
            = reinterpret_cast<ContainerCount>(0x004048A0)(container);
        if (count <= 0)
        {
            // Nothing in the room: forget the population, so whatever
            // appears next is treated as new.
            ResetTrackedMonsters();
            return;
        }

        // One snapshot per frame: nothing mutates the list synchronously
        // during the walk (deaths apply when NOTI 38 comes back), and the
        // bounds-checked at() absorbs a list that shrank meanwhile.
        ++ScanFrame;
        bool sawMatchedMonster = false;
        for (int index = 0; index < count; ++index)
        {
            void* item
                = reinterpret_cast<ContainerAt>(0x00405830)(container, index);
            if (item == nullptr)
            {
                continue;
            }
            auto** itemVtable = *reinterpret_cast<void***>(item);
            if (itemVtable == nullptr
                || itemVtable[ItemCategoryVtableOffset / sizeof(void*)]
                    == nullptr
                || itemVtable[ItemIdVtableOffset / sizeof(void*)] == nullptr
                || itemVtable[ItemLivenessVtableOffset / sizeof(void*)]
                    == nullptr)
            {
                continue;
            }
            const auto isMonster = reinterpret_cast<ItemCategory>(
                itemVtable[ItemCategoryVtableOffset / sizeof(void*)]);
            // The command tests the low byte of the category answer only.
            if ((isMonster(item, MonsterCategoryQuery) & 0xFF) == 0)
            {
                continue;
            }
            sawMatchedMonster = true;
            const auto getId = reinterpret_cast<ItemId>(
                itemVtable[ItemIdVtableOffset / sizeof(void*)]);
            const auto monsterId = static_cast<unsigned short>(
                getId(item) & 0xFFFF);
            const auto isAliveTarget = reinterpret_cast<ItemLiveness>(
                itemVtable[ItemLivenessVtableOffset / sizeof(void*)]);
            const bool alive = isAliveTarget(item) > 0;

            const std::ptrdiff_t slot
                = FindTrackedMonster(item, monsterId);
            if (slot < 0)
            {
                // First sight of this object. Dispatch it only while the
                // room-entry window is open: the engine's liveness query is
                // logged but must not gate the decision (it read as dead for
                // fully live monsters on the real client), while entities
                // that appear after the window belong to fight choreography
                // and are only tracked.
                const std::ptrdiff_t freeSlot = TakeFreeTrackedSlot();
                TrackedMonsters[freeSlot].item = item;
                TrackedMonsters[freeSlot].id = monsterId;
                TrackedMonsters[freeSlot].state = TrackedStateAlive;
                TrackedMonsters[freeSlot].seenFrame = ScanFrame;
                if (GetTickCount() < DispatchWindowUntil)
                {
                    LogKillerEvent(
                        "dispatch: ptr=%p id=%u category=%#x alive=%d\n",
                        item,
                        static_cast<unsigned int>(monsterId),
                        static_cast<unsigned int>(
                            isMonster(item, MonsterCategoryQuery)),
                        alive ? 1 : 0);
                    reinterpret_cast<KillDispatch>(0x00843AC0)(item);
                }
                else
                {
                    LogKillerEvent(
                        "tracked (window closed): ptr=%p id=%u\n",
                        item,
                        static_cast<unsigned int>(monsterId));
                }
                continue;
            }

            TrackedMonsters[slot].seenFrame = ScanFrame;
            if (!alive)
            {
                TrackedMonsters[slot].state = TrackedStateDeadListed;
                continue;
            }
            if (TrackedMonsters[slot].state == TrackedStateAlive)
            {
                // Alive and dispatched before: nothing to do.
                continue;
            }
            // Dead-listed before, alive now: a scripted revival or a
            // recycled object. Track only - a revival belongs to fight
            // choreography (the multi-fight boss between phases) and
            // dispatching it settled a full kill per phase.
            TrackedMonsters[slot].state = TrackedStateAlive;
            LogKillerEvent(
                "revival tracked: ptr=%p id=%u\n",
                item,
                static_cast<unsigned int>(monsterId));
        }
        FreeUnseenTrackedSlots();
        if (!sawMatchedMonster)
        {
            // The list holds objects but no monsters (players and props
            // only): the room's population is gone, so forget it and treat
            // whatever appears next as new.
            ResetTrackedMonsters();
        }
    }

    __declspec(naked) void DungeonKillTickHook()
    {
        __asm
        {
            // Keep every register and the flag word untouched by the walk.
            pushad
            call KillNewlyGeneratedMonsters
            popad

            // Recreate the displaced call, then return to 0x00433C12 with
            // the tick's return value still in eax.
            mov eax, 433660h
            call eax
            ret
        }
    }

    bool GetCallDestination(
        std::uintptr_t callAddress,
        std::uintptr_t& destination) noexcept
    {
        unsigned char opcode = 0;
        std::memcpy(
            &opcode,
            reinterpret_cast<const void*>(callAddress),
            sizeof(opcode));
        if (opcode != 0xE8)
        {
            return false;
        }

        std::int32_t displacement = 0;
        std::memcpy(
            &displacement,
            reinterpret_cast<const void*>(callAddress + 1),
            sizeof(displacement));
        const std::int64_t target =
            static_cast<std::int64_t>(callAddress + 5)
            + static_cast<std::int64_t>(displacement);
        if (target < 0
            || static_cast<std::uint64_t>(target)
                > static_cast<std::uint64_t>(UINTPTR_MAX))
        {
            return false;
        }

        destination = static_cast<std::uintptr_t>(target);
        return true;
    }

    bool RedirectCallSite(
        std::uintptr_t site,
        std::uintptr_t originalTarget,
        std::uintptr_t hookTarget) noexcept
    {
        std::uintptr_t destination = 0;
        if (!GetCallDestination(site, destination))
        {
            return false;
        }
        if (destination == hookTarget)
        {
            return true;
        }
        if (destination != originalTarget)
        {
            return false;
        }
        return dfl::memory_patch::WriteRelativeCall(site, hookTarget);
    }

    bool InstallLoadGateHooks() noexcept
    {
        if (!RedirectCallSite(
                RoomLookupCallSite,
                RoomLookupFunction,
                reinterpret_cast<std::uintptr_t>(
                    &RoomLookupAndClearLoadMark)))
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: room-push load-mark hook was not installed.\n");
            return false;
        }

        const auto finishLoadingHook = reinterpret_cast<std::uintptr_t>(
            &FinishLoadingAndMarkLoad);
        for (const std::uintptr_t site : FinishLoadingSendSites)
        {
            if (!RedirectCallSite(
                    site,
                    SendPacketFunction,
                    finishLoadingHook))
            {
                OutputDebugStringA(
                    "DAFLegacy.Ijl15: finish-loading load-mark hook was not installed.\n");
                return false;
            }
        }
        return true;
    }
}

namespace dfl::dungeon_killer_mode
{
    bool InstallHook() noexcept
    {
        std::uintptr_t currentDestination = 0;
        if (!GetCallDestination(FramePumpTickCall, currentDestination))
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: frame pump tick call site is not E8.\n");
            return false;
        }

        const auto tickHookAddress = reinterpret_cast<std::uintptr_t>(
            &DungeonKillTickHook);
        if (currentDestination == tickHookAddress)
        {
            return InstallLoadGateHooks();
        }
        if (currentDestination != FrameTimeTickFunction)
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: frame pump tick call target does not match 433660.\n");
            return false;
        }
        if (!dfl::memory_patch::WriteRelativeCall(
                FramePumpTickCall,
                tickHookAddress))
        {
            OutputDebugStringA(
                "DAFLegacy.Ijl15: failed to install the dungeon kill hook.\n");
            return false;
        }
        return InstallLoadGateHooks();
    }
}
