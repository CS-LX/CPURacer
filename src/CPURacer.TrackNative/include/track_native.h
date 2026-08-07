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

/* External topmost overlay (screen coords). */
#define TRACK_FOLLOW_EXTERNAL 0
/* TaskmgrPlayer-style: host paints as child of chart (client 0,0,w,h). */
#define TRACK_FOLLOW_CHILD 1

typedef struct TrackRoiState {
    int64_t chart_hwnd;
    int64_t main_hwnd;
    int32_t left;   /* screen px (external mode) */
    int32_t top;
    int32_t width;  /* chart outer size in screen px; child mode also uses as MoveWindow size */
    int32_t height;
    uint32_t dpi;
    int32_t chart_count;
    int32_t should_show; /* external: foreground; child: chart visible */
    int32_t is_cpu_page;
    int32_t follow_mode; /* TRACK_FOLLOW_* echoed for the host */
} TrackRoiState;

typedef void(__stdcall* TrackRoiCallback)(const TrackRoiState* state, void* user_data);

TRACK_API void __stdcall Track_SetFollowMode(int mode);

/* Returns 0 on success. */
TRACK_API int __stdcall Track_Start(TrackRoiCallback callback, void* user_data);

TRACK_API void __stdcall Track_Stop(void);

TRACK_API int __stdcall Track_GetState(TrackRoiState* out_state);

#ifdef __cplusplus
}
#endif
