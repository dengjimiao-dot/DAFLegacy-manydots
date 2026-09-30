#pragma once

namespace dfl::message_audio
{
    // Keep Type16Brown visible but suppress the client's CHAT_MESSAGE sound.
    bool InstallType16ChatSoundHook() noexcept;
}
