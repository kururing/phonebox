#include "nvenc_encoder.h"
#include <algorithm>
#include <cstdio>
#include <cstring>

typedef NVENCSTATUS (NVENCAPI *PFN_NvEncodeAPICreateInstance)(NV_ENCODE_API_FUNCTION_LIST*);

std::string NvencEncoder::NvStr(const char* what, NVENCSTATUS st) {
    char buf[160];
    std::snprintf(buf, sizeof(buf), "%s failed (NVENCSTATUS %d)", what, static_cast<int>(st));
    return buf;
}

NvencEncoder::~NvencEncoder() {
    Shutdown();
}

bool NvencEncoder::Init(ID3D11Device* device, uint32_t width, uint32_t height, uint32_t fps,
                        uint32_t bitrateBps, uint32_t maxBitrateBps, bool hevc, bool intraRefresh,
                        bool h264Baseline, std::string& err) {
    Shutdown();
    width_ = width;
    height_ = height;
    fps_ = std::max(fps, 1u);
    hevc_ = hevc;
    intraRefresh_ = intraRefresh;
    frameIdx_ = 0;

    dll_ = LoadLibraryW(L"nvEncodeAPI64.dll");
    if (!dll_) { err = "LoadLibraryW(nvEncodeAPI64.dll) failed"; return false; }

    auto createInst = reinterpret_cast<PFN_NvEncodeAPICreateInstance>(
        GetProcAddress(dll_, "NvEncodeAPICreateInstance"));
    if (!createInst) { err = "GetProcAddress(NvEncodeAPICreateInstance) failed"; return false; }

    std::memset(&fn_, 0, sizeof(fn_));
    fn_.version = NV_ENCODE_API_FUNCTION_LIST_VER;
    NVENCSTATUS st = createInst(&fn_);
    if (st != NV_ENC_SUCCESS) { err = NvStr("NvEncodeAPICreateInstance", st); return false; }

    NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS openParams{};
    openParams.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
    openParams.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
    openParams.device = device;
    openParams.apiVersion = NVENCAPI_VERSION;
    st = fn_.nvEncOpenEncodeSessionEx(&openParams, &encoder_);
    if (st != NV_ENC_SUCCESS) { err = NvStr("nvEncOpenEncodeSessionEx", st); return false; }

    const GUID codecGuid = hevc_ ? NV_ENC_CODEC_HEVC_GUID : NV_ENC_CODEC_H264_GUID;
    const uint64_t pixels = static_cast<uint64_t>(width_) * height_;
    const GUID presetGuid = (pixels <= 1500000ULL) ? NV_ENC_PRESET_P3_GUID : NV_ENC_PRESET_P1_GUID;
    const NV_ENC_TUNING_INFO tuning = NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;

    NV_ENC_PRESET_CONFIG presetCfg{};
    presetCfg.version = NV_ENC_PRESET_CONFIG_VER;
    presetCfg.presetCfg.version = NV_ENC_CONFIG_VER;
    st = fn_.nvEncGetEncodePresetConfigEx(encoder_, codecGuid, presetGuid, tuning, &presetCfg);
    if (st != NV_ENC_SUCCESS) { err = NvStr("nvEncGetEncodePresetConfigEx", st); return false; }

    encodeConfig_ = presetCfg.presetCfg;
    encodeConfig_.version = NV_ENC_CONFIG_VER;
    encodeConfig_.profileGUID = hevc_
        ? NV_ENC_HEVC_PROFILE_MAIN_GUID
        : (h264Baseline ? NV_ENC_H264_PROFILE_BASELINE_GUID : NV_ENC_H264_PROFILE_HIGH_GUID);
    encodeConfig_.frameIntervalP = 1; // 0 B-frames
    encodeConfig_.gopLength = intraRefresh_ ? NVENC_INFINITE_GOPLENGTH : (fps_ * 4);

    auto& rc = encodeConfig_.rcParams;
    rc.rateControlMode = NV_ENC_PARAMS_RC_VBR;
    rc.multiPass = NV_ENC_MULTI_PASS_DISABLED;
    rc.averageBitRate = bitrateBps ? bitrateBps : 10000000u;
    rc.maxBitRate = maxBitrateBps ? std::max(maxBitrateBps, rc.averageBitRate) : std::max(rc.averageBitRate * 3u / 2u, rc.averageBitRate);
    rc.vbvBufferSize = std::max(static_cast<uint32_t>((static_cast<uint64_t>(rc.maxBitRate) * 2ULL) / fps_), 400000u);
    rc.vbvInitialDelay = rc.vbvBufferSize;
    rc.lowDelayKeyFrameScale = 1;
    rc.zeroReorderDelay = 1;
    rc.enableLookahead = 0;
    rc.enableAQ = 0;

    // Query intra-refresh support on this GPU before enabling
    bool canIntraRefresh = false;
    if (intraRefresh_) {
        NV_ENC_CAPS_PARAM cap{};
        cap.version = NV_ENC_CAPS_PARAM_VER;
        cap.capsToQuery = NV_ENC_CAPS_SUPPORT_INTRA_REFRESH;
        int val = 0;
        if (fn_.nvEncGetEncodeCaps(encoder_, codecGuid, &cap, &val) == NV_ENC_SUCCESS && val > 0) {
            canIntraRefresh = true;
        }
    }

    if (hevc_) {
        auto& c = encodeConfig_.encodeCodecConfig.hevcConfig;
        c.idrPeriod = encodeConfig_.gopLength;
        c.repeatSPSPPS = 1;
        c.disableSPSPPS = 0;
        c.maxNumRefFramesInDPB = 1;
        c.hevcVUIParameters.bitstreamRestrictionFlag = 1;
        if (canIntraRefresh) {
            c.enableIntraRefresh = 1;
            c.intraRefreshPeriod = 60;
            c.intraRefreshCnt = 10;
        }
    } else {
        auto& c = encodeConfig_.encodeCodecConfig.h264Config;
        c.idrPeriod = encodeConfig_.gopLength;
        c.repeatSPSPPS = 1;
        c.disableSPSPPS = 0;
        c.maxNumRefFrames = 1;
        c.h264VUIParameters.bitstreamRestrictionFlag = 1;
        if (canIntraRefresh) {
            c.enableIntraRefresh = 1;
            c.intraRefreshPeriod = 60;
            c.intraRefreshCnt = 10;
        }
    }

    std::memset(&initParams_, 0, sizeof(initParams_));
    initParams_.version = NV_ENC_INITIALIZE_PARAMS_VER;
    initParams_.encodeGUID = codecGuid;
    initParams_.presetGUID = presetGuid;
    initParams_.tuningInfo = tuning;
    initParams_.encodeWidth = width_;
    initParams_.encodeHeight = height_;
    initParams_.darWidth = width_;
    initParams_.darHeight = height_;
    initParams_.frameRateNum = fps_;
    initParams_.frameRateDen = 1;
    initParams_.enablePTD = 1;
    initParams_.enableEncodeAsync = 0;
    initParams_.maxEncodeWidth = width_;
    initParams_.maxEncodeHeight = height_;
    initParams_.encodeConfig = &encodeConfig_;

    st = fn_.nvEncInitializeEncoder(encoder_, &initParams_);
    if (st != NV_ENC_SUCCESS) { err = NvStr("nvEncInitializeEncoder", st); return false; }

    NV_ENC_CREATE_BITSTREAM_BUFFER bs{};
    bs.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
    st = fn_.nvEncCreateBitstreamBuffer(encoder_, &bs);
    if (st != NV_ENC_SUCCESS) { err = NvStr("nvEncCreateBitstreamBuffer", st); return false; }
    bitstream_ = bs.bitstreamBuffer;

    return true;
}

bool NvencEncoder::RegisterRing(ID3D11Texture2D* const* textures, uint32_t count, std::string& err) {
    UnregisterRing();
    registered_.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        NV_ENC_REGISTER_RESOURCE reg{};
        reg.version = NV_ENC_REGISTER_RESOURCE_VER;
        reg.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
        reg.width = width_;
        reg.height = height_;
        reg.pitch = 0;
        reg.resourceToRegister = textures[i];
        reg.bufferFormat = NV_ENC_BUFFER_FORMAT_ARGB;
        reg.bufferUsage = NV_ENC_INPUT_IMAGE;
        NVENCSTATUS st = fn_.nvEncRegisterResource(encoder_, &reg);
        if (st != NV_ENC_SUCCESS) {
            UnregisterRing();
            err = NvStr("nvEncRegisterResource", st);
            return false;
        }
        registered_.push_back(reg.registeredResource);
    }
    return true;
}

void NvencEncoder::UnregisterRing() {
    if (encoder_) {
        for (auto r : registered_) {
            if (r) fn_.nvEncUnregisterResource(encoder_, r);
        }
    }
    registered_.clear();
}

bool NvencEncoder::ReconfigureBitrate(uint32_t bitrateBps, uint32_t maxBitrateBps, std::string& err) {
    if (!encoder_) return false;
    uint32_t maxBr = std::max(maxBitrateBps, bitrateBps);
    encodeConfig_.rcParams.averageBitRate = bitrateBps;
    encodeConfig_.rcParams.maxBitRate = maxBr;
    encodeConfig_.rcParams.vbvBufferSize = std::max(static_cast<uint32_t>((static_cast<uint64_t>(maxBr) * 2ULL) / fps_), 400000u);
    encodeConfig_.rcParams.vbvInitialDelay = encodeConfig_.rcParams.vbvBufferSize;

    NV_ENC_RECONFIGURE_PARAMS rec{};
    rec.version = NV_ENC_RECONFIGURE_PARAMS_VER;
    rec.reInitEncodeParams = initParams_;
    rec.resetEncoder = 0;
    rec.forceIDR = 0;
    NVENCSTATUS st = fn_.nvEncReconfigureEncoder(encoder_, &rec);
    if (st != NV_ENC_SUCCESS) { err = NvStr("nvEncReconfigureEncoder", st); return false; }
    return true;
}

void NvencEncoder::Shutdown() {
    UnregisterRing();
    if (encoder_ && bitstream_) {
        fn_.nvEncDestroyBitstreamBuffer(encoder_, bitstream_);
        bitstream_ = nullptr;
    }
    if (encoder_) {
        fn_.nvEncDestroyEncoder(encoder_);
        encoder_ = nullptr;
    }
    if (dll_) {
        FreeLibrary(dll_);
        dll_ = nullptr;
    }
}
