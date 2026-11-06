#pragma once
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <cstdio>

class OverlayCompatibility {
    using SwapChain = Microsoft::WRL::ComPtr<IDXGISwapChain1>;
    HWND window=nullptr;
    SwapChain swapChain;
    HRESULT status=S_FALSE;
    bool enabled=false;

    static HRESULT lastWindowsError() noexcept {
        DWORD error=GetLastError();return HRESULT_FROM_WIN32(error?error:ERROR_GEN_FAILURE);
    }
    static HRESULT windowClassStatus() noexcept {
        // Регистрация общая для процесса; каждое окно принадлежит своему экземпляру.
        static const HRESULT registration=[]() noexcept {
            WNDCLASSW value={};value.lpfnWndProc=DefWindowProcW;value.hInstance=GetModuleHandleW(nullptr);
            value.lpszClassName=L"RewindCaptureOverlayCompatibility";
            return RegisterClassW(&value)?S_OK:lastWindowsError();
        }();
        return registration;
    }
    static HRESULT createOnPlatform(IDXGIFactory2* factory,ID3D11Device* device,HWND target,
        const DXGI_SWAP_CHAIN_DESC1* description,IDXGISwapChain1** result) noexcept {
        return factory->CreateSwapChainForHwnd(device,target,description,nullptr,nullptr,result);
    }
    void release() noexcept {
        swapChain.Reset();
        if(window){if(IsWindow(window))DestroyWindow(window);window=nullptr;}
    }
    void log() const noexcept {
        std::fprintf(stderr,"{\"overlayCompatibility\":true,\"enabled\":%s,\"nvidiaHook\":%s,\"hiddenSwapChain\":%s,\"hresult\":%lu,\"presentCalls\":0}\n",
            enabled?"true":"false",GetModuleHandleW(L"nvspcap64.dll")?"true":"false",active()?"true":"false",(unsigned long)status);
    }
    HRESULT initialize(ID3D11Device* device,IDXGIFactory1* factory,
        HRESULT (*create)(IDXGIFactory2*,ID3D11Device*,HWND,const DXGI_SWAP_CHAIN_DESC1*,IDXGISwapChain1**)) noexcept {
        if(!device||!factory||!create)return E_POINTER;
        Microsoft::WRL::ComPtr<IDXGIFactory2> factory2;
        HRESULT value=factory->QueryInterface(IID_PPV_ARGS(&factory2));if(FAILED(value))return value;
        value=windowClassStatus();if(FAILED(value))return value;
        window=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,L"RewindCaptureOverlayCompatibility",
            L"Rewind Screen Capture",WS_POPUP,0,0,1,1,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        if(!window)return lastWindowsError();
        DXGI_SWAP_CHAIN_DESC1 description={};description.Width=description.Height=1;
        description.Format=DXGI_FORMAT_B8G8R8A8_UNORM;description.SampleDesc.Count=1;
        description.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;description.BufferCount=2;
        description.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;description.Scaling=DXGI_SCALING_STRETCH;
        value=create(factory2.Get(),device,window,&description,swapChain.GetAddressOf());if(FAILED(value))return value;
        return factory2->MakeWindowAssociation(window,DXGI_MWA_NO_ALT_ENTER|DXGI_MWA_NO_WINDOW_CHANGES);
    }
public:
    using CreateSwapChain = HRESULT (*)(IDXGIFactory2*,ID3D11Device*,HWND,const DXGI_SWAP_CHAIN_DESC1*,IDXGISwapChain1**);
    OverlayCompatibility(ID3D11Device* device,IDXGIFactory1* factory,bool requested,CreateSwapChain create=createOnPlatform) noexcept:enabled(requested) {
        // NvSPCAP зависает на offscreen-устройстве без swap chain. Это окно никогда не показывается;
        // Present не вызывается. Отказ вспомогательного DXGI-ресурса не останавливает запись.
        if(enabled){status=initialize(device,factory,create);if(FAILED(status))release();}
        log();
    }
    OverlayCompatibility(const OverlayCompatibility&)=delete;
    OverlayCompatibility& operator=(const OverlayCompatibility&)=delete;
    ~OverlayCompatibility(){release();}
    bool active() const noexcept { return window&&swapChain; }
    HRESULT result() const noexcept { return status; }
    bool pump() noexcept {
        if(!active())return true;
        // Окно и очередь обслуживает тот же encoder clock, который создал ресурс.
        MSG message;while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){
            if(message.message==WM_QUIT)return false;
            TranslateMessage(&message);DispatchMessageW(&message);
        }
        if(!IsWindow(window)){status=HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE);release();log();}
        return true;
    }
};
