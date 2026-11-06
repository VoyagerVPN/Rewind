#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <stdexcept>
#include <cstdio>
#include "OverlayCompatibility.h"
using Microsoft::WRL::ComPtr;

static void require(bool condition,const char* message){if(!condition)throw std::runtime_error(message);}
static HWND observedWindow=nullptr;
static HRESULT observeSwapChain(IDXGIFactory2* factory,ID3D11Device* device,HWND window,const DXGI_SWAP_CHAIN_DESC1* description,IDXGISwapChain1** result) noexcept {
    observedWindow=window;
    return factory->CreateSwapChainForHwnd(device,window,description,nullptr,nullptr,result);
}
static HRESULT failSwapChain(IDXGIFactory2*,ID3D11Device*,HWND window,const DXGI_SWAP_CHAIN_DESC1*,IDXGISwapChain1**) noexcept {
    observedWindow=window;
    return IsWindow(window)?E_OUTOFMEMORY:E_INVALIDARG;
}
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    try{
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;ComPtr<IDXGIFactory1> factory;
        require(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)),"D3D11 device unavailable");
        require(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))),"DXGI factory unavailable");
        HWND created=nullptr;
        {
            OverlayCompatibility compatibility(device.Get(),factory.Get(),true,observeSwapChain);
            require(compatibility.active(),"NVIDIA compatibility endpoint was not activated");
            created=observedWindow;
            require(IsWindow(created),"Compatibility endpoint has no window");
            require(!IsWindowVisible(created),"Compatibility window became visible");
            require(GetForegroundWindow()!=created,"Compatibility window stole focus");
            LONG_PTR style=GetWindowLongPtrW(created,GWL_EXSTYLE);
            require((style&WS_EX_TOOLWINDOW)&&(style&WS_EX_NOACTIVATE),"Compatibility window can enter normal application switching");
            require(compatibility.pump(),"Compatibility endpoint stopped without a quit request");
        }
        require(!IsWindow(created),"Compatibility window survived its owner");
        std::printf("PASS compatibility_endpoint_stays_hidden_and_is_destroyed_with_its_owner\n");
        observedWindow=nullptr;
        {
            OverlayCompatibility compatibility(device.Get(),factory.Get(),false,observeSwapChain);
            require(!compatibility.active()&&!observedWindow,"Disabled compatibility allocated a DXGI window");
            require(compatibility.pump(),"Disabled compatibility stopped recording");
        }
        std::printf("PASS disabled_compatibility_does_not_allocate_an_endpoint\n");
        {
            OverlayCompatibility compatibility(device.Get(),factory.Get(),true,failSwapChain);
            require(compatibility.result()==E_OUTOFMEMORY,"DXGI failure was not preserved in diagnostics");
            require(!compatibility.active()&&observedWindow&&!IsWindow(observedWindow),"DXGI failure leaked a partially created window");
            require(compatibility.pump(),"Optional DXGI failure stopped recording");
        }
        std::printf("PASS dxgi_failure_releases_partial_resources_and_allows_recording\n");
        {
            OverlayCompatibility compatibility(device.Get(),factory.Get(),true,observeSwapChain);
            require(compatibility.active(),"Compatibility endpoint did not initialize for close check");
            require(PostMessageW(observedWindow,WM_CLOSE,0,0)!=FALSE,"Could not close the test endpoint");
            require(compatibility.pump(),"Auxiliary window close stopped recording");
            require(!compatibility.active()&&!IsWindow(observedWindow),"Closed auxiliary window retained DXGI resources");
        }
        std::printf("PASS auxiliary_window_close_does_not_stop_recording\n");
        return 0;
    }catch(const std::exception& error){std::fprintf(stderr,"FAIL: %s\n",error.what());return 1;}
}
