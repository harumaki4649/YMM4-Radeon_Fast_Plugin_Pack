#define MA_NO_ENCODING
#define MA_NO_DEVICE_IO
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define RF_EXPORT __declspec(dllexport)
#else
#define RF_EXPORT
#endif

typedef struct RfAudioDecoder
{
    ma_decoder decoder;
    ma_uint64 lengthFrames;
    ma_uint32 sampleRate;
} RfAudioDecoder;

static __declspec(thread) char g_last_error[256];

static void rf_set_error(const char* message)
{
    if (message == NULL)
        message = "unknown error";

    strncpy(g_last_error, message, sizeof(g_last_error) - 1);
    g_last_error[sizeof(g_last_error) - 1] = '\0';
}

RF_EXPORT const char* rf_audio_last_error(void)
{
    return g_last_error;
}

RF_EXPORT int rf_audio_open(const wchar_t* path, void** outHandle, int* outHz, uint64_t* outLengthFrames)
{
    if (path == NULL || outHandle == NULL || outHz == NULL || outLengthFrames == NULL)
    {
        rf_set_error("invalid argument");
        return -1;
    }

    *outHandle = NULL;
    *outHz = 0;
    *outLengthFrames = 0;

    RfAudioDecoder* handle = (RfAudioDecoder*)calloc(1, sizeof(RfAudioDecoder));
    if (handle == NULL)
    {
        rf_set_error("out of memory");
        return -2;
    }

    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, 1, 0);
    ma_result result = ma_decoder_init_file_w(path, &config, &handle->decoder);
    if (result != MA_SUCCESS)
    {
        free(handle);
        rf_set_error("ma_decoder_init_file_w failed");
        return (int)result;
    }

    handle->sampleRate = handle->decoder.outputSampleRate;
    if (handle->sampleRate == 0)
    {
        ma_decoder_uninit(&handle->decoder);
        free(handle);
        rf_set_error("invalid sample rate");
        return -3;
    }

    result = ma_decoder_get_length_in_pcm_frames(&handle->decoder, &handle->lengthFrames);
    if (result != MA_SUCCESS)
        handle->lengthFrames = 0;

    *outHandle = handle;
    *outHz = (int)handle->sampleRate;
    *outLengthFrames = (uint64_t)handle->lengthFrames;
    return 0;
}

RF_EXPORT int rf_audio_read(void* rawHandle, float* destination, int samples, int* outSamplesRead)
{
    if (rawHandle == NULL || destination == NULL || samples < 0 || outSamplesRead == NULL)
    {
        rf_set_error("invalid argument");
        return -1;
    }

    *outSamplesRead = 0;
    if (samples == 0)
        return 0;

    RfAudioDecoder* handle = (RfAudioDecoder*)rawHandle;
    ma_uint64 framesRead = 0;
    ma_result result = ma_decoder_read_pcm_frames(&handle->decoder, destination, (ma_uint64)samples, &framesRead);
    if (result != MA_SUCCESS && result != MA_AT_END)
    {
        rf_set_error("ma_decoder_read_pcm_frames failed");
        return (int)result;
    }

    *outSamplesRead = (int)framesRead;
    return 0;
}

RF_EXPORT int rf_audio_seek(void* rawHandle, uint64_t frame)
{
    if (rawHandle == NULL)
    {
        rf_set_error("invalid argument");
        return -1;
    }

    RfAudioDecoder* handle = (RfAudioDecoder*)rawHandle;
    ma_result result = ma_decoder_seek_to_pcm_frame(&handle->decoder, (ma_uint64)frame);
    if (result != MA_SUCCESS)
    {
        rf_set_error("ma_decoder_seek_to_pcm_frame failed");
        return (int)result;
    }

    return 0;
}

RF_EXPORT void rf_audio_close(void* rawHandle)
{
    if (rawHandle == NULL)
        return;

    RfAudioDecoder* handle = (RfAudioDecoder*)rawHandle;
    ma_decoder_uninit(&handle->decoder);
    free(handle);
}
