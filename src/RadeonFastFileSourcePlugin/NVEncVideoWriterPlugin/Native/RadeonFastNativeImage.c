#include <vips/vips.h>

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <locale.h>
#include <windows.h>

#if defined(_WIN32)
#define RF_EXPORT __declspec(dllexport)
#else
#define RF_EXPORT
#endif

typedef struct RfImage
{
    int width;
    int height;
    int stride;
    uint64_t bytes;
    void* data;
} RfImage;

static __declspec(thread) char g_image_last_error[512];
static volatile LONG g_vips_state = 0;

static void rf_image_set_error(const char* message)
{
    if (message == NULL)
        message = "unknown error";

    strncpy(g_image_last_error, message, sizeof(g_image_last_error) - 1);
    g_image_last_error[sizeof(g_image_last_error) - 1] = '\0';
}

static char* rf_wide_to_utf8(const wchar_t* path)
{
    int len = WideCharToMultiByte(CP_UTF8, 0, path, -1, NULL, 0, NULL, NULL);
    if (len <= 0)
        return NULL;

    char* utf8 = (char*)malloc((size_t)len);
    if (utf8 == NULL)
        return NULL;

    if (WideCharToMultiByte(CP_UTF8, 0, path, -1, utf8, len, NULL, NULL) <= 0)
    {
        free(utf8);
        return NULL;
    }

    return utf8;
}

RF_EXPORT const char* rf_image_last_error(void)
{
    return g_image_last_error;
}

static int rf_image_ensure_vips(void)
{
    LONG state = InterlockedCompareExchange(&g_vips_state, 1, 0);
    if (state == 0)
    {
        /*
         * libvips は初期化時に gettext/bindtextdomain を経由して
         * setlocale(LC_ALL, "") を呼び出すことがある。
         * これによりプロセス全体の C ランタイムロケールが変更され、
         * ホスト(YMM4)の Rhubarb/PocketSphinx がパス変換に
         * "Unicode文字のマッピングがターゲットのマルチバイトコードページにありません"
         * エラーでクラッシュする。
         * VIPS_INIT の前後でロケールを保存・復元してこの副作用を防ぐ。
         */
        char* saved_locale = NULL;
        const char* current_locale = setlocale(LC_ALL, NULL);
        if (current_locale != NULL)
            saved_locale = _strdup(current_locale);

        int vips_ok = (VIPS_INIT("RadeonFastFileSourcePlugin") == 0);

        if (saved_locale != NULL)
        {
            setlocale(LC_ALL, saved_locale);
            free(saved_locale);
        }

        if (!vips_ok)
        {
            rf_image_set_error(vips_error_buffer());
            vips_error_clear();
            InterlockedExchange(&g_vips_state, -1);
            return -1;
        }

        SYSTEM_INFO info;
        GetSystemInfo(&info);
        int concurrency = (int)info.dwNumberOfProcessors;
        if (concurrency < 1)
            concurrency = 1;
        if (concurrency > 12)
            concurrency = 12;
        vips_concurrency_set(concurrency);

        InterlockedExchange(&g_vips_state, 2);
        return 0;
    }

    while (state == 1)
    {
        Sleep(1);
        state = InterlockedCompareExchange(&g_vips_state, 0, 0);
    }

    if (state != 2)
    {
        rf_image_set_error("libvips init failed");
        return -1;
    }

    return 0;
}

RF_EXPORT int rf_image_decode_bgra(const wchar_t* path, RfImage* outImage)
{
    if (path == NULL || outImage == NULL)
    {
        rf_image_set_error("invalid argument");
        return -1;
    }

    memset(outImage, 0, sizeof(*outImage));

    if (rf_image_ensure_vips())
        return -2;

    char* utf8 = rf_wide_to_utf8(path);
    if (utf8 == NULL)
    {
        rf_image_set_error("path conversion failed");
        return -3;
    }

    VipsImage* image = vips_image_new_from_file(utf8, "access", VIPS_ACCESS_SEQUENTIAL, NULL);
    free(utf8);
    if (image == NULL)
    {
        rf_image_set_error(vips_error_buffer());
        vips_error_clear();
        return -4;
    }

    VipsImage* srgb = NULL;
    if (vips_colourspace(image, &srgb, VIPS_INTERPRETATION_sRGB, NULL))
    {
        srgb = image;
        g_object_ref(srgb);
    }

    VipsImage* ucharImage = NULL;
    if (vips_cast(srgb, &ucharImage, VIPS_FORMAT_UCHAR, NULL))
    {
        g_object_unref(srgb);
        g_object_unref(image);
        rf_image_set_error(vips_error_buffer());
        vips_error_clear();
        return -5;
    }

    VipsImage* rgba = NULL;
    if (ucharImage->Bands == 4)
    {
        rgba = ucharImage;
        g_object_ref(rgba);
    }
    else if (ucharImage->Bands == 3)
    {
        if (vips_addalpha(ucharImage, &rgba, NULL))
        {
            g_object_unref(ucharImage);
            g_object_unref(srgb);
            g_object_unref(image);
            rf_image_set_error(vips_error_buffer());
            vips_error_clear();
            return -6;
        }
    }
    else
    {
        VipsImage* rgb = NULL;
        if (vips_colourspace(ucharImage, &rgb, VIPS_INTERPRETATION_sRGB, NULL) || vips_addalpha(rgb, &rgba, NULL))
        {
            if (rgb != NULL)
                g_object_unref(rgb);
            g_object_unref(ucharImage);
            g_object_unref(srgb);
            g_object_unref(image);
            rf_image_set_error(vips_error_buffer());
            vips_error_clear();
            return -7;
        }
        g_object_unref(rgb);
    }

    size_t rgbaBytes = 0;
    void* rgbaMemory = vips_image_write_to_memory(rgba, &rgbaBytes);
    if (rgbaMemory == NULL)
    {
        g_object_unref(rgba);
        g_object_unref(ucharImage);
        g_object_unref(srgb);
        g_object_unref(image);
        rf_image_set_error(vips_error_buffer());
        vips_error_clear();
        return -8;
    }

    int width = rgba->Xsize;
    int height = rgba->Ysize;
    int stride = width * 4;
    uint64_t bgraBytes = (uint64_t)stride * (uint64_t)height;
    uint8_t* bgra = (uint8_t*)malloc((size_t)bgraBytes);
    if (bgra == NULL)
    {
        g_free(rgbaMemory);
        g_object_unref(rgba);
        g_object_unref(ucharImage);
        g_object_unref(srgb);
        g_object_unref(image);
        rf_image_set_error("out of memory");
        return -9;
    }

    const uint8_t* src = (const uint8_t*)rgbaMemory;
    for (uint64_t i = 0; i < (uint64_t)width * (uint64_t)height; i++)
    {
        uint8_t r = src[i * 4 + 0];
        uint8_t g = src[i * 4 + 1];
        uint8_t b = src[i * 4 + 2];
        uint8_t a = src[i * 4 + 3];

        bgra[i * 4 + 0] = (uint8_t)(((uint16_t)b * a + 127) / 255);
        bgra[i * 4 + 1] = (uint8_t)(((uint16_t)g * a + 127) / 255);
        bgra[i * 4 + 2] = (uint8_t)(((uint16_t)r * a + 127) / 255);
        bgra[i * 4 + 3] = a;
    }

    g_free(rgbaMemory);
    g_object_unref(rgba);
    g_object_unref(ucharImage);
    g_object_unref(srgb);
    g_object_unref(image);

    outImage->width = width;
    outImage->height = height;
    outImage->stride = stride;
    outImage->bytes = bgraBytes;
    outImage->data = bgra;
    return 0;
}

RF_EXPORT void rf_image_free(void* data)
{
    free(data);
}
