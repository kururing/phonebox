#pragma once
#include <d3d11_1.h>
#include <dxgi1_5.h>
#include <wrl/client.h>
#include <cstdint>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

enum class AcquireStatus {
    Acquired,
    Timeout,
    AccessLost,
    Error
};

class D3dFrameSource {
public:
    static constexpr uint32_t kRingSize = 2;
    ~D3dFrameSource() { ReleaseGdiFallback(); }

    bool Init(uint32_t adapterIndex, uint32_t outputIndex, bool synthetic,
              uint32_t synthW, uint32_t synthH, bool drawCursor,
              uint32_t targetEncW, uint32_t targetEncH, std::string& err);

    AcquireStatus AcquireNext(uint32_t timeoutMs, uint32_t& outSlot, bool& outResized, std::string& err);
    bool ReacquireDuplication(std::string& err);
    bool SetEncodeSize(uint32_t targetEncW, uint32_t targetEncH, std::string& err);

    ID3D11Device* Device() const { return device_.Get(); }
    ID3D11Texture2D* RingTexture(uint32_t slot) const { return ring_[slot].Get(); }
    uint32_t Width() const { return width_; }
    uint32_t Height() const { return height_; }
    uint32_t EncodeWidth() const { return encodeW_ ? encodeW_ : width_; }
    uint32_t EncodeHeight() const { return encodeH_ ? encodeH_ : height_; }
    uint64_t QueryVramUsageBytes() const;

private:
    bool NeedsScale() const { return encodeW_ != width_ || encodeH_ != height_; }
    bool CreateRingTextures(uint32_t w, uint32_t h, std::string& err);
    bool EnsureFullDesktopTexture(std::string& err);
    bool EnsureCapStagingTexture(std::string& err);
    bool InitGdiFallback(int left, int top, uint32_t w, uint32_t h, std::string& err);
    void ReleaseGdiFallback();
    bool InitDuplication(std::string& err);
    bool InitCursorPipeline(std::string& err);
    bool InitScalePipeline(std::string& err);
    void UpdateScaleConstants();
    void UpdateCursorShape(const DXGI_OUTDUPL_FRAME_INFO& info);
    void DrawCursorToRtv(ID3D11RenderTargetView* rtv);
    void ScaleFullToSlot(uint32_t slot);
    void RenderSyntheticFrame(uint32_t slot);

    bool synthetic_ = false;
    bool drawCursor_ = true;
    uint32_t adapterIndex_ = 0xFFFFFFFFu;
    uint32_t outputIndex_ = 0;
    uint32_t width_ = 0;
    uint32_t height_ = 0;
    uint32_t reqEncodeW_ = 0;
    uint32_t reqEncodeH_ = 0;
    uint32_t encodeW_ = 0;
    uint32_t encodeH_ = 0;
    uint32_t nextSlot_ = 0;
    uint64_t synthFrameIdx_ = 0;

    ComPtr<IDXGIFactory4> factory_;
    ComPtr<IDXGIAdapter3> adapter_;
    ComPtr<IDXGIOutput1> output_;
    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext1> context_;
    ComPtr<ID3D11Device> capDevice_;
    ComPtr<ID3D11DeviceContext1> capContext_;
    ComPtr<ID3D11Texture2D> capStaging_;
    bool crossAdapter_ = false;
    bool gdiFallback_ = false;
    int gdiLeft_ = 0;
    int gdiTop_ = 0;
    HDC gdiScreenDc_ = nullptr;
    HDC gdiMemDc_ = nullptr;
    HBITMAP gdiBitmap_ = nullptr;
    HGDIOBJ gdiOldObj_ = nullptr;
    void* gdiBits_ = nullptr;
    uint64_t gdiLastHash_ = 0;
    ULONGLONG gdiLastTickMs_ = 0;
    ComPtr<IDXGIOutputDuplication> dupl_;

    ComPtr<ID3D11Texture2D> ring_[kRingSize];
    ComPtr<ID3D11RenderTargetView> ringRtv_[kRingSize];

    ComPtr<ID3D11Texture2D> fullDesktopTex_;
    ComPtr<ID3D11RenderTargetView> fullDesktopRtv_;
    ComPtr<ID3D11ShaderResourceView> fullDesktopSrv_;

    // GPU supersampling downscaler
    ComPtr<ID3D11VertexShader> scaleVs_;
    ComPtr<ID3D11PixelShader> scalePs_;
    ComPtr<ID3D11SamplerState> scaleSampler_;
    ComPtr<ID3D11Buffer> scaleCb_;
    bool scaleReady_ = false;

    // GPU cursor overlay
    ComPtr<ID3D11VertexShader> cursorVs_;
    ComPtr<ID3D11PixelShader> cursorPs_;
    ComPtr<ID3D11SamplerState> cursorSampler_;
    ComPtr<ID3D11BlendState> cursorBlend_;
    ComPtr<ID3D11Texture2D> cursorTex_;
    ComPtr<ID3D11ShaderResourceView> cursorSrv_;
    std::vector<uint8_t> cursorRawBuf_;
    std::vector<uint32_t> cursorBgra_;
    uint32_t cursorW_ = 0;
    uint32_t cursorH_ = 0;
    int32_t cursorX_ = 0;
    int32_t cursorY_ = 0;
    bool cursorVisible_ = false;
    bool cursorReady_ = false;
};
