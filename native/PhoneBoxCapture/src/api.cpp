#include "phonebox_capture.h"
#include "d3d_duplicator.h"
#include "nvenc_encoder.h"

#include <windows.h>
#include <mmsystem.h>
#include <algorithm>
#include <atomic>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>

namespace {

int64_t QpcNow() {
    LARGE_INTEGER li{};
    QueryPerformanceCounter(&li);
    return li.QuadPart;
}

int64_t QpcFreq() {
    static int64_t f = [] {
        LARGE_INTEGER li{};
        QueryPerformanceFrequency(&li);
        return li.QuadPart;
    }();
    return f;
}

uint64_t TicksToUs(int64_t ticks) {
    return static_cast<uint64_t>((ticks * 1000000LL) / QpcFreq());
}

void ClampForEncoder(uint32_t& w, uint32_t& h) {
    constexpr uint32_t kMaxEncDim = 4096u;
    if (w > kMaxEncDim || h > kMaxEncDim) {
        double scale = std::min(static_cast<double>(kMaxEncDim) / w, static_cast<double>(kMaxEncDim) / h);
        w = static_cast<uint32_t>(w * scale);
        h = static_cast<uint32_t>(h * scale);
    }
    w = std::max(64u, w & ~1u);
    h = std::max(64u, h & ~1u);
}

} // namespace

struct PbCapHandle {
    PbCapConfig cfg{};
    PbFrameCallback callback = nullptr;
    void* user = nullptr;

    D3dFrameSource source;
    NvencEncoder encoder;

    std::atomic<bool> running{ false };
    std::atomic<bool> forceIdr{ true };
    std::atomic<uint32_t> pendingBitrate{ 0 };
    std::atomic<uint32_t> pendingMaxBitrate{ 0 };
    std::atomic<uint32_t> pendingEncodeW{ 0 };
    std::atomic<uint32_t> pendingEncodeH{ 0 };
    std::thread worker;

    std::mutex mu;
    PbCapStats stats{};
    std::string lastError;
    uint64_t totalCaptureUs = 0;
    uint64_t totalEncodeUs = 0;

    void SetError(const std::string& e) {
        std::lock_guard<std::mutex> lk(mu);
        lastError = e;
    }

    bool InitEncoderAndRing(std::string& err) {
        uint32_t ew = source.EncodeWidth();
        uint32_t eh = source.EncodeHeight();
        ClampForEncoder(ew, eh);
        if (ew != source.EncodeWidth() || eh != source.EncodeHeight()) {
            encoder.UnregisterRing();
            if (!source.SetEncodeSize(ew, eh, err)) {
                return false;
            }
        }
        if (!encoder.Init(source.Device(), source.EncodeWidth(), source.EncodeHeight(),
                          cfg.max_fps ? cfg.max_fps : 60,
                          cfg.bitrate_bps ? cfg.bitrate_bps : 10000000u,
                          cfg.max_bitrate_bps ? cfg.max_bitrate_bps : 15000000u,
                          cfg.codec == PB_CODEC_HEVC,
                          cfg.intra_refresh != 0,
                          cfg.h264_baseline != 0, err)) {
            return false;
        }
        ID3D11Texture2D* texs[D3dFrameSource::kRingSize];
        for (uint32_t i = 0; i < D3dFrameSource::kRingSize; ++i) texs[i] = source.RingTexture(i);
        if (!encoder.RegisterRing(texs, D3dFrameSource::kRingSize, err)) {
            return false;
        }
        std::lock_guard<std::mutex> lk(mu);
        stats.width = source.EncodeWidth();
        stats.height = source.EncodeHeight();
        return true;
    }

    void WorkerLoop() {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        timeBeginPeriod(1);

        const uint32_t fps = cfg.max_fps ? cfg.max_fps : 60;
        const uint64_t frameIntervalUs = 1000000ULL / fps;
        const int64_t startTick = QpcNow();
        uint64_t nextTargetUs = 0;
        bool hasLastFrame = false;
        uint32_t lastSlot = 0;
        uint32_t trailingFlushLeft = 0;
        uint64_t lastEncodeUs = 0;

        auto encodeAndReport = [&](uint32_t encodeSlot, uint32_t capUs, uint64_t ptsUs) {
            bool idr = forceIdr.exchange(false, std::memory_order_acq_rel);
            int64_t tEnc0 = QpcNow();
            std::string encErr;
            bool ok = encoder.EncodeSlot(encodeSlot, ptsUs, idr, [&](const EncodedPacketView& pkt) {
                uint32_t encUs = static_cast<uint32_t>(TicksToUs(QpcNow() - tEnc0));
                uint32_t flags = (pkt.isIdr ? PB_FLAG_IDR : 0u) | (pkt.isHevc ? PB_FLAG_HEVC : 0u);
                if (callback) {
                    callback(pkt.data, pkt.size, ptsUs, capUs, encUs, flags, user);
                }
                std::lock_guard<std::mutex> lk(mu);
                stats.frames_captured++;
                stats.frames_encoded++;
                if (pkt.isIdr) stats.idr_count++;
                stats.bytes_encoded += pkt.size;
                stats.last_capture_us = capUs;
                stats.last_encode_us = encUs;
                totalCaptureUs += capUs;
                totalEncodeUs += encUs;
                stats.avg_capture_us = static_cast<double>(totalCaptureUs) / stats.frames_encoded;
                stats.avg_encode_us = static_cast<double>(totalEncodeUs) / stats.frames_encoded;
                stats.width = source.EncodeWidth();
                stats.height = source.EncodeHeight();
            }, encErr);
            if (!ok) {
                SetError(encErr);
            }
            lastEncodeUs = ptsUs;
        };

        while (running.load(std::memory_order_relaxed)) {
            uint32_t newEncW = pendingEncodeW.exchange(0, std::memory_order_acq_rel);
            if (newEncW > 0) {
                uint32_t newEncH = pendingEncodeH.load(std::memory_order_acquire);
                if (newEncH > 0) {
                    uint32_t targetW = std::min(newEncW, source.Width());
                    uint32_t targetH = std::min(newEncH, source.Height());
                    ClampForEncoder(targetW, targetH);
                    if (targetW != source.EncodeWidth() || targetH != source.EncodeHeight()) {
                        encoder.UnregisterRing();
                        std::string rerr;
                        if (source.SetEncodeSize(targetW, targetH, rerr) && InitEncoderAndRing(rerr)) {
                            hasLastFrame = false;
                            trailingFlushLeft = 0;
                            forceIdr.store(true, std::memory_order_release);
                        } else {
                            SetError(rerr);
                        }
                    }
                }
            }

            uint32_t newBr = pendingBitrate.exchange(0, std::memory_order_acq_rel);
            if (newBr > 0) {
                uint32_t newMax = pendingMaxBitrate.load(std::memory_order_acquire);
                std::string rerr;
                if (!encoder.ReconfigureBitrate(newBr, newMax ? newMax : newBr, rerr)) {
                    SetError(rerr);
                }
            }

            if (nextTargetUs > 0) {
                uint64_t elapsedUs = TicksToUs(QpcNow() - startTick);
                if (elapsedUs < nextTargetUs) {
                    DWORD waitMs = static_cast<DWORD>((nextTargetUs - elapsedUs) / 1000);
                    if (waitMs > 0) {
                        Sleep(waitMs);
                    }
                    if (!running.load(std::memory_order_relaxed)) break;
                }
            }

            int64_t tCap0 = QpcNow();
            uint32_t slot = 0;
            bool resized = false;
            std::string err;
            AcquireStatus st = source.AcquireNext(16, slot, resized, err);
            int64_t tCap1 = QpcNow();

            if (st == AcquireStatus::Timeout) {
                if (resized) {
                    hasLastFrame = false;
                    trailingFlushLeft = 0;
                    std::string rerr;
                    if (!InitEncoderAndRing(rerr)) SetError(rerr);
                    forceIdr.store(true, std::memory_order_release);
                    nextTargetUs = 0;
                    continue;
                }

                uint64_t nowUs = TicksToUs(tCap1 - startTick);
                bool needIdrNow = forceIdr.load(std::memory_order_acquire);
                bool needFlush = trailingFlushLeft > 0;
                bool needKeepalive = (nowUs - lastEncodeUs) >= 1000000ULL;

                if (hasLastFrame && (needIdrNow || needFlush || needKeepalive)) {
                    if (trailingFlushLeft > 0) --trailingFlushLeft;
                    encodeAndReport(lastSlot, 0, nowUs);
                    nextTargetUs = nowUs + frameIntervalUs;
                } else {
                    {
                        std::lock_guard<std::mutex> lk(mu);
                        stats.frames_skipped++;
                    }
                    nextTargetUs = 0;
                }
                continue;
            }
            if (st == AcquireStatus::AccessLost) {
                hasLastFrame = false;
                trailingFlushLeft = 0;
                Sleep(30);
                std::string rerr;
                if (source.ReacquireDuplication(rerr)) {
                    if (!InitEncoderAndRing(rerr)) SetError(rerr);
                    forceIdr.store(true, std::memory_order_release);
                } else {
                    SetError(rerr);
                }
                nextTargetUs = 0;
                continue;
            }
            if (st == AcquireStatus::Error) {
                SetError(err);
                Sleep(30);
                nextTargetUs = 0;
                continue;
            }

            uint32_t capUs = static_cast<uint32_t>(TicksToUs(tCap1 - tCap0));
            uint64_t ptsUs = TicksToUs(tCap1 - startTick);
            hasLastFrame = true;
            lastSlot = slot;
            trailingFlushLeft = 3;
            encodeAndReport(slot, capUs, ptsUs);
            nextTargetUs = ptsUs + frameIntervalUs;
        }

        timeEndPeriod(1);
    }
};

extern "C" {

PB_API int32_t pb_cap_create(const PbCapConfig* cfg, PbFrameCallback cb, void* user, PbCapHandle** out_handle) {
    if (!cfg || !out_handle) return -1;
    auto* h = new PbCapHandle();
    h->cfg = *cfg;
    h->callback = cb;
    h->user = user;

    std::string err;
    if (!h->source.Init(cfg->adapter_index, cfg->output_index,
                        cfg->source_mode == PB_SRC_D3D11_SYNTHETIC,
                        cfg->synthetic_width, cfg->synthetic_height,
                        cfg->draw_cursor != 0,
                        cfg->encode_width, cfg->encode_height, err)) {
        h->SetError(err);
        *out_handle = h;
        return -2;
    }
    if (!h->InitEncoderAndRing(err)) {
        h->SetError(err);
        *out_handle = h;
        return -3;
    }
    h->stats.width = h->source.EncodeWidth();
    h->stats.height = h->source.EncodeHeight();
    *out_handle = h;
    return 0;
}

PB_API int32_t pb_cap_start(PbCapHandle* h) {
    if (!h) return -1;
    bool expected = false;
    if (!h->running.compare_exchange_strong(expected, true)) return 0;
    h->worker = std::thread(&PbCapHandle::WorkerLoop, h);
    return 0;
}

PB_API void pb_cap_stop(PbCapHandle* h) {
    if (!h) return;
    h->running.store(false, std::memory_order_release);
    if (h->worker.joinable()) h->worker.join();
}

PB_API void pb_cap_request_idr(PbCapHandle* h) {
    if (h) h->forceIdr.store(true, std::memory_order_release);
}

PB_API int32_t pb_cap_set_bitrate(PbCapHandle* h, uint32_t bitrate_bps, uint32_t max_bitrate_bps) {
    if (!h || bitrate_bps == 0) return -1;
    h->pendingMaxBitrate.store(max_bitrate_bps, std::memory_order_release);
    h->pendingBitrate.store(bitrate_bps, std::memory_order_release);
    return 0;
}

PB_API int32_t pb_cap_set_encode_size(PbCapHandle* h, uint32_t encode_width, uint32_t encode_height) {
    if (!h || encode_width < 64 || encode_height < 64) return -1;
    h->pendingEncodeH.store(encode_height, std::memory_order_release);
    h->pendingEncodeW.store(encode_width, std::memory_order_release);
    return 0;
}

PB_API int32_t pb_cap_get_stats(PbCapHandle* h, PbCapStats* out_stats) {
    if (!h || !out_stats) return -1;
    std::lock_guard<std::mutex> lk(h->mu);
    *out_stats = h->stats;
    out_stats->vram_current_bytes = h->source.QueryVramUsageBytes();
    return 0;
}

PB_API int32_t pb_cap_get_last_error(PbCapHandle* h, char* buf, uint32_t buf_len) {
    if (!h || !buf || buf_len == 0) return -1;
    std::lock_guard<std::mutex> lk(h->mu);
    uint32_t n = static_cast<uint32_t>(std::min<size_t>(h->lastError.size(), buf_len - 1));
    std::memcpy(buf, h->lastError.data(), n);
    buf[n] = '\0';
    return static_cast<int32_t>(n);
}

PB_API void pb_cap_destroy(PbCapHandle* h) {
    if (!h) return;
    pb_cap_stop(h);
    h->encoder.Shutdown();
    delete h;
}

} // extern "C"
