#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <wrl/client.h>
#include "nvEncodeAPI.h"
#include <cstdio>
#include <vector>
#include <deque>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <algorithm>
#include <stdexcept>
#include <string>
#include <atomic>
#include <memory>
using Microsoft::WRL::ComPtr;
static void dx(HRESULT hr){if(FAILED(hr))throw std::runtime_error("DirectX="+std::to_string((unsigned long)hr));}
static void nv(NVENCSTATUS status){if(status!=NV_ENC_SUCCESS)throw std::runtime_error("NVENC="+std::to_string(status));}
static long long tick(){LARGE_INTEGER value;QueryPerformanceCounter(&value);return value.QuadPart;}
#include "NvEncoder.h"
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    try{
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
        dx(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
        ComPtr<ID3D10Multithread> threaded;dx(context.As(&threaded));threaded->SetMultithreadProtected(TRUE);
        LARGE_INTEGER hz;QueryPerformanceFrequency(&hz);
        D3D11_TEXTURE2D_DESC desc={};desc.Width=desc.Height=256;desc.ArraySize=desc.MipLevels=1;desc.Format=DXGI_FORMAT_NV12;desc.SampleDesc.Count=1;
        std::vector<unsigned char> pixels(256*256*3/2,128);D3D11_SUBRESOURCE_DATA data={pixels.data(),256,0};ComPtr<ID3D11Texture2D> image;dx(device->CreateTexture2D(&desc,&data,&image));
        {
            std::atomic<int> received=0;
            Encoder warmup(device.Get(),256,256,120,(double)hz.QuadPart,
                [&](const unsigned char*,size_t,uint64_t,bool,const std::atomic<bool>&){received++;},true);
            for(int i=0;i<256;i++){
                warmup.submit(context.Get(),image.Get(),i,120);warmup.waitIdle();
                if(received!=i+1)throw std::runtime_error("FAIL: warmup returned between input release and packet publication");
            }
            warmup.finish();
        }
        auto before=std::chrono::steady_clock::now();bool propagated=false;
        {
            Encoder encoder(device.Get(),256,256,120,(double)hz.QuadPart,
                [](const unsigned char*,size_t,uint64_t,bool,const std::atomic<bool>&){throw std::runtime_error("Injected packet receiver failure");},true);
            try{for(int i=0;i<24;i++)encoder.submit(context.Get(),image.Get(),i,120);encoder.finish();}
            catch(const std::runtime_error& error){propagated=std::string(error.what())=="Injected packet receiver failure";}
        }
        if(!propagated||std::chrono::steady_clock::now()-before>std::chrono::seconds(5))throw std::runtime_error("FAIL: NVENC transport error lost or cleanup stalled");
        std::fprintf(stderr,"{\"warmupDrains\":256,\"receiverFailurePropagated\":true,\"cleanupCompleted\":true}\n");return 0;
    }catch(const std::exception& error){std::fprintf(stderr,"%s\n",error.what());return 1;}
}
