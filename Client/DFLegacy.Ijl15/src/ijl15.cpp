#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <objidl.h>
#include <propidl.h>
#include <shlwapi.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <new>
#include <string>
#include <vector>

#include "ijl15.h"

using Microsoft::WRL::ComPtr;

namespace
{
    constexpr std::uint64_t MaxImageBytes = 512ULL * 1024ULL * 1024ULL;

    const IJLibVersion Version = {
        1,
        5,
        4,
        "ijl15.dll",
        "1.5.4-compatible",
        "DFLegacy.WIC.1",
        __DATE__,
        "Microsoft*"
    };

    class ComApartment final
    {
    public:
        ComApartment()
            : result_(CoInitializeEx(nullptr, COINIT_MULTITHREADED)),
            mustUninitialize_(SUCCEEDED(result_))
        {}

        ~ComApartment()
        {
            if (mustUninitialize_)
            {
                CoUninitialize();
            }
        }

        bool IsAvailable() const
        {
            return SUCCEEDED(result_) || result_ == RPC_E_CHANGED_MODE;
        }

    private:
        HRESULT result_;
        bool mustUninitialize_;
    };

    struct DecodeObjects
    {
        ComPtr<IWICImagingFactory> Factory;
        ComPtr<IWICBitmapDecoder> Decoder;
        ComPtr<IWICBitmapFrameDecode> Frame;
    };

    bool TryMultiply(
        std::uint64_t left,
        std::uint64_t right,
        std::uint64_t* result)
    {
        if (right != 0 && left > std::numeric_limits<std::uint64_t>::max() / right)
        {
            return false;
        }

        *result = left * right;
        return true;
    }

    bool FitsVector(std::uint64_t size)
    {
        return size <= MaxImageBytes
            && size <= static_cast<std::uint64_t>(
                std::numeric_limits<std::size_t>::max());
    }

    std::wstring ToWidePath(const char* path)
    {
        if (path == nullptr || *path == '\0')
        {
            return {};
        }

        const int length = MultiByteToWideChar(CP_ACP, 0, path, -1, nullptr, 0);
        if (length <= 1)
        {
            return {};
        }

        std::wstring widePath(static_cast<std::size_t>(length), L'\0');
        if (MultiByteToWideChar(
            CP_ACP,
            0,
            path,
            -1,
            widePath.data(),
            length) == 0)
        {
            return {};
        }

        widePath.resize(static_cast<std::size_t>(length - 1));
        return widePath;
    }

    IJLERR MapWicError(HRESULT result, bool fileInput)
    {
        if (result == E_OUTOFMEMORY)
        {
            return IJL_MEMORY_ERROR;
        }

        if (result == WINCODEC_ERR_UNKNOWNIMAGEFORMAT
            || result == WINCODEC_ERR_BADIMAGE
            || result == WINCODEC_ERR_COMPONENTNOTFOUND)
        {
            return IJL_ERR_NOT_JPEG;
        }

        if (result == WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT)
        {
            return IJL_UNSUPPORTED_BYTES_PER_PIXEL;
        }

        return fileInput ? IJL_FILE_ERROR : IJL_ERR_DATA;
    }

    IJLERR CreateDecodeObjects(
        const JPEG_CORE_PROPERTIES& properties,
        bool bufferInput,
        DecodeObjects* objects)
    {
        HRESULT result = CoCreateInstance(
            CLSID_WICImagingFactory,
            nullptr,
            CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(objects->Factory.ReleaseAndGetAddressOf()));
        if (FAILED(result))
        {
            return MapWicError(result, !bufferInput);
        }

        if (bufferInput)
        {
            if (properties.JPGBytes == nullptr || properties.JPGSizeBytes <= 0)
            {
                return IJL_ERR_NO_IMAGE;
            }

            ComPtr<IStream> stream;
            stream.Attach(SHCreateMemStream(
                properties.JPGBytes,
                static_cast<UINT>(properties.JPGSizeBytes)));
            if (!stream)
            {
                return IJL_MEMORY_ERROR;
            }

            result = objects->Factory->CreateDecoderFromStream(
                stream.Get(),
                nullptr,
                WICDecodeMetadataCacheOnLoad,
                objects->Decoder.ReleaseAndGetAddressOf());
        }
        else
        {
            const std::wstring path = ToWidePath(properties.JPGFile);
            if (path.empty())
            {
                return IJL_INVALID_FILENAME;
            }

            result = objects->Factory->CreateDecoderFromFilename(
                path.c_str(),
                nullptr,
                GENERIC_READ,
                WICDecodeMetadataCacheOnLoad,
                objects->Decoder.ReleaseAndGetAddressOf());
        }

        if (FAILED(result))
        {
            return MapWicError(result, !bufferInput);
        }

        result = objects->Decoder->GetFrame(
            0,
            objects->Frame.ReleaseAndGetAddressOf());
        return SUCCEEDED(result)
            ? IJL_OK
            : MapWicError(result, !bufferInput);
    }

    int GetChannelCount(
        IWICImagingFactory* factory,
        IWICBitmapFrameDecode* frame)
    {
        WICPixelFormatGUID pixelFormat{};
        if (FAILED(frame->GetPixelFormat(&pixelFormat)))
        {
            return 0;
        }

        ComPtr<IWICComponentInfo> componentInfo;
        if (FAILED(factory->CreateComponentInfo(
            pixelFormat,
            componentInfo.ReleaseAndGetAddressOf())))
        {
            return 0;
        }

        ComPtr<IWICPixelFormatInfo> pixelFormatInfo;
        if (FAILED(componentInfo.As(&pixelFormatInfo)))
        {
            return 0;
        }

        UINT channels = 0;
        if (FAILED(pixelFormatInfo->GetChannelCount(&channels)))
        {
            return 0;
        }

        return static_cast<int>(channels);
    }

    IJLERR PopulateJpegProperties(
        JPEG_CORE_PROPERTIES* properties,
        const DecodeObjects& objects)
    {
        UINT width = 0;
        UINT height = 0;
        HRESULT result = objects.Frame->GetSize(&width, &height);
        if (FAILED(result)
            || width == 0
            || height == 0
            || width > static_cast<UINT>(std::numeric_limits<int>::max())
            || height > static_cast<UINT>(std::numeric_limits<int>::max()))
        {
            return IJL_ERR_NO_IMAGE;
        }

        int channels = GetChannelCount(objects.Factory.Get(), objects.Frame.Get());
        if (channels != 1 && channels != 3 && channels != 4)
        {
            WICPixelFormatGUID pixelFormat{};
            if (SUCCEEDED(objects.Frame->GetPixelFormat(&pixelFormat))
                && IsEqualGUID(pixelFormat, GUID_WICPixelFormat8bppGray))
            {
                channels = 1;
            }
            else
            {
                channels = 3;
            }
        }

        properties->JPGWidth = static_cast<int>(width);
        properties->JPGHeight = static_cast<int>(height);
        properties->JPGChannels = channels;
        properties->JPGThumbWidth = 0;
        properties->JPGThumbHeight = 0;

        if (channels == 1)
        {
            properties->JPGColor = IJL_G;
            properties->JPGSubsampling = IJL_NONE;
        }
        else if (channels == 4)
        {
            properties->JPGColor = IJL_YCBCRA_FPX;
            properties->JPGSubsampling = IJL_4114;
        }
        else
        {
            properties->JPGColor = IJL_YCBCR;
            properties->JPGSubsampling = IJL_411;
        }

        return IJL_OK;
    }

    IJLERR ReadParameters(
        JPEG_CORE_PROPERTIES* properties,
        bool bufferInput)
    {
        DecodeObjects objects;
        IJLERR error = CreateDecodeObjects(*properties, bufferInput, &objects);
        if (error != IJL_OK)
        {
            return error;
        }

        return PopulateJpegProperties(properties, objects);
    }

    IJLERR ResolveDecodeFormat(
        const JPEG_CORE_PROPERTIES& properties,
        WICPixelFormatGUID* pixelFormat,
        int* bytesPerPixel)
    {
        if (properties.DIBChannels == 1 && properties.DIBColor == IJL_G)
        {
            *pixelFormat = GUID_WICPixelFormat8bppGray;
            *bytesPerPixel = 1;
            return IJL_OK;
        }

        if (properties.DIBChannels == 3 && properties.DIBColor == IJL_BGR)
        {
            *pixelFormat = GUID_WICPixelFormat24bppBGR;
            *bytesPerPixel = 3;
            return IJL_OK;
        }

        if (properties.DIBChannels == 3 && properties.DIBColor == IJL_RGB)
        {
            *pixelFormat = GUID_WICPixelFormat24bppRGB;
            *bytesPerPixel = 3;
            return IJL_OK;
        }

        if (properties.DIBChannels == 4 && properties.DIBColor == IJL_RGBA_FPX)
        {
            *pixelFormat = GUID_WICPixelFormat32bppRGBA;
            *bytesPerPixel = 4;
            return IJL_OK;
        }

        return IJL_UNSUPPORTED_BYTES_PER_PIXEL;
    }

    IJLERR DecodeWholeImage(
        JPEG_CORE_PROPERTIES* properties,
        bool bufferInput)
    {
        if (properties->DIBBytes == nullptr)
        {
            return IJL_ERR_NO_IMAGE;
        }

        DecodeObjects objects;
        IJLERR error = CreateDecodeObjects(*properties, bufferInput, &objects);
        if (error != IJL_OK)
        {
            return error;
        }

        error = PopulateJpegProperties(properties, objects);
        if (error != IJL_OK)
        {
            return error;
        }

        if (properties->DIBWidth == 0)
        {
            properties->DIBWidth = properties->JPGWidth;
        }
        if (properties->DIBHeight == 0)
        {
            properties->DIBHeight = properties->JPGHeight;
        }

        const int width = properties->DIBWidth;
        const std::int64_t signedHeight = properties->DIBHeight;
        const std::int64_t absoluteHeight =
            signedHeight < 0 ? -signedHeight : signedHeight;
        if (width <= 0
            || absoluteHeight <= 0
            || width != properties->JPGWidth
            || absoluteHeight != properties->JPGHeight
            || properties->DIBPadBytes < 0)
        {
            return IJL_INVALID_JPEG_PROPERTIES;
        }

        WICPixelFormatGUID targetFormat{};
        int bytesPerPixel = 0;
        error = ResolveDecodeFormat(
            *properties,
            &targetFormat,
            &bytesPerPixel);
        if (error != IJL_OK)
        {
            return error;
        }

        std::uint64_t packedStride64 = 0;
        std::uint64_t packedSize64 = 0;
        const auto height = static_cast<std::uint64_t>(absoluteHeight);
        if (!TryMultiply(
            static_cast<std::uint64_t>(width),
            static_cast<std::uint64_t>(bytesPerPixel),
            &packedStride64)
            || !TryMultiply(packedStride64, height, &packedSize64)
            || packedStride64 > std::numeric_limits<UINT>::max()
            || packedSize64 > std::numeric_limits<UINT>::max()
            || !FitsVector(packedSize64))
        {
            return IJL_MEMORY_ERROR;
        }

        const std::uint64_t destinationStride64 =
            packedStride64 + static_cast<std::uint64_t>(properties->DIBPadBytes);
        std::uint64_t destinationSize64 = 0;
        if (!TryMultiply(destinationStride64, height, &destinationSize64)
            || !FitsVector(destinationSize64))
        {
            return IJL_MEMORY_ERROR;
        }

        WICPixelFormatGUID sourceFormat{};
        HRESULT result = objects.Frame->GetPixelFormat(&sourceFormat);
        if (FAILED(result))
        {
            return MapWicError(result, !bufferInput);
        }

        ComPtr<IWICBitmapSource> bitmapSource;
        if (IsEqualGUID(sourceFormat, targetFormat))
        {
            result = objects.Frame.As(&bitmapSource);
        }
        else
        {
            ComPtr<IWICFormatConverter> converter;
            result = objects.Factory->CreateFormatConverter(
                converter.ReleaseAndGetAddressOf());
            if (SUCCEEDED(result))
            {
                BOOL canConvert = FALSE;
                result = converter->CanConvert(
                    sourceFormat,
                    targetFormat,
                    &canConvert);
                if (SUCCEEDED(result) && !canConvert)
                {
                    return IJL_UNSUPPORTED_BYTES_PER_PIXEL;
                }
            }
            if (SUCCEEDED(result))
            {
                result = converter->Initialize(
                    objects.Frame.Get(),
                    targetFormat,
                    WICBitmapDitherTypeNone,
                    nullptr,
                    0.0,
                    WICBitmapPaletteTypeCustom);
            }
            if (SUCCEEDED(result))
            {
                result = converter.As(&bitmapSource);
            }
        }

        if (FAILED(result))
        {
            return MapWicError(result, !bufferInput);
        }

        std::vector<unsigned char> packedPixels(
            static_cast<std::size_t>(packedSize64));
        result = bitmapSource->CopyPixels(
            nullptr,
            static_cast<UINT>(packedStride64),
            static_cast<UINT>(packedSize64),
            packedPixels.data());
        if (FAILED(result))
        {
            return MapWicError(result, !bufferInput);
        }

        const auto packedStride = static_cast<std::size_t>(packedStride64);
        const auto destinationStride =
            static_cast<std::size_t>(destinationStride64);
        const auto rowCount = static_cast<std::size_t>(height);
        for (std::size_t sourceRow = 0; sourceRow < rowCount; ++sourceRow)
        {
            const std::size_t destinationRow = signedHeight < 0
                ? sourceRow
                : rowCount - sourceRow - 1;
            unsigned char* destination =
                properties->DIBBytes + destinationRow * destinationStride;
            std::memcpy(
                destination,
                packedPixels.data() + sourceRow * packedStride,
                packedStride);
        }

        properties->cconversion_reqd =
            properties->JPGColor == properties->DIBColor ? 0 : 1;
        properties->upsampling_reqd =
            properties->JPGSubsampling == IJL_NONE ? 0 : 1;
        return IJL_OK;
    }

    IJLERR NormalizeSourcePixels(
        JPEG_CORE_PROPERTIES* properties,
        WICPixelFormatGUID* pixelFormat,
        UINT* outputStride,
        std::vector<unsigned char>* pixels)
    {
        if (properties->DIBBytes == nullptr
            || properties->DIBWidth <= 0
            || properties->DIBHeight == 0
            || properties->DIBPadBytes < 0)
        {
            return IJL_INVALID_JPEG_PROPERTIES;
        }

        const std::int64_t signedHeight = properties->DIBHeight;
        const std::int64_t absoluteHeight =
            signedHeight < 0 ? -signedHeight : signedHeight;
        if (absoluteHeight > std::numeric_limits<int>::max())
        {
            return IJL_INVALID_JPEG_PROPERTIES;
        }

        const int width = properties->DIBWidth;
        const int height = static_cast<int>(absoluteHeight);
        int sourceBytesPerPixel = properties->DIBChannels;
        int destinationBytesPerPixel = 0;
        bool swapRedBlue = false;
        bool discardAlpha = false;

        if (sourceBytesPerPixel == 1 && properties->DIBColor == IJL_G)
        {
            *pixelFormat = GUID_WICPixelFormat8bppGray;
            destinationBytesPerPixel = 1;
        }
        else if (sourceBytesPerPixel == 3
            && (properties->DIBColor == IJL_RGB
                || properties->DIBColor == IJL_BGR))
        {
            *pixelFormat = GUID_WICPixelFormat24bppBGR;
            destinationBytesPerPixel = 3;
            swapRedBlue = properties->DIBColor == IJL_RGB;
        }
        else if (sourceBytesPerPixel == 4
            && properties->DIBColor == IJL_RGBA_FPX)
        {
            *pixelFormat = GUID_WICPixelFormat24bppBGR;
            destinationBytesPerPixel = 3;
            swapRedBlue = true;
            discardAlpha = true;
        }
        else
        {
            return IJL_UNSUPPORTED_BYTES_PER_PIXEL;
        }

        std::uint64_t sourceStride64 = 0;
        std::uint64_t destinationStride64 = 0;
        std::uint64_t destinationSize64 = 0;
        if (!TryMultiply(
            static_cast<std::uint64_t>(width),
            static_cast<std::uint64_t>(sourceBytesPerPixel),
            &sourceStride64))
        {
            return IJL_MEMORY_ERROR;
        }
        sourceStride64 += static_cast<std::uint64_t>(properties->DIBPadBytes);

        if (!TryMultiply(
            static_cast<std::uint64_t>(width),
            static_cast<std::uint64_t>(destinationBytesPerPixel),
            &destinationStride64)
            || !TryMultiply(
                destinationStride64,
                static_cast<std::uint64_t>(height),
                &destinationSize64)
            || destinationStride64 > std::numeric_limits<UINT>::max()
            || !FitsVector(destinationSize64))
        {
            return IJL_MEMORY_ERROR;
        }

        pixels->resize(static_cast<std::size_t>(destinationSize64));
        const auto sourceStride = static_cast<std::size_t>(sourceStride64);
        const auto destinationStride =
            static_cast<std::size_t>(destinationStride64);
        for (int destinationRow = 0; destinationRow < height; ++destinationRow)
        {
            // IJL encoding uses a negative DIBHeight to request vertical reversal.
            const int sourceRow = signedHeight < 0
                ? height - destinationRow - 1
                : destinationRow;
            const unsigned char* source =
                properties->DIBBytes
                + static_cast<std::size_t>(sourceRow) * sourceStride;
            unsigned char* destination =
                pixels->data()
                + static_cast<std::size_t>(destinationRow) * destinationStride;

            if (!swapRedBlue && !discardAlpha)
            {
                std::memcpy(destination, source, destinationStride);
                continue;
            }

            for (int x = 0; x < width; ++x)
            {
                const unsigned char* sourcePixel =
                    source + static_cast<std::size_t>(x) * sourceBytesPerPixel;
                unsigned char* destinationPixel =
                    destination
                    + static_cast<std::size_t>(x) * destinationBytesPerPixel;
                destinationPixel[0] = sourcePixel[2];
                destinationPixel[1] = sourcePixel[1];
                destinationPixel[2] = sourcePixel[0];
            }
        }

        *outputStride = static_cast<UINT>(destinationStride64);
        return IJL_OK;
    }

    IJLERR EncodeToMemory(
        JPEG_CORE_PROPERTIES* properties,
        std::vector<unsigned char>* encodedBytes)
    {
        WICPixelFormatGUID pixelFormat{};
        UINT stride = 0;
        std::vector<unsigned char> pixels;
        IJLERR error = NormalizeSourcePixels(
            properties,
            &pixelFormat,
            &stride,
            &pixels);
        if (error != IJL_OK)
        {
            return error;
        }

        const UINT width = static_cast<UINT>(properties->DIBWidth);
        const UINT height = static_cast<UINT>(
            properties->DIBHeight < 0
            ? -static_cast<std::int64_t>(properties->DIBHeight)
            : properties->DIBHeight);

        ComPtr<IWICImagingFactory> factory;
        HRESULT result = CoCreateInstance(
            CLSID_WICImagingFactory,
            nullptr,
            CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(factory.ReleaseAndGetAddressOf()));
        if (FAILED(result))
        {
            return MapWicError(result, false);
        }

        ComPtr<IWICBitmap> bitmap;
        result = factory->CreateBitmapFromMemory(
            width,
            height,
            pixelFormat,
            stride,
            static_cast<UINT>(pixels.size()),
            pixels.data(),
            bitmap.ReleaseAndGetAddressOf());
        if (FAILED(result))
        {
            return MapWicError(result, false);
        }

        ComPtr<IStream> stream;
        result = CreateStreamOnHGlobal(
            nullptr,
            TRUE,
            stream.ReleaseAndGetAddressOf());
        if (FAILED(result))
        {
            return result == E_OUTOFMEMORY ? IJL_MEMORY_ERROR : IJL_INTERNAL_ERROR;
        }

        ComPtr<IWICBitmapEncoder> encoder;
        result = factory->CreateEncoder(
            GUID_ContainerFormatJpeg,
            nullptr,
            encoder.ReleaseAndGetAddressOf());
        if (SUCCEEDED(result))
        {
            result = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
        }

        ComPtr<IWICBitmapFrameEncode> frame;
        ComPtr<IPropertyBag2> options;
        if (SUCCEEDED(result))
        {
            result = encoder->CreateNewFrame(
                frame.ReleaseAndGetAddressOf(),
                options.ReleaseAndGetAddressOf());
        }

        if (SUCCEEDED(result) && options)
        {
            PROPBAG2 option{};
            option.pstrName = const_cast<wchar_t*>(L"ImageQuality");
            VARIANT value{};
            VariantInit(&value);
            value.vt = VT_R4;
            value.fltVal =
                static_cast<float>(std::clamp(properties->jquality, 0, 100))
                / 100.0F;
            result = options->Write(1, &option, &value);
            VariantClear(&value);
        }

        if (SUCCEEDED(result))
        {
            result = frame->Initialize(options.Get());
        }
        if (SUCCEEDED(result))
        {
            result = frame->SetSize(width, height);
        }
        if (SUCCEEDED(result))
        {
            WICPixelFormatGUID encoderFormat = pixelFormat;
            result = frame->SetPixelFormat(&encoderFormat);
        }
        if (SUCCEEDED(result))
        {
            result = frame->WriteSource(bitmap.Get(), nullptr);
        }
        if (SUCCEEDED(result))
        {
            result = frame->Commit();
        }
        if (SUCCEEDED(result))
        {
            result = encoder->Commit();
        }
        if (FAILED(result))
        {
            return MapWicError(result, false);
        }

        STATSTG statistics{};
        result = stream->Stat(&statistics, STATFLAG_NONAME);
        if (FAILED(result)
            || statistics.cbSize.HighPart != 0
            || statistics.cbSize.LowPart
            > static_cast<ULONG>(std::numeric_limits<int>::max()))
        {
            return IJL_INTERNAL_ERROR;
        }

        encodedBytes->resize(statistics.cbSize.LowPart);
        LARGE_INTEGER beginning{};
        result = stream->Seek(beginning, STREAM_SEEK_SET, nullptr);
        if (FAILED(result))
        {
            return IJL_INTERNAL_ERROR;
        }

        ULONG bytesRead = 0;
        result = stream->Read(
            encodedBytes->data(),
            static_cast<ULONG>(encodedBytes->size()),
            &bytesRead);
        if (FAILED(result) || bytesRead != encodedBytes->size())
        {
            return IJL_INTERNAL_ERROR;
        }

        properties->JPGWidth = static_cast<int>(width);
        properties->JPGHeight = static_cast<int>(height);
        properties->JPGSizeBytes = static_cast<int>(encodedBytes->size());
        return IJL_OK;
    }

    IJLERR WriteFileBytes(
        const char* path,
        const std::vector<unsigned char>& bytes)
    {
        const std::wstring widePath = ToWidePath(path);
        if (widePath.empty())
        {
            return IJL_INVALID_FILENAME;
        }

        HANDLE file = CreateFileW(
            widePath.c_str(),
            GENERIC_WRITE,
            0,
            nullptr,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return IJL_FILE_ERROR;
        }

        DWORD bytesWritten = 0;
        const BOOL wrote = WriteFile(
            file,
            bytes.data(),
            static_cast<DWORD>(bytes.size()),
            &bytesWritten,
            nullptr);
        const BOOL closed = CloseHandle(file);
        if (!wrote || bytesWritten != bytes.size())
        {
            return IJL_FILE_ERROR;
        }
        if (!closed)
        {
            return IJL_ERR_FILECLOSE;
        }

        return IJL_OK;
    }
}

static_assert(offsetof(JPEG_CORE_PROPERTIES, DIBBytes) == 4);
static_assert(offsetof(JPEG_CORE_PROPERTIES, JPGFile) == 32);
static_assert(offsetof(JPEG_CORE_PROPERTIES, JPGBytes) == 36);
static_assert(offsetof(JPEG_CORE_PROPERTIES, JPGSizeBytes) == 40);
static_assert(offsetof(JPEG_CORE_PROPERTIES, JPGWidth) == 44);
static_assert(offsetof(JPEG_CORE_PROPERTIES, jquality) == 80);
static_assert(sizeof(JPEG_CORE_PROPERTIES) == 20072);

const IJLibVersion* __stdcall ijlGetLibVersion()
{
    return &Version;
}

IJLERR __stdcall ijlInit(JPEG_CORE_PROPERTIES* properties)
{
    if (properties == nullptr)
    {
        return IJL_INVALID_JPEG_PROPERTIES;
    }

    std::memset(properties, 0, 84);
    properties->DIBChannels = 3;
    properties->DIBColor = IJL_BGR;
    properties->JPGChannels = 3;
    properties->JPGColor = IJL_YCBCR;
    properties->JPGSubsampling = IJL_411;
    properties->cconversion_reqd = 1;
    properties->upsampling_reqd = 1;
    properties->jquality = 75;
    return IJL_OK;
}

IJLERR __stdcall ijlFree(JPEG_CORE_PROPERTIES* properties)
{
    return properties == nullptr ? IJL_INVALID_JPEG_PROPERTIES : IJL_OK;
}

IJLERR __stdcall ijlRead(
    JPEG_CORE_PROPERTIES* properties,
    IJLIOTYPE ioType)
{
    if (properties == nullptr)
    {
        return IJL_INVALID_JPEG_PROPERTIES;
    }

    try
    {
        ComApartment apartment;
        if (!apartment.IsAvailable())
        {
            return IJL_INTERNAL_ERROR;
        }

        switch (ioType)
        {
        case IJL_JFILE_READPARAMS:
            return ReadParameters(properties, false);
        case IJL_JBUFF_READPARAMS:
            return ReadParameters(properties, true);
        case IJL_JFILE_READWHOLEIMAGE:
            return DecodeWholeImage(properties, false);
        case IJL_JBUFF_READWHOLEIMAGE:
            return DecodeWholeImage(properties, true);
        default:
            return IJL_PROG_NOT_SUPPORTED;
        }
    }
    catch (const std::bad_alloc&)
    {
        return IJL_MEMORY_ERROR;
    }
    catch (...)
    {
        return IJL_EXCEPTION_DETECTED;
    }
}

IJLERR __stdcall ijlWrite(
    JPEG_CORE_PROPERTIES* properties,
    IJLIOTYPE ioType)
{
    if (properties == nullptr)
    {
        return IJL_INVALID_JPEG_PROPERTIES;
    }

    try
    {
        if (ioType != IJL_JFILE_WRITEWHOLEIMAGE
            && ioType != IJL_JBUFF_WRITEWHOLEIMAGE)
        {
            return IJL_PROG_NOT_SUPPORTED;
        }

        ComApartment apartment;
        if (!apartment.IsAvailable())
        {
            return IJL_INTERNAL_ERROR;
        }

        const int capacity = properties->JPGSizeBytes;
        std::vector<unsigned char> encodedBytes;
        IJLERR error = EncodeToMemory(properties, &encodedBytes);
        if (error != IJL_OK)
        {
            return error;
        }

        if (ioType == IJL_JFILE_WRITEWHOLEIMAGE)
        {
            return WriteFileBytes(properties->JPGFile, encodedBytes);
        }

        if (properties->JPGBytes == nullptr)
        {
            return IJL_ERR_NO_IMAGE;
        }
        if (capacity < 0
            || static_cast<std::size_t>(capacity) < encodedBytes.size())
        {
            return IJL_BUFFER_TOO_SMALL;
        }

        std::memcpy(
            properties->JPGBytes,
            encodedBytes.data(),
            encodedBytes.size());
        return IJL_OK;
    }
    catch (const std::bad_alloc&)
    {
        return IJL_MEMORY_ERROR;
    }
    catch (...)
    {
        return IJL_EXCEPTION_DETECTED;
    }
}

const char* __stdcall ijlErrorStr(IJLERR code)
{
    switch (code)
    {
    case IJL_OK: return "Success";
    case IJL_INTERRUPT_OK: return "Interrupt Success";
    case IJL_ROI_OK: return "ROI Success";
    case IJL_EXCEPTION_DETECTED: return "Exception detected";
    case IJL_INVALID_ENCODER: return "Invalid Encoder";
    case IJL_UNSUPPORTED_SUBSAMPLING: return "Unsupported subsampling";
    case IJL_UNSUPPORTED_BYTES_PER_PIXEL:
        return "Unsupported bytes per pixel";
    case IJL_MEMORY_ERROR: return "Memory error";
    case IJL_BAD_HUFFMAN_TABLE: return "Bad Huffman table";
    case IJL_BAD_QUANT_TABLE: return "Bad Quantization table";
    case IJL_INVALID_JPEG_PROPERTIES: return "Invalid JPEG_PROPERTIES";
    case IJL_ERR_FILECLOSE: return "Error close file";
    case IJL_INVALID_FILENAME: return "Invalid file name";
    case IJL_ERROR_EOF: return "Error EOF";
    case IJL_PROG_NOT_SUPPORTED: return "Not supported";
    case IJL_ERR_NOT_JPEG: return "Not JPEG";
    case IJL_ERR_COMP: return "Error COMP";
    case IJL_ERR_SOF: return "Error SOF";
    case IJL_ERR_DNL: return "Error DNL";
    case IJL_ERR_NO_HUF: return "No Huffman table";
    case IJL_ERR_NO_QUAN: return "No Quantization table";
    case IJL_ERR_NO_FRAME: return "No frame";
    case IJL_ERR_MULT_FRAME: return "Multiply frame";
    case IJL_ERR_DATA: return "Data error";
    case IJL_ERR_NO_IMAGE: return "No image";
    case IJL_FILE_ERROR: return "File error";
    case IJL_INTERNAL_ERROR: return "Internal error";
    case IJL_BAD_RST_MARKER: return "Bad RST marker";
    case IJL_THUMBNAIL_DIB_TOO_SMALL: return "Thumbnail too small";
    case IJL_THUMBNAIL_DIB_WRONG_COLOR:
        return "Thumbnail has wrong color";
    case IJL_BUFFER_TOO_SMALL: return "Output buffer too small";
    case IJL_UNSUPPORTED_FRAME: return "Unsupported frame was found";
    case IJL_ERR_COM_BUFFER:
        return "Error access to jpeg_comment buffer";
    case IJL_RESERVED: return "Reserved";
    default: return "Unknown error code";
    }
}
