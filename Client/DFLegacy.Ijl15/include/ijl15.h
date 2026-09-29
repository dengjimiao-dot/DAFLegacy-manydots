#pragma once

#if !defined(_WIN32)
#error DFLegacy.Ijl15 is a Windows compatibility module.
#endif

#if !defined(_M_IX86)
#error DFLegacy.Ijl15 must be built for x86.
#endif

enum IJLIOTYPE
{
    IJL_SETUP = -1,
    IJL_JFILE_READPARAMS = 0,
    IJL_JBUFF_READPARAMS = 1,
    IJL_JFILE_READWHOLEIMAGE = 2,
    IJL_JBUFF_READWHOLEIMAGE = 3,
    IJL_JFILE_WRITEWHOLEIMAGE = 8,
    IJL_JBUFF_WRITEWHOLEIMAGE = 9
};

enum IJL_COLOR
{
    IJL_RGB = 1,
    IJL_BGR = 2,
    IJL_YCBCR = 3,
    IJL_G = 4,
    IJL_RGBA_FPX = 5,
    IJL_YCBCRA_FPX = 6,
    IJL_OTHER = 255
};

enum IJL_JPGSUBSAMPLING
{
    IJL_NONE = 0,
    IJL_411 = 1,
    IJL_422 = 2,
    IJL_4114 = 3,
    IJL_4224 = 4,
    IJL_SSOTHER = 255
};

enum IJLERR
{
    IJL_OK = 0,
    IJL_INTERRUPT_OK = 1,
    IJL_ROI_OK = 2,
    IJL_EXCEPTION_DETECTED = -1,
    IJL_INVALID_ENCODER = -2,
    IJL_UNSUPPORTED_SUBSAMPLING = -3,
    IJL_UNSUPPORTED_BYTES_PER_PIXEL = -4,
    IJL_MEMORY_ERROR = -5,
    IJL_BAD_HUFFMAN_TABLE = -6,
    IJL_BAD_QUANT_TABLE = -7,
    IJL_INVALID_JPEG_PROPERTIES = -8,
    IJL_ERR_FILECLOSE = -9,
    IJL_INVALID_FILENAME = -10,
    IJL_ERROR_EOF = -11,
    IJL_PROG_NOT_SUPPORTED = -12,
    IJL_ERR_NOT_JPEG = -13,
    IJL_ERR_COMP = -14,
    IJL_ERR_SOF = -15,
    IJL_ERR_DNL = -16,
    IJL_ERR_NO_HUF = -17,
    IJL_ERR_NO_QUAN = -18,
    IJL_ERR_NO_FRAME = -19,
    IJL_ERR_MULT_FRAME = -20,
    IJL_ERR_DATA = -21,
    IJL_ERR_NO_IMAGE = -22,
    IJL_FILE_ERROR = -23,
    IJL_INTERNAL_ERROR = -24,
    IJL_BAD_RST_MARKER = -25,
    IJL_THUMBNAIL_DIB_TOO_SMALL = -26,
    IJL_THUMBNAIL_DIB_WRONG_COLOR = -27,
    IJL_BUFFER_TOO_SMALL = -28,
    IJL_UNSUPPORTED_FRAME = -29,
    IJL_ERR_COM_BUFFER = -30,
    IJL_RESERVED = -99
};

struct IJLibVersion
{
    int major;
    int minor;
    int build;
    const char* Name;
    const char* Version;
    const char* InternalVersion;
    const char* BuildDate;
    const char* CallConv;
};

#pragma pack(push, 4)
struct JPEG_CORE_PROPERTIES
{
    int UseJPEGPROPERTIES;
    unsigned char* DIBBytes;
    int DIBWidth;
    int DIBHeight;
    int DIBPadBytes;
    int DIBChannels;
    IJL_COLOR DIBColor;
    int DIBSubsampling;
    const char* JPGFile;
    unsigned char* JPGBytes;
    int JPGSizeBytes;
    int JPGWidth;
    int JPGHeight;
    int JPGChannels;
    IJL_COLOR JPGColor;
    IJL_JPGSUBSAMPLING JPGSubsampling;
    int JPGThumbWidth;
    int JPGThumbHeight;
    int cconversion_reqd;
    int upsampling_reqd;
    int jquality;
    unsigned char Reserved[20072 - 84];
};
#pragma pack(pop)

#if defined(IJL15_EXPORTS)
#define IJL15_API extern "C"
#else
#define IJL15_API extern "C" __declspec(dllimport)
#endif

IJL15_API const IJLibVersion* __stdcall ijlGetLibVersion();
IJL15_API IJLERR __stdcall ijlInit(JPEG_CORE_PROPERTIES* properties);
IJL15_API IJLERR __stdcall ijlFree(JPEG_CORE_PROPERTIES* properties);
IJL15_API IJLERR __stdcall ijlRead(
    JPEG_CORE_PROPERTIES* properties,
    IJLIOTYPE ioType);
IJL15_API IJLERR __stdcall ijlWrite(
    JPEG_CORE_PROPERTIES* properties,
    IJLIOTYPE ioType);
IJL15_API const char* __stdcall ijlErrorStr(IJLERR code);
