#pragma once
#include <stdint.h>

#ifdef PB_CAP_EXPORTS
#define PB_API __declspec(dllexport)
#else
#define PB_API __declspec(dllimport)
#endif

typedef struct PbCapHandle PbCapHandle;

typedef enum PbCodec {
    PB_CODEC_H264 = 0,
    PB_CODEC_HEVC = 1
} PbCodec;

typedef enum PbSourceMode {
    PB_SRC_DXGI_OUTPUT = 0,
    PB_SRC_D3D11_SYNTHETIC = 1
} PbSourceMode;

typedef enum PbFrameFlags {
    PB_FLAG_IDR  = 1 << 0,
    PB_FLAG_HEVC = 1 << 1
} PbFrameFlags;

typedef struct PbCapConfig {
    uint32_t adapter_index;    // DXGI adapter index (0xFFFFFFFF = auto first NVIDIA)
    uint32_t output_index;     // DXGI output index (for PB_SRC_DXGI_OUTPUT)
    uint32_t source_mode;      // PbSourceMode
    uint32_t synthetic_width;  // Used when source_mode == PB_SRC_D3D11_SYNTHETIC (default 1920)
    uint32_t synthetic_height; // Used when source_mode == PB_SRC_D3D11_SYNTHETIC (default 1080)
    uint32_t max_fps;          // Default 60
    uint32_t bitrate_bps;      // Default 6_000_000
    uint32_t max_bitrate_bps;  // Default 10_000_000
    uint32_t codec;            // PbCodec (0 = H.264 High, 1 = HEVC Main)
    uint32_t draw_cursor;      // 1 = composite hardware cursor on GPU
    uint32_t intra_refresh;    // 1 = periodic intra-refresh instead of periodic IDR spikes
    uint32_t h264_baseline;    // 1 = H.264 Baseline profile (for decoders without FEATURE_LowLatency)
    uint32_t encode_width;     // 0 = source native; >0 = GPU-scaled encode width (even, clamped <= source)
    uint32_t encode_height;    // 0 = source native; >0 = GPU-scaled encode height (even, clamped <= source)
} PbCapConfig;

typedef struct PbCapStats {
    uint64_t frames_captured;
    uint64_t frames_encoded;
    uint64_t frames_skipped;
    uint64_t idr_count;
    uint64_t bytes_encoded;
    uint32_t last_capture_us;
    uint32_t last_encode_us;
    double   avg_capture_us;
    double   avg_encode_us;
    uint32_t width;
    uint32_t height;
    uint64_t vram_current_bytes;
} PbCapStats;

typedef void (__cdecl *PbFrameCallback)(
    const uint8_t* data,
    uint32_t size,
    uint64_t pts_us,
    uint32_t capture_us,
    uint32_t encode_us,
    uint32_t flags,
    void* user);

#ifdef __cplusplus
extern "C" {
#endif

PB_API int32_t pb_cap_create(const PbCapConfig* cfg, PbFrameCallback cb, void* user, PbCapHandle** out_handle);
PB_API int32_t pb_cap_start(PbCapHandle* h);
PB_API void    pb_cap_stop(PbCapHandle* h);
PB_API void    pb_cap_request_idr(PbCapHandle* h);
PB_API int32_t pb_cap_set_bitrate(PbCapHandle* h, uint32_t bitrate_bps, uint32_t max_bitrate_bps);
PB_API int32_t pb_cap_set_encode_size(PbCapHandle* h, uint32_t encode_width, uint32_t encode_height);
PB_API int32_t pb_cap_get_stats(PbCapHandle* h, PbCapStats* out_stats);
PB_API int32_t pb_cap_get_last_error(PbCapHandle* h, char* buf, uint32_t buf_len);
PB_API void    pb_cap_destroy(PbCapHandle* h);

#ifdef __cplusplus
}
#endif
