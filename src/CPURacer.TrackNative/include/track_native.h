#pragma once

#include <stdint.h>

#ifdef TRACKNATIVE_EXPORTS
#define TRACK_API __declspec(dllexport)
#else
#define TRACK_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct TrackRoiState {
    int64_t chart_hwnd;
    int64_t main_hwnd;
    int32_t left;
    int32_t top;
    int32_t width;
    int32_t height;
    uint32_t dpi;
    int32_t chart_count;
    int32_t should_show; /* 1 = Taskmgr/chart foreground AND CPU page chart found */
    int32_t is_cpu_page; /* 1 = heuristic says CPU performance page */
} TrackRoiState;

typedef void(__stdcall* TrackRoiCallback)(const TrackRoiState* state, void* user_data);

/* Returns 0 on success. */
TRACK_API int __stdcall Track_Start(TrackRoiCallback callback, void* user_data);

TRACK_API void __stdcall Track_Stop(void);

/* Optional synchronous snapshot (0 = ok, non-zero = none). */
TRACK_API int __stdcall Track_GetState(TrackRoiState* out_state);

#ifdef __cplusplus
}
#endif
