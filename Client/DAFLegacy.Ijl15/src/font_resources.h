#pragma once

namespace dfl::font_resources
{
    // Registers the fonts under the client directory for this process only
    // (AddFontResourceEx with FR_PRIVATE), so the engine keeps requesting the
    // family it hard-codes while GDI resolves it to the local file. Returns
    // true when at least one font was registered.
    bool RegisterLocalFonts() noexcept;
}
