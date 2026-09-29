#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <cstdint>
#include <cstring>

#include "memory_patch.h"
#include "message_audio.h"

namespace
{
    constexpr std::uintptr_t ChatMessageEventCall = 0x00846696;
    constexpr std::uintptr_t OriginalChatMessageEvent = 0x00433890;
    constexpr std::uintptr_t HookStackToCallerEbp =
        5 * sizeof(std::uint32_t); // Four event arguments plus the hook return.
    constexpr std::uintptr_t MessageTypeOffsetFromCallerEbp = 0x0C;
    constexpr std::uintptr_t MessageTypeOffsetFromHookEsp =
        HookStackToCallerEbp + MessageTypeOffsetFromCallerEbp;
    static_assert(MessageTypeOffsetFromHookEsp == 0x20);

    // sub_846670 first calls sub_845B60, whose `retn 14h` restores ESP to EBP,
    // then pushes four CHAT_MESSAGE arguments. At this hook's entry the caller's
    // n13 message type therefore resides at [esp+0x20]. For non-Type16 messages,
    // tail-jump to the original function so it sees the untouched return address
    // and four arguments. Calling it from here would insert another return address
    // and shift every argument by four bytes.
    __declspec(naked) void Type16ChatSoundGate()
    {
        __asm
        {
            cmp dword ptr [esp + 20h], 10h
            je skip_chat_message_event

            mov eax, 00433890h
            jmp eax

        skip_chat_message_event:
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
        const auto target = static_cast<std::int64_t>(callAddress + 5)
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
}

namespace dfl::message_audio
{
    bool InstallType16ChatSoundHook() noexcept
    {
        std::uintptr_t currentDestination = 0;
        if (!GetCallDestination(ChatMessageEventCall, currentDestination))
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: CHAT_MESSAGE call site is not E8.\n");
            return false;
        }

        const auto hookAddress = reinterpret_cast<std::uintptr_t>(
            &Type16ChatSoundGate);
        if (currentDestination == hookAddress)
        {
            return true;
        }
        if (currentDestination != OriginalChatMessageEvent)
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: CHAT_MESSAGE call target does not match 2008DF.\n");
            return false;
        }

        if (!dfl::memory_patch::WriteRelativeCall(
                ChatMessageEventCall,
                hookAddress))
        {
            OutputDebugStringA(
                "DFLegacy.Ijl15: failed to install Type16 chat-sound hook.\n");
            return false;
        }
        return true;
    }
}
