#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <locale.h>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#define RF_EXPORT __declspec(dllexport)
#else
#define RF_EXPORT
#endif

typedef struct RfVideoProbeResult
{
    int32_t status;
    int32_t streamIndex;
    int32_t hasHardwareDevice;
    int32_t preferredBackend;
    char codecName[64];
    char hardwareName[64];
    char message[256];
} RfVideoProbeResult;

typedef struct AVFormatContext AVFormatContext;
typedef struct AVInputFormat AVInputFormat;
typedef struct AVDictionary AVDictionary;
typedef struct AVCodec AVCodec;

typedef struct AVCodecNameView
{
    const char* name;
} AVCodecNameView;

typedef struct AVCodecHWConfig
{
    int pix_fmt;
    int methods;
    int device_type;
} AVCodecHWConfig;

enum
{
    RF_VIDEO_BACKEND_NONE = 0,
    RF_VIDEO_BACKEND_AMD_HARDWARE = 1,
    RF_VIDEO_BACKEND_FFMPEG = 2
};

enum
{
    AVMEDIA_TYPE_VIDEO = 0
};

typedef int (*p_avformat_open_input)(AVFormatContext**, const char*, const AVInputFormat*, AVDictionary**);
typedef int (*p_avformat_find_stream_info)(AVFormatContext*, AVDictionary**);
typedef void (*p_avformat_close_input)(AVFormatContext**);
typedef int (*p_av_find_best_stream)(AVFormatContext*, int, int, int, const AVCodec**, int);
typedef const AVCodecHWConfig* (*p_avcodec_get_hw_config)(const AVCodec*, int);
typedef const char* (*p_av_hwdevice_get_type_name)(int);

static __declspec(thread) char g_last_error[256];

static void rf_copy(char* destination, size_t destinationSize, const char* source)
{
    if (destination == NULL || destinationSize == 0)
        return;

    if (source == NULL)
        source = "";

    strncpy(destination, source, destinationSize - 1);
    destination[destinationSize - 1] = '\0';
}

static void rf_set_error(const char* message)
{
    rf_copy(g_last_error, sizeof(g_last_error), message);
}

static void rf_result_message(RfVideoProbeResult* result, const char* message)
{
    if (result != NULL)
        rf_copy(result->message, sizeof(result->message), message);
    rf_set_error(message);
}

RF_EXPORT const char* rf_video_last_error(void)
{
    return g_last_error;
}

#if defined(_WIN32)
static int rf_wide_to_utf8(const wchar_t* path, char* buffer, int bufferSize)
{
    if (path == NULL || buffer == NULL || bufferSize <= 0)
        return 0;

    int written = WideCharToMultiByte(CP_UTF8, 0, path, -1, buffer, bufferSize, NULL, NULL);
    if (written <= 0 || written >= bufferSize)
    {
        buffer[0] = '\0';
        return 0;
    }

    return 1;
}

static HMODULE rf_load_library_from_dir(const wchar_t* directory, const wchar_t* name)
{
    wchar_t fullPath[MAX_PATH * 2];
    if (directory != NULL && directory[0] != L'\0')
    {
        _snwprintf_s(fullPath, sizeof(fullPath) / sizeof(fullPath[0]), _TRUNCATE, L"%ls\\%ls", directory, name);
        HMODULE module = LoadLibraryExW(fullPath, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module != NULL)
            return module;
    }

    return LoadLibraryW(name);
}

static FARPROC rf_get_proc(HMODULE module, const char* name)
{
    return module == NULL ? NULL : GetProcAddress(module, name);
}
#endif

RF_EXPORT int rf_video_probe(const wchar_t* path, const wchar_t* ffmpegDirectory, int preferHardware, RfVideoProbeResult* result)
{
    if (result == NULL)
    {
        rf_set_error("invalid result pointer");
        return -1;
    }

    memset(result, 0, sizeof(*result));
    result->status = -1;
    result->streamIndex = -1;
    result->preferredBackend = RF_VIDEO_BACKEND_NONE;

#if !defined(_WIN32)
    (void)path;
    (void)ffmpegDirectory;
    (void)preferHardware;
    rf_result_message(result, "Windows-only native video probe");
    return -2;
#else
    if (path == NULL || path[0] == L'\0')
    {
        rf_result_message(result, "invalid path");
        return -3;
    }

    char utf8Path[32768];
    if (!rf_wide_to_utf8(path, utf8Path, (int)sizeof(utf8Path)))
    {
        rf_result_message(result, "path utf8 conversion failed");
        return -4;
    }

    /*
     * FFmpeg DLL の読み込みや初期化が C ランタイムのロケールを
     * 副作用的に変更しないよう、前後で保存・復元する。
     * ホストプロセス(YMM4)の Rhubarb/PocketSphinx がロケール変更に
     * よるパス変換エラーでクラッシュするのを防ぐ。
     */
    char* saved_locale = NULL;
    const char* current_locale = setlocale(LC_ALL, NULL);
    if (current_locale != NULL)
        saved_locale = _strdup(current_locale);

    HMODULE avutil = rf_load_library_from_dir(ffmpegDirectory, L"avutil-60.dll");
    HMODULE avcodec = rf_load_library_from_dir(ffmpegDirectory, L"avcodec-62.dll");
    HMODULE avformat = rf_load_library_from_dir(ffmpegDirectory, L"avformat-62.dll");
    if (avutil == NULL || avcodec == NULL || avformat == NULL)
    {
        if (saved_locale != NULL)
        {
            setlocale(LC_ALL, saved_locale);
            free(saved_locale);
        }
        rf_result_message(result, "ffmpeg dlls not found");
        return -5;
    }

    /* FFmpeg DLL の読み込み完了後、ロケールを復元 */
    if (saved_locale != NULL)
    {
        setlocale(LC_ALL, saved_locale);
        free(saved_locale);
    }

    p_avformat_open_input avformat_open_input_fn = (p_avformat_open_input)rf_get_proc(avformat, "avformat_open_input");
    p_avformat_find_stream_info avformat_find_stream_info_fn = (p_avformat_find_stream_info)rf_get_proc(avformat, "avformat_find_stream_info");
    p_avformat_close_input avformat_close_input_fn = (p_avformat_close_input)rf_get_proc(avformat, "avformat_close_input");
    p_av_find_best_stream av_find_best_stream_fn = (p_av_find_best_stream)rf_get_proc(avformat, "av_find_best_stream");
    p_avcodec_get_hw_config avcodec_get_hw_config_fn = (p_avcodec_get_hw_config)rf_get_proc(avcodec, "avcodec_get_hw_config");
    p_av_hwdevice_get_type_name av_hwdevice_get_type_name_fn = (p_av_hwdevice_get_type_name)rf_get_proc(avutil, "av_hwdevice_get_type_name");
    if (avformat_open_input_fn == NULL ||
        avformat_find_stream_info_fn == NULL ||
        avformat_close_input_fn == NULL ||
        av_find_best_stream_fn == NULL ||
        avcodec_get_hw_config_fn == NULL ||
        av_hwdevice_get_type_name_fn == NULL)
    {
        rf_result_message(result, "ffmpeg entry point missing");
        return -6;
    }

    AVFormatContext* format = NULL;
    int openResult = avformat_open_input_fn(&format, utf8Path, NULL, NULL);
    if (openResult < 0 || format == NULL)
    {
        rf_result_message(result, "avformat_open_input failed");
        return openResult == 0 ? -7 : openResult;
    }

    int streamInfoResult = avformat_find_stream_info_fn(format, NULL);
    if (streamInfoResult < 0)
    {
        avformat_close_input_fn(&format);
        rf_result_message(result, "avformat_find_stream_info failed");
        return streamInfoResult;
    }

    const AVCodec* decoder = NULL;
    int streamIndex = av_find_best_stream_fn(format, AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
    if (streamIndex < 0 || decoder == NULL)
    {
        avformat_close_input_fn(&format);
        rf_result_message(result, "video stream not found");
        return streamIndex == 0 ? -8 : streamIndex;
    }

    result->streamIndex = streamIndex;
    rf_copy(result->codecName, sizeof(result->codecName), ((const AVCodecNameView*)decoder)->name);
    for (int i = 0; i < 64; i++)
    {
        const AVCodecHWConfig* config = avcodec_get_hw_config_fn(decoder, i);
        if (config == NULL)
            break;

        const char* hwName = av_hwdevice_get_type_name_fn(config->device_type);
        if (hwName == NULL)
            continue;

        if (strcmp(hwName, "d3d11va") == 0 || strcmp(hwName, "dxva2") == 0)
        {
            result->hasHardwareDevice = 1;
            rf_copy(result->hardwareName, sizeof(result->hardwareName), hwName);
            break;
        }
    }

    result->preferredBackend = (preferHardware && result->hasHardwareDevice)
        ? RF_VIDEO_BACKEND_AMD_HARDWARE
        : RF_VIDEO_BACKEND_FFMPEG;
    result->status = 0;
    rf_result_message(result, result->hasHardwareDevice ? "video probe ok with hardware device" : "video probe ok without hardware device");

    avformat_close_input_fn(&format);
    return 0;
#endif
}
