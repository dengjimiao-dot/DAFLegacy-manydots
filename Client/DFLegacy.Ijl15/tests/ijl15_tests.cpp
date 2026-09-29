#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <vector>

#define IJL15_EXPORTS
#include "dnf_patches.h"
#include "external_string_table.h"
#include "ijl15.h"
#include "memory_patch.h"
#include "output_paths.h"

namespace
{
struct IjlApi
{
    HMODULE Module;
    const IJLibVersion* (__stdcall* GetLibVersion)();
    IJLERR (__stdcall* Init)(JPEG_CORE_PROPERTIES*);
    IJLERR (__stdcall* Free)(JPEG_CORE_PROPERTIES*);
    IJLERR (__stdcall* Read)(JPEG_CORE_PROPERTIES*, IJLIOTYPE);
    IJLERR (__stdcall* Write)(JPEG_CORE_PROPERTIES*, IJLIOTYPE);
    const char* (__stdcall* ErrorStr)(IJLERR);
};

int Fail(const char* message)
{
    std::fprintf(stderr, "FAIL: %s\n", message);
    return 1;
}

bool IsClose(unsigned char actual, unsigned char expected, int tolerance)
{
    return std::abs(
        static_cast<int>(actual) - static_cast<int>(expected)) <= tolerance;
}

template<typename T>
T Resolve(HMODULE module, const char* name)
{
    return reinterpret_cast<T>(GetProcAddress(module, name));
}

bool LoadApi(IjlApi* api)
{
    api->Module = LoadLibraryW(L"ijl15.dll");
    if (api->Module == nullptr)
    {
        return false;
    }

    api->GetLibVersion = Resolve<decltype(api->GetLibVersion)>(
        api->Module,
        "ijlGetLibVersion");
    api->Init = Resolve<decltype(api->Init)>(api->Module, "ijlInit");
    api->Free = Resolve<decltype(api->Free)>(api->Module, "ijlFree");
    api->Read = Resolve<decltype(api->Read)>(api->Module, "ijlRead");
    api->Write = Resolve<decltype(api->Write)>(api->Module, "ijlWrite");
    api->ErrorStr = Resolve<decltype(api->ErrorStr)>(
        api->Module,
        "ijlErrorStr");
    return api->GetLibVersion != nullptr
        && api->Init != nullptr
        && api->Free != nullptr
        && api->Read != nullptr
        && api->Write != nullptr
        && api->ErrorStr != nullptr;
}

bool TestMemoryPatchHelpers()
{
    constexpr std::size_t RegionSize = 64;
    void* const region = VirtualAlloc(
        nullptr,
        RegionSize,
        MEM_COMMIT | MEM_RESERVE,
        PAGE_EXECUTE_READWRITE);
    if (region == nullptr)
    {
        return false;
    }

    std::memset(region, 0x5A, RegionSize);
    DWORD oldProtection = 0;
    if (!VirtualProtect(
            region,
            RegionSize,
            PAGE_EXECUTE_READ,
            &oldProtection))
    {
        VirtualFree(region, 0, MEM_RELEASE);
        return false;
    }

    const std::uintptr_t base =
        reinterpret_cast<std::uintptr_t>(region);
    const std::array<unsigned char, 4> bytes = {
        0x10,
        0x20,
        0x30,
        0x40
    };
    bool passed = dfl::memory_patch::WriteBytes(
        base,
        bytes.data(),
        bytes.size());
    passed = passed
        && std::memcmp(region, bytes.data(), bytes.size()) == 0;

    passed = passed && dfl::memory_patch::WriteNops(base + 4, 4);
    const auto* const memory =
        static_cast<const unsigned char*>(region);
    passed = passed
        && std::all_of(memory + 4, memory + 8, [](unsigned char value)
        {
            return value == 0x90;
        });

    passed = passed && dfl::memory_patch::Fill(base + 8, 0, 4);
    passed = passed
        && std::all_of(memory + 8, memory + 12, [](unsigned char value)
        {
            return value == 0;
        });

    constexpr std::size_t JumpOffset = 16;
    constexpr std::size_t CallOffset = 24;
    constexpr std::size_t JumpDestinationOffset = 48;
    constexpr std::size_t CallDestinationOffset = 52;
    passed = passed && dfl::memory_patch::WriteRelativeJump(
        base + JumpOffset,
        base + JumpDestinationOffset);
    passed = passed && memory[JumpOffset] == 0xE9;
    std::int32_t displacement = 0;
    std::memcpy(
        &displacement,
        memory + JumpOffset + 1,
        sizeof(displacement));
    passed = passed
        && base + JumpOffset + 5 + displacement
            == base + JumpDestinationOffset;

    passed = passed && dfl::memory_patch::WriteRelativeCall(
        base + CallOffset,
        base + CallDestinationOffset);
    passed = passed && memory[CallOffset] == 0xE8;
    std::memcpy(
        &displacement,
        memory + CallOffset + 1,
        sizeof(displacement));
    passed = passed
        && base + CallOffset + 5 + displacement
            == base + CallDestinationOffset;

    passed = passed
        && dfl::memory_patch::WriteBreakpoints(base + 32, 4);
    passed = passed
        && std::all_of(memory + 32, memory + 36, [](unsigned char value)
        {
            return value == 0xCC;
        });
    passed = passed
        && !dfl::memory_patch::WriteRelativeJump(0, base);
    passed = passed
        && !dfl::memory_patch::WriteRelativeCall(
            0x00010000,
            0xF0000000);

    MEMORY_BASIC_INFORMATION information{};
    passed = passed
        && VirtualQuery(
            region,
            &information,
            sizeof(information)) == sizeof(information)
        && information.Protect == PAGE_EXECUTE_READ;

    VirtualFree(region, 0, MEM_RELEASE);
    return passed;
}

bool TestCustomStackableUseModePolicy()
{
    using dfl::dnf_patches::ShouldUseStackableMode1;

    return dfl::dnf_patches::EnableCustomStackableUseMode1
        && ShouldUseStackableMode1(7'518u, 10u)
        && ShouldUseStackableMode1(7'519u, 10u)
        && ShouldUseStackableMode1(1'121u, 10u)
        && ShouldUseStackableMode1(1'122u, 10u)
        && ShouldUseStackableMode1(1'123u, 10u)
        && !ShouldUseStackableMode1(7'517u, 10u)
        && ShouldUseStackableMode1(71u, 13u)
        && ShouldUseStackableMode1(0u, 13u)
        && !ShouldUseStackableMode1(71u, 0u);
}

bool TestCustomPopupPatchConfiguration()
{
    return dfl::dnf_patches::RemoveCustomPopupAnnouncementTitle;
}

bool TestMailboxArchiveRefreshPatchConfiguration()
{
    return dfl::dnf_patches::FixMailboxArchiveRefresh;
}

bool TestCeraPurchaseGatePolicy()
{
    using Gate = dfl::dnf_patches::CeraPurchaseGate;
    using dfl::dnf_patches::ShouldBypassCeraPurchaseGate;

    return ShouldBypassCeraPurchaseGate(Gate::LegacySaleWindow)
        && ShouldBypassCeraPurchaseGate(Gate::PerCharacterLimit)
        && ShouldBypassCeraPurchaseGate(Gate::CharacterLevel)
        && !ShouldBypassCeraPurchaseGate(Gate::ClientInventoryCapacity)
        && !ShouldBypassCeraPurchaseGate(Gate::JobCompatibility)
        && !ShouldBypassCeraPurchaseGate(Gate::CurrencyBalance)
        && !ShouldBypassCeraPurchaseGate(Gate::RequiredOption)
        && !ShouldBypassCeraPurchaseGate(Gate::GiftEligibility);
}

bool TestExternalStringTableConfiguration()
{
    using Validation = dfl::external_string_table::EncodingValidation;

    constexpr unsigned char Cp936Text[] = {
        '7', '2', '3', '>',
        0xCD, 0xE6,
        0xBC, 0xD2,
        '\r', '\n'};
    constexpr unsigned char Utf8Text[] = {
        '7', '2', '3', '>',
        0xE7, 0x8E, 0xA9,
        0xE5, 0xAE, 0xB6,
        '\r', '\n'};
    constexpr unsigned char Utf8Bom[] = {
        0xEF, 0xBB, 0xBF, '7', '2', '3', '>'};
    constexpr unsigned char InvalidCp936[] = {
        '7', '2', '3', '>', 0x81, 0x7F};
    constexpr unsigned char EmbeddedNull[] = {
        '7', '2', '3', '>', 0, 'x'};

    wchar_t path[MAX_PATH]{};
    return dfl::dnf_patches::UseExternalDnfStringTable
        && std::wcscmp(
            dfl::external_string_table::RelativePath,
            L"Plugin\\dnf.str") == 0
        && dfl::external_string_table::BuildPluginPath(
            L"D:\\DFLegacy\\DF2008\\DNF.exe",
            path,
            MAX_PATH)
        && std::wcscmp(
            path,
            L"D:\\DFLegacy\\DF2008\\Plugin\\dnf.str") == 0
        && dfl::external_string_table::ValidateCp936(
            Cp936Text,
            sizeof(Cp936Text)) == Validation::ValidCp936
        && dfl::external_string_table::ValidateCp936(
            Utf8Text,
            sizeof(Utf8Text)) == Validation::Utf8
        && dfl::external_string_table::ValidateCp936(
            Utf8Bom,
            sizeof(Utf8Bom)) == Validation::UnicodeBom
        && dfl::external_string_table::ValidateCp936(
            InvalidCp936,
            sizeof(InvalidCp936)) == Validation::InvalidCp936
        && dfl::external_string_table::ValidateCp936(
            EmbeddedNull,
            sizeof(EmbeddedNull)) == Validation::EmbeddedNull;
}

bool TestRelativeOutputPathPolicy()
{
    char output[128]{};
    if (!dfl::output_paths::BuildRelativePath(
            "DNF.trc",
            output,
            sizeof(output))
        || std::strcmp(output, "Logs\\DNF.trc") != 0)
    {
        return false;
    }

    std::memset(output, 0, sizeof(output));
    if (!dfl::output_paths::BuildRelativePath(
            "Low\\DNF\\NeopleEngine.LOG",
            output,
            sizeof(output))
        || std::strcmp(output, "Logs\\NeopleEngine.LOG") != 0)
    {
        return false;
    }

    std::memset(output, 0, sizeof(output));
    if (!dfl::output_paths::BuildRelativePath(
            "SomeOtherClient.LOG",
            output,
            sizeof(output))
        || std::strcmp(output, "Logs\\SomeOtherClient.LOG") != 0)
    {
        return false;
    }

    std::memset(output, 0, sizeof(output));
    if (!dfl::output_paths::BuildRelativePath(
            "ScreenShot",
            output,
            sizeof(output))
        || std::strcmp(output, "ScreenShot\\") != 0)
    {
        return false;
    }

    std::memset(output, 0, sizeof(output));
    if (!dfl::output_paths::BuildRelativePath(
            "ScreenShot\\ScreenShot00004.JPG",
            output,
            sizeof(output))
        || std::strcmp(output, "ScreenShot\\ScreenShot00004.JPG") != 0)
    {
        return false;
    }

    return !dfl::output_paths::BuildRelativePath(
        "Interface\\premiumService.img",
        output,
        sizeof(output));
}
}

int main()
{
    if (!TestMemoryPatchHelpers())
    {
        return Fail("memory patch helpers failed");
    }
    if (!TestCustomStackableUseModePolicy())
    {
        return Fail("[etc] stackable mode-1 list failed");
    }
    if (!TestCustomPopupPatchConfiguration())
    {
        return Fail("custom popup announcement-title patch is disabled");
    }

    if (!TestMailboxArchiveRefreshPatchConfiguration())
    {
        return Fail("mailbox archive refresh patch is disabled");
    }
    if (!TestCeraPurchaseGatePolicy())
    {
        return Fail("Cera purchase-gate policy is invalid");
    }
    if (!TestExternalStringTableConfiguration())
    {
        return Fail("external CP936 dnf.str configuration failed");
    }
    if (!TestRelativeOutputPathPolicy())
    {
        return Fail("relative Logs/ScreenShot output path policy failed");
    }

    IjlApi api{};
    if (!LoadApi(&api))
    {
        return Fail("could not load the six IJL exports");
    }

    JPEG_CORE_PROPERTIES properties{};
    if (api.Init(&properties) != IJL_OK)
    {
        return Fail("ijlInit failed");
    }
    if (properties.DIBChannels != 3
        || properties.DIBColor != IJL_BGR
        || properties.JPGChannels != 3
        || properties.JPGColor != IJL_YCBCR
        || properties.jquality != 75)
    {
        return Fail("ijlInit defaults do not match IJL 1.5");
    }

    // A top-down RGB image with four colored quadrants. The DNF_v1.0 screenshot
    // path supplies this row order with a positive DIBHeight.
    constexpr int width = 16;
    constexpr int height = 16;
    std::vector<unsigned char> rgb(width * height * 3);
    for (int y = 0; y < height; ++y)
    {
        for (int x = 0; x < width; ++x)
        {
            unsigned char* pixel =
                rgb.data() + static_cast<std::size_t>(y * width + x) * 3;
            pixel[0] = x < width / 2 ? 230 : 30;
            pixel[1] = y < height / 2 ? 220 : 20;
            pixel[2] = x < width / 2 ? 20 : 225;
        }
    }

    std::vector<unsigned char> jpeg(width * height * 6);
    properties.DIBBytes = rgb.data();
    properties.DIBWidth = width;
    properties.DIBHeight = height;
    properties.DIBPadBytes = 0;
    properties.DIBChannels = 3;
    properties.DIBColor = IJL_RGB;
    properties.JPGBytes = jpeg.data();
    properties.JPGSizeBytes = static_cast<int>(jpeg.size());
    properties.JPGWidth = width;
    properties.JPGHeight = height;
    properties.JPGChannels = 3;
    properties.JPGColor = IJL_YCBCR;
    properties.JPGSubsampling = IJL_411;
    properties.jquality = 95;

    if (api.Write(&properties, IJL_JBUFF_WRITEWHOLEIMAGE) != IJL_OK)
    {
        return Fail("buffer JPEG encoding failed");
    }
    const int jpegSize = properties.JPGSizeBytes;
    if (jpegSize < 4
        || jpeg[0] != 0xFF
        || jpeg[1] != 0xD8
        || jpeg[jpegSize - 2] != 0xFF
        || jpeg[jpegSize - 1] != 0xD9)
    {
        return Fail("encoded output is not a complete JPEG");
    }

    JPEG_CORE_PROPERTIES decoded{};
    if (api.Init(&decoded) != IJL_OK)
    {
        return Fail("decode ijlInit failed");
    }
    decoded.JPGBytes = jpeg.data();
    decoded.JPGSizeBytes = jpegSize;
    if (api.Read(&decoded, IJL_JBUFF_READPARAMS) != IJL_OK
        || decoded.JPGWidth != width
        || decoded.JPGHeight != height
        || decoded.JPGChannels != 3)
    {
        return Fail("buffer JPEG parameter read failed");
    }

    constexpr int padding = 4;
    const int decodedStride = width * 3 + padding;
    std::vector<unsigned char> bgr(
        static_cast<std::size_t>(decodedStride * height),
        0xCD);
    decoded.DIBBytes = bgr.data();
    decoded.DIBWidth = width;
    decoded.DIBHeight = height;
    decoded.DIBPadBytes = padding;
    decoded.DIBChannels = 3;
    decoded.DIBColor = IJL_BGR;
    if (api.Read(&decoded, IJL_JBUFF_READWHOLEIMAGE) != IJL_OK)
    {
        return Fail("buffer JPEG decode failed");
    }

    // Positive decode height is bottom-up, so the first row is the image bottom.
    const unsigned char* bottomLeft = bgr.data();
    if (!IsClose(bottomLeft[0], 20, 45)
        || !IsClose(bottomLeft[1], 20, 45)
        || !IsClose(bottomLeft[2], 230, 45))
    {
        return Fail("positive-height encoding or bottom-up decoding is wrong");
    }
    if (!std::all_of(
            bottomLeft + width * 3,
            bottomLeft + decodedStride,
            [](unsigned char value) { return value == 0xCD; }))
    {
        return Fail("DIB row padding was modified");
    }

    std::array<char, MAX_PATH> temporaryDirectory{};
    std::array<char, MAX_PATH> temporaryFile{};
    if (GetTempPathA(
            static_cast<DWORD>(temporaryDirectory.size()),
            temporaryDirectory.data()) == 0
        || GetTempFileNameA(
            temporaryDirectory.data(),
            "ijl",
            0,
            temporaryFile.data()) == 0)
    {
        return Fail("could not create a temporary JPEG path");
    }

    properties.JPGFile = temporaryFile.data();
    if (api.Write(&properties, IJL_JFILE_WRITEWHOLEIMAGE) != IJL_OK)
    {
        DeleteFileA(temporaryFile.data());
        return Fail("file JPEG encoding failed");
    }

    JPEG_CORE_PROPERTIES fileDecoded{};
    api.Init(&fileDecoded);
    fileDecoded.JPGFile = temporaryFile.data();
    if (api.Read(&fileDecoded, IJL_JFILE_READPARAMS) != IJL_OK
        || fileDecoded.JPGWidth != width
        || fileDecoded.JPGHeight != height)
    {
        DeleteFileA(temporaryFile.data());
        return Fail("file JPEG parameter read failed");
    }

    std::vector<unsigned char> filePixels(width * height * 3);
    fileDecoded.DIBBytes = filePixels.data();
    fileDecoded.DIBWidth = width;
    fileDecoded.DIBHeight = -height;
    fileDecoded.DIBChannels = 3;
    fileDecoded.DIBColor = IJL_BGR;
    if (api.Read(&fileDecoded, IJL_JFILE_READWHOLEIMAGE) != IJL_OK)
    {
        DeleteFileA(temporaryFile.data());
        return Fail("file JPEG decode failed");
    }
    DeleteFileA(temporaryFile.data());

    const IJLibVersion* version = api.GetLibVersion();
    if (version == nullptr
        || version->major != 1
        || version->minor != 5
        || version->build != 4)
    {
        return Fail("version export is invalid");
    }
    if (std::strcmp(api.ErrorStr(IJL_BUFFER_TOO_SMALL),
            "Output buffer too small") != 0)
    {
        return Fail("error string export is invalid");
    }
    if (api.Free(&fileDecoded) != IJL_OK
        || api.Free(&decoded) != IJL_OK
        || api.Free(&properties) != IJL_OK)
    {
        return Fail("ijlFree failed");
    }

    FreeLibrary(api.Module);
    std::puts("ijl15 compatibility tests passed");
    return 0;
}
