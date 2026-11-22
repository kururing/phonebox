#include "d3d_duplicator.h"
#include <d3dcompiler.h>
#include <cstdio>
#include <cstring>
#include <algorithm>

static std::string HrStr(const char* what, HRESULT hr) {
    char buf[192];
    std::snprintf(buf, sizeof(buf), "%s failed (HRESULT 0x%08X)", what, static_cast<unsigned>(hr));
    return buf;
}

static const char* kCursorHlsl = R"(
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VSOut VSMain(uint id : SV_VertexID) {
    float2 uv = float2((id << 1) & 2, id & 2);
    VSOut o;
    o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    o.uv = uv;
    return o;
}
Texture2D tex0 : register(t0);
SamplerState smp0 : register(s0);
float4 PSMain(VSOut i) : SV_TARGET {
    return tex0.Sample(smp0, i.uv);
}
)";

static const char* kScaleHlsl = R"(
cbuffer ScaleParams : register(b0) { float4 offUv; };
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VSOut VSMain(uint id : SV_VertexID) {
    float2 uv = float2((id << 1) & 2, id & 2);
    VSOut o;
    o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    o.uv = uv;
    return o;
}
Texture2D tex0 : register(t0);
SamplerState smp0 : register(s0);
float4 PSMain(VSOut i) : SV_TARGET {
    float2 d = offUv.xy;
    float4 c = tex0.Sample(smp0, i.uv + float2(-d.x, -d.y));
    c += tex0.Sample(smp0, i.uv + float2( d.x, -d.y));
    c += tex0.Sample(smp0, i.uv + float2(-d.x,  d.y));
    c += tex0.Sample(smp0, i.uv + float2( d.x,  d.y));
    return c * 0.25;
}
)";

bool D3dFrameSource::Init(uint32_t adapterIndex, uint32_t outputIndex, bool synthetic,
                          uint32_t synthW, uint32_t synthH, bool drawCursor,
                          uint32_t targetEncW, uint32_t targetEncH, std::string& err) {
    adapterIndex_ = adapterIndex;
    outputIndex_ = outputIndex;
    synthetic_ = synthetic;
    drawCursor_ = drawCursor;
    reqEncodeW_ = targetEncW;
    reqEncodeH_ = targetEncH;
    encodeW_ = 0;
    encodeH_ = 0;

    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory_));
    if (FAILED(hr)) { err = HrStr("CreateDXGIFactory1", hr); return false; }

    ComPtr<IDXGIAdapter1> chosen;
    ComPtr<IDXGIAdapter1> nvidia;
    for (UINT i = 0;; ++i) {
        ComPtr<IDXGIAdapter1> cand;
        if (factory_->EnumAdapters1(i, &cand) == DXGI_ERROR_NOT_FOUND) break;
        DXGI_ADAPTER_DESC1 desc{};
        cand->GetDesc1(&desc);
        if (!nvidia && desc.VendorId == 0x10DE) {
            nvidia = cand;
            if (adapterIndex_ == 0xFFFFFFFFu) {
                chosen = cand;
                adapterIndex_ = i;
            }
        }
        if (adapterIndex_ != 0xFFFFFFFFu && i == adapterIndex_) {
            chosen = cand;
        }
    }

    if (!nvidia) { err = "No NVIDIA DXGI adapter (VendorId 0x10DE) found"; return false; }
    if (!chosen) { err = "Selected DXGI adapter index not found"; return false; }

    DXGI_ADAPTER_DESC1 chosenDesc{};
    chosen->GetDesc1(&chosenDesc);
    crossAdapter_ = (chosenDesc.VendorId != 0x10DE);
    nvidia.As(&adapter_);

    D3D_FEATURE_LEVEL flOut{};
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    ComPtr<ID3D11DeviceContext> ctx;
    hr = D3D11CreateDevice(
        nvidia.Get(),
        D3D_DRIVER_TYPE_UNKNOWN,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        levels, 2,
        D3D11_SDK_VERSION,
        &device_,
        &flOut,
        &ctx);
    if (FAILED(hr)) { err = HrStr("D3D11CreateDevice(NVENC)", hr); return false; }
    hr = ctx.As(&context_);
    if (FAILED(hr)) { err = HrStr("QueryInterface ID3D11DeviceContext1", hr); return false; }

    if (crossAdapter_) {
        ComPtr<ID3D11DeviceContext> capCtx;
        hr = D3D11CreateDevice(
            chosen.Get(),
            D3D_DRIVER_TYPE_UNKNOWN,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, 2,
            D3D11_SDK_VERSION,
            &capDevice_,
            &flOut,
            &capCtx);
        if (FAILED(hr)) { err = HrStr("D3D11CreateDevice(Capture)", hr); return false; }
        hr = capCtx.As(&capContext_);
        if (FAILED(hr)) { err = HrStr("QueryInterface ID3D11DeviceContext1(Capture)", hr); return false; }
    } else {
        capDevice_ = device_;
        capContext_ = context_;
    }

    ComPtr<ID3D10Multithread> mt;
    if (SUCCEEDED(device_.As(&mt))) mt->SetMultithreadProtected(TRUE);

    ComPtr<IDXGIDevice1> dxgiDev;
    if (SUCCEEDED(device_.As(&dxgiDev))) {
        dxgiDev->SetMaximumFrameLatency(1);
        dxgiDev->SetGPUThreadPriority(5); // best-effort; ignored if non-elevated
    }

    if (synthetic_) {
        width_ = synthW ? synthW : 1920;
        height_ = synthH ? synthH : 1080;
        return SetEncodeSize(reqEncodeW_, reqEncodeH_, err);
    }

    ComPtr<IDXGIOutput> out0;
    hr = chosen->EnumOutputs(outputIndex_, &out0);
    if (FAILED(hr)) { err = HrStr("EnumOutputs", hr); return false; }
    hr = out0.As(&output_);
    if (FAILED(hr)) { err = HrStr("QueryInterface IDXGIOutput1", hr); return false; }

    if (!InitDuplication(err)) return false;
    if (drawCursor_) {
        cursorReady_ = InitCursorPipeline(err);
        if (!cursorReady_) err.clear(); // non-fatal
    }
    return true;
}

bool D3dFrameSource::InitDuplication(std::string& err) {
    dupl_.Reset();
    DXGI_OUTPUT_DESC od{};
    output_->GetDesc(&od);
    uint32_t w = static_cast<uint32_t>(od.DesktopCoordinates.right - od.DesktopCoordinates.left);
    uint32_t h = static_cast<uint32_t>(od.DesktopCoordinates.bottom - od.DesktopCoordinates.top);
    if (w == 0 || h == 0) { err = "Selected DXGI output has 0 dimensions"; return false; }

    HRESULT hr = E_FAIL;
    ComPtr<IDXGIOutput5> out5;
    if (SUCCEEDED(output_.As(&out5))) {
        const DXGI_FORMAT fmts[] = { DXGI_FORMAT_B8G8R8A8_UNORM };
        hr = out5->DuplicateOutput1(capDevice_.Get(), 0, 1, fmts, &dupl_);
    }
    if (FAILED(hr)) {
        hr = output_->DuplicateOutput(capDevice_.Get(), &dupl_);
    }
    if (FAILED(hr)) {
        if (hr == DXGI_ERROR_UNSUPPORTED || crossAdapter_) {
            if (!InitGdiFallback(od.DesktopCoordinates.left, od.DesktopCoordinates.top, w, h, err)) return false;
            if (w != width_ || h != height_ || !ring_[0]) {
                width_ = w;
                height_ = h;
                encodeW_ = 0;
                encodeH_ = 0;
                if (!SetEncodeSize(reqEncodeW_, reqEncodeH_, err)) return false;
            }
            return true;
        }
        err = HrStr("IDXGIOutput::DuplicateOutput", hr);
        return false;
    }

    if (w != width_ || h != height_ || !ring_[0]) {
        width_ = w;
        height_ = h;
        encodeW_ = 0;
        encodeH_ = 0;
        if (!SetEncodeSize(reqEncodeW_, reqEncodeH_, err)) return false;
    }
    return true;
}

bool D3dFrameSource::SetEncodeSize(uint32_t targetEncW, uint32_t targetEncH, std::string& err) {
    if (width_ == 0 || height_ == 0) {
        reqEncodeW_ = targetEncW;
        reqEncodeH_ = targetEncH;
        return true;
    }

    uint32_t newW = width_;
    uint32_t newH = height_;
    if (targetEncW > 0 && targetEncH > 0) {
        newW = std::min(targetEncW, width_);
        newH = std::min(targetEncH, height_);
    }
    newW = std::max(64u, newW & ~1u);
    newH = std::max(64u, newH & ~1u);
    if (newW > width_) newW = width_ & ~1u;
    if (newH > height_) newH = height_ & ~1u;
    newW = std::max(64u, newW);
    newH = std::max(64u, newH);

    reqEncodeW_ = targetEncW;
    reqEncodeH_ = targetEncH;

    if (newW == encodeW_ && newH == encodeH_ && ring_[0]) {
        if (NeedsScale()) {
            if (!EnsureFullDesktopTexture(err)) return false;
            if (!scaleReady_ && !InitScalePipeline(err)) return false;
            UpdateScaleConstants();
        } else {
            fullDesktopTex_.Reset();
            fullDesktopRtv_.Reset();
            fullDesktopSrv_.Reset();
        }
        return true;
    }

    encodeW_ = newW;
    encodeH_ = newH;
    if (!CreateRingTextures(encodeW_, encodeH_, err)) return false;

    if (NeedsScale()) {
        if (!EnsureFullDesktopTexture(err)) return false;
        if (!scaleReady_ && !InitScalePipeline(err)) return false;
        UpdateScaleConstants();
    } else {
        fullDesktopTex_.Reset();
        fullDesktopRtv_.Reset();
        fullDesktopSrv_.Reset();
    }
    return true;
}

bool D3dFrameSource::EnsureFullDesktopTexture(std::string& err) {
    if (fullDesktopTex_) {
        D3D11_TEXTURE2D_DESC cur{};
        fullDesktopTex_->GetDesc(&cur);
        if (cur.Width == width_ && cur.Height == height_ && fullDesktopRtv_ && fullDesktopSrv_) {
            return true;
        }
    }
    fullDesktopTex_.Reset();
    fullDesktopRtv_.Reset();
    fullDesktopSrv_.Reset();

    D3D11_TEXTURE2D_DESC td{};
    td.Width = width_;
    td.Height = height_;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;

    HRESULT hr = device_->CreateTexture2D(&td, nullptr, &fullDesktopTex_);
    if (FAILED(hr)) { err = HrStr("CreateTexture2D(fullDesktop)", hr); return false; }
    hr = device_->CreateRenderTargetView(fullDesktopTex_.Get(), nullptr, &fullDesktopRtv_);
    if (FAILED(hr)) { err = HrStr("CreateRenderTargetView(fullDesktop)", hr); return false; }
    hr = device_->CreateShaderResourceView(fullDesktopTex_.Get(), nullptr, &fullDesktopSrv_);
    if (FAILED(hr)) { err = HrStr("CreateShaderResourceView(fullDesktop)", hr); return false; }
    return true;
}

bool D3dFrameSource::EnsureCapStagingTexture(std::string& err) {
    if (capStaging_) {
        D3D11_TEXTURE2D_DESC cur{};
        capStaging_->GetDesc(&cur);
        if (cur.Width == width_ && cur.Height == height_) return true;
    }
    capStaging_.Reset();
    D3D11_TEXTURE2D_DESC td{};
    td.Width = width_;
    td.Height = height_;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_STAGING;
    td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    HRESULT hr = capDevice_->CreateTexture2D(&td, nullptr, &capStaging_);
    if (FAILED(hr)) { err = HrStr("CreateTexture2D(capStaging)", hr); return false; }
    return true;
}

bool D3dFrameSource::InitGdiFallback(int left, int top, uint32_t w, uint32_t h, std::string& err) {
    ReleaseGdiFallback();
    HDC tmpDc = GetDC(nullptr);
    if (!tmpDc) { err = "GetDC(nullptr) failed"; return false; }
    gdiMemDc_ = CreateCompatibleDC(tmpDc);
    if (!gdiMemDc_) { ReleaseDC(nullptr, tmpDc); ReleaseGdiFallback(); err = "CreateCompatibleDC failed"; return false; }
    BITMAPINFO bmi{};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = static_cast<LONG>(w);
    bmi.bmiHeader.biHeight = -static_cast<LONG>(h); // top-down BGRA
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;
    gdiBitmap_ = CreateDIBSection(gdiMemDc_, &bmi, DIB_RGB_COLORS, &gdiBits_, nullptr, 0);
    ReleaseDC(nullptr, tmpDc);
    if (!gdiBitmap_ || !gdiBits_) { ReleaseGdiFallback(); err = "CreateDIBSection failed"; return false; }
    gdiOldObj_ = SelectObject(gdiMemDc_, gdiBitmap_);
    gdiLeft_ = left;
    gdiTop_ = top;
    gdiFallback_ = true;
    return true;
}

void D3dFrameSource::ReleaseGdiFallback() {
    gdiFallback_ = false;
    if (gdiMemDc_ && gdiOldObj_) { SelectObject(gdiMemDc_, gdiOldObj_); gdiOldObj_ = nullptr; }
    if (gdiBitmap_) { DeleteObject(gdiBitmap_); gdiBitmap_ = nullptr; }
    if (gdiMemDc_) { DeleteDC(gdiMemDc_); gdiMemDc_ = nullptr; }
    if (gdiScreenDc_) { ReleaseDC(nullptr, gdiScreenDc_); gdiScreenDc_ = nullptr; }
    gdiBits_ = nullptr;
}

bool D3dFrameSource::InitScalePipeline(std::string& err) {
    if (scaleReady_) return true;
    ComPtr<ID3DBlob> vsBlob, psBlob, errBlob;
    HRESULT hr = D3DCompile(kScaleHlsl, std::strlen(kScaleHlsl), "scale", nullptr, nullptr,
                            "VSMain", "vs_5_0", 0, 0, &vsBlob, &errBlob);
    if (FAILED(hr)) { err = HrStr("D3DCompile scale VS", hr); return false; }
    hr = D3DCompile(kScaleHlsl, std::strlen(kScaleHlsl), "scale", nullptr, nullptr,
                    "PSMain", "ps_5_0", 0, 0, &psBlob, &errBlob);
    if (FAILED(hr)) { err = HrStr("D3DCompile scale PS", hr); return false; }

    hr = device_->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &scaleVs_);
    if (FAILED(hr)) { err = HrStr("CreateVertexShader(scale)", hr); return false; }
    hr = device_->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &scalePs_);
    if (FAILED(hr)) { err = HrStr("CreatePixelShader(scale)", hr); return false; }

    D3D11_SAMPLER_DESC sd{};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    hr = device_->CreateSamplerState(&sd, &scaleSampler_);
    if (FAILED(hr)) { err = HrStr("CreateSamplerState(scale)", hr); return false; }

    D3D11_BUFFER_DESC cbd{};
    cbd.ByteWidth = 16;
    cbd.Usage = D3D11_USAGE_DEFAULT;
    cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    hr = device_->CreateBuffer(&cbd, nullptr, &scaleCb_);
    if (FAILED(hr)) { err = HrStr("CreateBuffer(scaleCb)", hr); return false; }

    scaleReady_ = true;
    return true;
}

void D3dFrameSource::UpdateScaleConstants() {
    if (!scaleCb_ || encodeW_ == 0 || encodeH_ == 0 || width_ == 0 || height_ == 0) return;
    float rx = static_cast<float>(width_) / static_cast<float>(encodeW_);
    float ry = static_cast<float>(height_) / static_cast<float>(encodeH_);
    float offX = std::clamp((rx - 1.0f) * 0.5f, 0.0f, 0.85f) / static_cast<float>(width_);
    float offY = std::clamp((ry - 1.0f) * 0.5f, 0.0f, 0.85f) / static_cast<float>(height_);
    float params[4] = { offX, offY, 0.0f, 0.0f };
    context_->UpdateSubresource(scaleCb_.Get(), 0, nullptr, params, 0, 0);
}

void D3dFrameSource::ScaleFullToSlot(uint32_t slot) {
    if (!scaleReady_ || !fullDesktopSrv_ || !ringRtv_[slot]) return;

    ID3D11RenderTargetView* rtv = ringRtv_[slot].Get();
    context_->OMSetRenderTargets(1, &rtv, nullptr);
    const float blendFactor[4] = { 0, 0, 0, 0 };
    context_->OMSetBlendState(nullptr, blendFactor, 0xFFFFFFFFu);

    D3D11_VIEWPORT vp{};
    vp.TopLeftX = 0.0f;
    vp.TopLeftY = 0.0f;
    vp.Width = static_cast<float>(encodeW_);
    vp.Height = static_cast<float>(encodeH_);
    vp.MinDepth = 0.0f;
    vp.MaxDepth = 1.0f;
    context_->RSSetViewports(1, &vp);

    context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context_->IASetInputLayout(nullptr);
    context_->VSSetShader(scaleVs_.Get(), nullptr, 0);
    context_->PSSetShader(scalePs_.Get(), nullptr, 0);
    ID3D11Buffer* cb = scaleCb_.Get();
    ID3D11ShaderResourceView* srv = fullDesktopSrv_.Get();
    ID3D11SamplerState* smp = scaleSampler_.Get();
    context_->PSSetConstantBuffers(0, 1, &cb);
    context_->PSSetShaderResources(0, 1, &srv);
    context_->PSSetSamplers(0, 1, &smp);
    context_->Draw(3, 0);

    ID3D11ShaderResourceView* nullSrv = nullptr;
    ID3D11RenderTargetView* nullRtv = nullptr;
    context_->PSSetShaderResources(0, 1, &nullSrv);
    context_->OMSetRenderTargets(1, &nullRtv, nullptr);
}

bool D3dFrameSource::ReacquireDuplication(std::string& err) {
    if (synthetic_) return true;
    return InitDuplication(err);
}

bool D3dFrameSource::CreateRingTextures(uint32_t w, uint32_t h, std::string& err) {
    D3D11_TEXTURE2D_DESC td{};
    td.Width = w;
    td.Height = h;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;

    for (uint32_t i = 0; i < kRingSize; ++i) {
        ring_[i].Reset();
        ringRtv_[i].Reset();
        HRESULT hr = device_->CreateTexture2D(&td, nullptr, &ring_[i]);
        if (FAILED(hr)) { err = HrStr("CreateTexture2D(ring)", hr); return false; }
        hr = device_->CreateRenderTargetView(ring_[i].Get(), nullptr, &ringRtv_[i]);
        if (FAILED(hr)) { err = HrStr("CreateRenderTargetView(ring)", hr); return false; }
    }
    return true;
}

bool D3dFrameSource::InitCursorPipeline(std::string& err) {
    ComPtr<ID3DBlob> vsBlob, psBlob, errBlob;
    HRESULT hr = D3DCompile(kCursorHlsl, std::strlen(kCursorHlsl), "cursor", nullptr, nullptr,
                            "VSMain", "vs_5_0", 0, 0, &vsBlob, &errBlob);
    if (FAILED(hr)) { err = HrStr("D3DCompile VS", hr); return false; }
    hr = D3DCompile(kCursorHlsl, std::strlen(kCursorHlsl), "cursor", nullptr, nullptr,
                    "PSMain", "ps_5_0", 0, 0, &psBlob, &errBlob);
    if (FAILED(hr)) { err = HrStr("D3DCompile PS", hr); return false; }

    hr = device_->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &cursorVs_);
    if (FAILED(hr)) return false;
    hr = device_->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &cursorPs_);
    if (FAILED(hr)) return false;

    D3D11_SAMPLER_DESC sd{};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    if (FAILED(device_->CreateSamplerState(&sd, &cursorSampler_))) return false;

    D3D11_BLEND_DESC bd{};
    bd.RenderTarget[0].BlendEnable = TRUE;
    bd.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
    bd.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    bd.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
    bd.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
    bd.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    bd.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
    bd.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    return SUCCEEDED(device_->CreateBlendState(&bd, &cursorBlend_));
}

void D3dFrameSource::UpdateCursorShape(const DXGI_OUTDUPL_FRAME_INFO& info) {
    if (info.LastMouseUpdateTime.QuadPart != 0) {
        cursorX_ = info.PointerPosition.Position.x;
        cursorY_ = info.PointerPosition.Position.y;
        cursorVisible_ = info.PointerPosition.Visible != FALSE;
    }
    if (info.PointerShapeBufferSize == 0) return;

    cursorRawBuf_.resize(info.PointerShapeBufferSize);
    UINT req = 0;
    DXGI_OUTDUPL_POINTER_SHAPE_INFO psi{};
    if (FAILED(dupl_->GetFramePointerShape(info.PointerShapeBufferSize, cursorRawBuf_.data(), &req, &psi)))
        return;

    uint32_t w = psi.Width;
    uint32_t h = (psi.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME) ? (psi.Height / 2) : psi.Height;
    if (w == 0 || h == 0) return;

    cursorBgra_.assign(static_cast<size_t>(w) * h, 0);
    if (psi.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR) {
        for (uint32_t y = 0; y < h; ++y) {
            std::memcpy(&cursorBgra_[y * w], cursorRawBuf_.data() + y * psi.Pitch, w * 4);
        }
    } else if (psi.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR) {
        for (uint32_t y = 0; y < h; ++y) {
            const uint32_t* src = reinterpret_cast<const uint32_t*>(cursorRawBuf_.data() + y * psi.Pitch);
            for (uint32_t x = 0; x < w; ++x) {
                uint32_t px = src[x];
                cursorBgra_[y * w + x] = (px & 0xFF000000u) ? ((px & 0x00FFFFFFu) | 0x80000000u)
                                                             : ((px & 0x00FFFFFFu) | 0xFF000000u);
            }
        }
    } else if (psi.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME) {
        for (uint32_t y = 0; y < h; ++y) {
            const uint8_t* andRow = cursorRawBuf_.data() + y * psi.Pitch;
            const uint8_t* xorRow = cursorRawBuf_.data() + (y + h) * psi.Pitch;
            for (uint32_t x = 0; x < w; ++x) {
                uint8_t mask = static_cast<uint8_t>(0x80u >> (x & 7));
                bool a = (andRow[x >> 3] & mask) != 0;
                bool xo = (xorRow[x >> 3] & mask) != 0;
                if (!a && !xo)      cursorBgra_[y * w + x] = 0xFF000000u; // black
                else if (!a && xo)  cursorBgra_[y * w + x] = 0xFFFFFFFFu; // white
                else if (a && xo)   cursorBgra_[y * w + x] = 0xD0FFFFFFu; // inverted approx
                else                cursorBgra_[y * w + x] = 0x00000000u; // transparent
            }
        }
    }

    D3D11_TEXTURE2D_DESC td{};
    td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_IMMUTABLE; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA init{ cursorBgra_.data(), w * 4, 0 };
    cursorTex_.Reset();
    cursorSrv_.Reset();
    if (SUCCEEDED(device_->CreateTexture2D(&td, &init, &cursorTex_)) &&
        SUCCEEDED(device_->CreateShaderResourceView(cursorTex_.Get(), nullptr, &cursorSrv_))) {
        cursorW_ = w;
        cursorH_ = h;
    }
}

void D3dFrameSource::DrawCursorToRtv(ID3D11RenderTargetView* rtv) {
    if (!cursorReady_ || !cursorVisible_ || !cursorSrv_ || cursorW_ == 0 || cursorH_ == 0 || !rtv) return;

    context_->OMSetRenderTargets(1, &rtv, nullptr);
    const float blendFactor[4] = { 0, 0, 0, 0 };
    context_->OMSetBlendState(cursorBlend_.Get(), blendFactor, 0xFFFFFFFFu);

    D3D11_VIEWPORT vp{};
    vp.TopLeftX = static_cast<float>(cursorX_);
    vp.TopLeftY = static_cast<float>(cursorY_);
    vp.Width = static_cast<float>(cursorW_);
    vp.Height = static_cast<float>(cursorH_);
    vp.MinDepth = 0.0f;
    vp.MaxDepth = 1.0f;
    context_->RSSetViewports(1, &vp);

    context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context_->IASetInputLayout(nullptr);
    context_->VSSetShader(cursorVs_.Get(), nullptr, 0);
    context_->PSSetShader(cursorPs_.Get(), nullptr, 0);
    ID3D11ShaderResourceView* srv = cursorSrv_.Get();
    ID3D11SamplerState* smp = cursorSampler_.Get();
    context_->PSSetShaderResources(0, 1, &srv);
    context_->PSSetSamplers(0, 1, &smp);
    context_->Draw(3, 0);

    ID3D11ShaderResourceView* nullSrv = nullptr;
    ID3D11RenderTargetView* nullRtv = nullptr;
    context_->PSSetShaderResources(0, 1, &nullSrv);
    context_->OMSetRenderTargets(1, &nullRtv, nullptr);
}

void D3dFrameSource::RenderSyntheticFrame(uint32_t slot) {
    // Pure dGPU VRAM animated frame via ClearView (moving bars + background pulse)
    const bool scale = NeedsScale() && fullDesktopRtv_;
    ID3D11RenderTargetView* rtv = scale ? fullDesktopRtv_.Get() : ringRtv_[slot].Get();
    const uint32_t fw = scale ? width_ : encodeW_;
    const uint32_t fh = scale ? height_ : encodeH_;

    uint64_t f = synthFrameIdx_++;
    float bg[4] = { 0.07f, 0.09f, 0.14f, 1.0f };
    context_->ClearRenderTargetView(rtv, bg);

    const uint32_t barW = std::max(8u, fw / 8);
    const uint32_t x = static_cast<uint32_t>((f * 17) % std::max(1u, fw - barW));
    const uint32_t y = static_cast<uint32_t>((f * 9) % std::max(1u, fh / 2));
    D3D11_RECT r1{ static_cast<LONG>(x), 0, static_cast<LONG>(x + barW), static_cast<LONG>(fh) };
    float c1[4] = { 0.15f, 0.65f, 0.95f, 1.0f };
    context_->ClearView(rtv, c1, &r1, 1);

    const LONG marginX = static_cast<LONG>(std::min(40u, fw / 4));
    D3D11_RECT r2{ marginX, static_cast<LONG>(y), static_cast<LONG>(fw - marginX), static_cast<LONG>(std::min(fh, y + 80)) };
    float c2[4] = { 0.95f, 0.35f, 0.25f, 1.0f };
    context_->ClearView(rtv, c2, &r2, 1);

    if (scale) {
        ScaleFullToSlot(slot);
    }
}

AcquireStatus D3dFrameSource::AcquireNext(uint32_t timeoutMs, uint32_t& outSlot, bool& outResized, std::string& err) {
    outResized = false;
    uint32_t slot = nextSlot_;
    nextSlot_ = (nextSlot_ + 1) % kRingSize;

    if (synthetic_) {
        RenderSyntheticFrame(slot);
        outSlot = slot;
        return AcquireStatus::Acquired;
    }

    if (gdiFallback_) {
        if (!gdiMemDc_ || !gdiBits_) { err = "GDI capture not initialized"; return AcquireStatus::Error; }
        HDC screenDc = GetDC(nullptr);
        if (!screenDc) { err = "GetDC(nullptr) failed"; return AcquireStatus::Timeout; }
        BOOL ok = BitBlt(gdiMemDc_, 0, 0, static_cast<int>(width_), static_cast<int>(height_),
                         screenDc, gdiLeft_, gdiTop_, SRCCOPY);
        ReleaseDC(nullptr, screenDc);
        if (!ok) {
            return AcquireStatus::Timeout;
        }
        if (drawCursor_) {
            CURSORINFO ci{};
            ci.cbSize = sizeof(ci);
            if (GetCursorInfo(&ci) && (ci.flags & CURSOR_SHOWING) && ci.hCursor) {
                ICONINFO ii{};
                int hx = 0, hy = 0;
                if (GetIconInfo(ci.hCursor, &ii)) {
                    hx = static_cast<int>(ii.xHotspot);
                    hy = static_cast<int>(ii.yHotspot);
                    if (ii.hbmMask) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor) DeleteObject(ii.hbmColor);
                }
                DrawIconEx(gdiMemDc_, ci.ptScreenPos.x - gdiLeft_ - hx, ci.ptScreenPos.y - gdiTop_ - hy,
                           ci.hCursor, 0, 0, 0, nullptr, DI_NORMAL);
            }
        }
        // Fast strided hash to skip GPU upload + NVENC encode when the primary screen is static.
        const uint64_t* words = static_cast<const uint64_t*>(gdiBits_);
        const size_t totalWords = (static_cast<size_t>(width_) * height_) / 2;
        uint64_t h = 1469598103934665603ULL;
        for (size_t i = 0; i < totalWords; i += 64) {
            h ^= words[i];
            h *= 1099511628211ULL;
        }
        if (h == gdiLastHash_) {
            return AcquireStatus::Timeout;
        }
        gdiLastHash_ = h;

        const bool scale = NeedsScale() && fullDesktopTex_;
        ID3D11Texture2D* destTex = scale ? fullDesktopTex_.Get() : ring_[slot].Get();
        context_->UpdateSubresource(destTex, 0, nullptr, gdiBits_, width_ * 4, 0);
        if (scale) ScaleFullToSlot(slot);
        outSlot = slot;
        return AcquireStatus::Acquired;
    }

    if (!dupl_) { err = "Duplication not initialized"; return AcquireStatus::AccessLost; }

    DXGI_OUTDUPL_FRAME_INFO info{};
    ComPtr<IDXGIResource> res;
    HRESULT hr = dupl_->AcquireNextFrame(timeoutMs, &info, &res);
    if (hr == DXGI_ERROR_WAIT_TIMEOUT) return AcquireStatus::Timeout;
    if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_SESSION_DISCONNECTED) {
        return AcquireStatus::AccessLost;
    }
    if (FAILED(hr)) { err = HrStr("AcquireNextFrame", hr); return AcquireStatus::Error; }

    if (drawCursor_) UpdateCursorShape(info);

    bool desktopChanged = info.LastPresentTime.QuadPart != 0;
    bool cursorChanged = drawCursor_ && info.LastMouseUpdateTime.QuadPart != 0;

    if (!desktopChanged && !cursorChanged) {
        dupl_->ReleaseFrame();
        return AcquireStatus::Timeout;
    }

    ComPtr<ID3D11Texture2D> desktopTex;
    hr = res.As(&desktopTex);
    if (FAILED(hr)) {
        dupl_->ReleaseFrame();
        err = HrStr("QueryInterface ID3D11Texture2D", hr);
        return AcquireStatus::Error;
    }

    D3D11_TEXTURE2D_DESC ddesc{};
    desktopTex->GetDesc(&ddesc);
    if (ddesc.Width != width_ || ddesc.Height != height_) {
        dupl_->ReleaseFrame();
        width_ = ddesc.Width;
        height_ = ddesc.Height;
        encodeW_ = 0;
        encodeH_ = 0;
        if (!SetEncodeSize(reqEncodeW_, reqEncodeH_, err)) return AcquireStatus::Error;
        outResized = true;
        return AcquireStatus::Timeout;
    }

    const bool scale = NeedsScale() && fullDesktopTex_;
    ID3D11Texture2D* destTex = scale ? fullDesktopTex_.Get() : ring_[slot].Get();
    ID3D11RenderTargetView* destRtv = scale ? fullDesktopRtv_.Get() : ringRtv_[slot].Get();

    if (crossAdapter_) {
        if (!EnsureCapStagingTexture(err)) {
            dupl_->ReleaseFrame();
            return AcquireStatus::Error;
        }
        capContext_->CopyResource(capStaging_.Get(), desktopTex.Get());
        dupl_->ReleaseFrame();
        D3D11_MAPPED_SUBRESOURCE mapped{};
        hr = capContext_->Map(capStaging_.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr)) {
            err = HrStr("Map(capStaging)", hr);
            return AcquireStatus::Error;
        }
        context_->UpdateSubresource(destTex, 0, nullptr, mapped.pData, mapped.RowPitch, 0);
        capContext_->Unmap(capStaging_.Get(), 0);
    } else {
        context_->CopyResource(destTex, desktopTex.Get());
        dupl_->ReleaseFrame();
    }

    if (drawCursor_) DrawCursorToRtv(destRtv);
    if (scale) ScaleFullToSlot(slot);

    outSlot = slot;
    return AcquireStatus::Acquired;
}

uint64_t D3dFrameSource::QueryVramUsageBytes() const {
    if (!adapter_) return 0;
    DXGI_QUERY_VIDEO_MEMORY_INFO info{};
    if (SUCCEEDED(adapter_->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info))) {
        return info.CurrentUsage;
    }
    return 0;
}
