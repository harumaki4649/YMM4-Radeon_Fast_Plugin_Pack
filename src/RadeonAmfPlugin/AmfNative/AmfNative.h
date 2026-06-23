#pragma once

#include <stdint.h>

struct ID3D11Device;
struct ID3D11Texture2D;

extern "C" {
    __declspec(dllexport) void* AmfCreate(
        ID3D11Device* device,
        ID3D11Texture2D* firstTexture,
        int width,
        int height,
        int fps,
        int bitrateKbps,
        int codec,
        int quality,
        int rateControlMode,
        int maxBitrateKbps,
        int queueDepth,
        int enablePreAnalysis,
        int enableDebugLog,
        const wchar_t* outputPath);

    __declspec(dllexport) int AmfEncode(void* handle, ID3D11Texture2D* texture);

    __declspec(dllexport) ID3D11Texture2D* AmfAcquireTexture(void* handle);

    __declspec(dllexport) void AmfReleaseTexture(void* handle, ID3D11Texture2D* texture);

    __declspec(dllexport) int AmfWriteAudio(void* handle, const float* samples, int sampleCount, int sampleRate, int channels);

    __declspec(dllexport) int AmfFinalize(void* handle);

    __declspec(dllexport) void AmfDestroy(void* handle);

    __declspec(dllexport) const wchar_t* AmfGetLastError(void* handle);
}
