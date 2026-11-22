#pragma once
#include <d3d11.h>
#include <windows.h>
#include <nvEncodeAPI.h>
#include <cstdint>
#include <string>
#include <vector>

struct EncodedPacketView {
    const uint8_t* data = nullptr;
    uint32_t size = 0;
    bool isIdr = false;
    bool isHevc = false;
};

class NvencEncoder {
public:
    ~NvencEncoder();

    bool Init(ID3D11Device* device, uint32_t width, uint32_t height, uint32_t fps,
              uint32_t bitrateBps, uint32_t maxBitrateBps, bool hevc, bool intraRefresh,
              bool h264Baseline, std::string& err);

    bool RegisterRing(ID3D11Texture2D* const* textures, uint32_t count, std::string& err);
    void UnregisterRing();

    bool ReconfigureBitrate(uint32_t bitrateBps, uint32_t maxBitrateBps, std::string& err);

    // Encodes registered slot synchronously; calls consumer while bitstream is locked (zero copy).
    template <typename Fn>
    bool EncodeSlot(uint32_t slot, uint64_t ptsUs, bool forceIdr, Fn&& onPacket, std::string& err) {
        if (slot >= registered_.size() || !registered_[slot]) {
            err = "Invalid NVENC ring slot";
            return false;
        }
        NV_ENC_MAP_INPUT_RESOURCE map{};
        map.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
        map.registeredResource = registered_[slot];
        NVENCSTATUS st = fn_.nvEncMapInputResource(encoder_, &map);
        if (st != NV_ENC_SUCCESS) { err = NvStr("nvEncMapInputResource", st); return false; }

        NV_ENC_PIC_PARAMS pic{};
        pic.version = NV_ENC_PIC_PARAMS_VER;
        pic.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
        pic.inputBuffer = map.mappedResource;
        pic.bufferFmt = map.mappedBufferFmt;
        pic.inputWidth = width_;
        pic.inputHeight = height_;
        pic.inputPitch = width_;
        pic.outputBitstream = bitstream_;
        pic.inputTimeStamp = ptsUs;
        pic.frameIdx = frameIdx_++;
        if (forceIdr || frameIdx_ == 1) {
            pic.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
        }

        st = fn_.nvEncEncodePicture(encoder_, &pic);
        if (st != NV_ENC_SUCCESS) {
            fn_.nvEncUnmapInputResource(encoder_, map.mappedResource);
            err = NvStr("nvEncEncodePicture", st);
            return false;
        }

        NV_ENC_LOCK_BITSTREAM lock{};
        lock.version = NV_ENC_LOCK_BITSTREAM_VER;
        lock.outputBitstream = bitstream_;
        st = fn_.nvEncLockBitstream(encoder_, &lock);
        if (st != NV_ENC_SUCCESS) {
            fn_.nvEncUnmapInputResource(encoder_, map.mappedResource);
            err = NvStr("nvEncLockBitstream", st);
            return false;
        }

        EncodedPacketView view{};
        view.data = static_cast<const uint8_t*>(lock.bitstreamBufferPtr);
        view.size = lock.bitstreamSizeInBytes;
        view.isIdr = (lock.pictureType == NV_ENC_PIC_TYPE_IDR) || (lock.pictureType == NV_ENC_PIC_TYPE_I);
        view.isHevc = hevc_;
        onPacket(view);

        fn_.nvEncUnlockBitstream(encoder_, bitstream_);
        fn_.nvEncUnmapInputResource(encoder_, map.mappedResource);
        return true;
    }

    void Shutdown();
    bool IsHevc() const { return hevc_; }

private:
    static std::string NvStr(const char* what, NVENCSTATUS st);

    HMODULE dll_ = nullptr;
    NV_ENCODE_API_FUNCTION_LIST fn_{};
    void* encoder_ = nullptr;
    NV_ENC_OUTPUT_PTR bitstream_ = nullptr;
    std::vector<NV_ENC_REGISTERED_PTR> registered_;

    NV_ENC_INITIALIZE_PARAMS initParams_{};
    NV_ENC_CONFIG encodeConfig_{};
    uint32_t width_ = 0;
    uint32_t height_ = 0;
    uint32_t fps_ = 60;
    bool hevc_ = false;
    bool intraRefresh_ = true;
    uint32_t frameIdx_ = 0;
};
