#pragma once

#include <stdint.h>

#ifdef TRACKNATIVE_EXPORTS
#define CAPTURE_API __declspec(dllexport)
#else
#define CAPTURE_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct CaptureConfig {
    int64_t main_hwnd;
    int32_t screen_left;
    int32_t screen_top;
    int32_t width;
    int32_t height;
    int32_t inset_left;
    int32_t inset_top;
    int32_t inset_right;
    int32_t inset_bottom;
    int32_t smooth_radius;
} CaptureConfig;

typedef struct CaptureFrameInfo {
    uint64_t sequence;
    int32_t frame_width;
    int32_t frame_height;
    int32_t inset_left;
    int32_t inset_top;
    int32_t inset_right;
    int32_t inset_bottom;
    int32_t plot_width;
    uint8_t accent_b;
    uint8_t accent_g;
    uint8_t accent_r;
    uint8_t reserved;
    int32_t has_update_timing;
    int64_t last_update_ticks;
    int64_t update_period_ticks;
    int64_t next_update_ticks;
} CaptureFrameInfo;

/*
 * Starts a free-threaded Windows Graphics Capture session for one window and
 * screen-space chart rectangle. The session crops on the GPU and publishes
 * only compact height fields. Returns an HRESULT-style value (0 on success).
 */
CAPTURE_API int32_t __stdcall Capture_Start(const CaptureConfig* config);

/*
 * Returns 1 when a newer height field was copied, 0 when no new chart update
 * is available, and a negative value on failure.
 */
CAPTURE_API int32_t __stdcall Capture_TryGetHeightField(
    uint64_t after_sequence,
    float* out_y_from_top,
    int32_t capacity,
    CaptureFrameInfo* out_info);

CAPTURE_API void __stdcall Capture_Stop(void);

/* Stateful native extractor used by non-WGC capture fallbacks and tests. */
CAPTURE_API void* __stdcall CaptureExtractor_Create(
    int32_t inset_left,
    int32_t inset_top,
    int32_t inset_right,
    int32_t inset_bottom,
    int32_t smooth_radius);

CAPTURE_API void __stdcall CaptureExtractor_Destroy(void* extractor);

/* Returns 1 on success, 0 for an invalid frame, and a negative value on error. */
CAPTURE_API int32_t __stdcall CaptureExtractor_ExtractBgra(
    void* extractor,
    const uint8_t* bgra,
    int32_t width,
    int32_t height,
    int32_t stride,
    float* out_y_from_top,
    int32_t capacity,
    CaptureFrameInfo* out_info);

#ifdef __cplusplus
}
#endif
