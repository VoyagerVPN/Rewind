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
#include "TransportStream.h"
static void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    try{
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
        dx(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
        ComPtr<ID3D10Multithread> threaded;dx(context.As(&threaded));threaded->SetMultithreadProtected(TRUE);
        LARGE_INTEGER hz;QueryPerformanceFrequency(&hz);int64_t epoch=(int64_t)(tick()*10000000.0/hz.QuadPart);
        auto image=[&](unsigned char y){std::vector<unsigned char> pixels(256*256*3/2,128);std::fill(pixels.begin(),pixels.begin()+256*256,y);
            D3D11_TEXTURE2D_DESC desc={};desc.Width=desc.Height=256;desc.ArraySize=desc.MipLevels=1;desc.Format=DXGI_FORMAT_NV12;desc.SampleDesc.Count=1;
            D3D11_SUBRESOURCE_DATA data={pixels.data(),256,0};ComPtr<ID3D11Texture2D> texture;dx(device->CreateTexture2D(&desc,&data,&texture));return texture;};
        auto white=image(235),black=image(16);TransportStream transport;std::vector<ImageTiming> timings;
        std::mutex gate;std::condition_variable changed;bool entered=false,release=false;
        std::atomic<int> delivered=0;
        Encoder encoder(device.Get(),256,256,120,(double)hz.QuadPart,
            [&](const unsigned char* bytes,size_t size,uint64_t pts,bool key,const std::atomic<bool>& cancelled){
                {std::unique_lock<std::mutex> lock(gate);entered=true;changed.notify_all();while(!release&&!cancelled)changed.wait_for(lock,std::chrono::milliseconds(5));}
                Sleep(15);transport.frame(bytes,size,pts,key,&cancelled);delivered++;
            },true,epoch,
            [&](ImageTiming value){timings.push_back(value);});
        std::fprintf(stderr,"integration: encoder initialized\n");
        ImageStamp selected{7,epoch-200000,epoch-150000,epoch-100000};
        for(uint64_t i=0;i<6;i++)encoder.submitPrepared(context.Get(),i,120,i*10000000/120,i==0,[&](ID3D11Texture2D* destination){context->CopyResource(destination,white.Get());return selected;});
        std::atomic<bool> inputsIdle=false;std::exception_ptr idleFailure;
        std::thread waiter([&]{try{encoder.waitInputsIdle();inputsIdle=true;}catch(...){idleFailure=std::current_exception();}changed.notify_all();});
        std::atomic<bool> outputIdle=false;std::exception_ptr drainFailure;
        std::thread drainWaiter([&]{try{encoder.waitIdle();outputIdle=true;}catch(...){drainFailure=std::current_exception();}changed.notify_all();});
        bool independent=false,drainBlocked=false;
        {std::unique_lock<std::mutex> lock(gate);changed.wait_for(lock,std::chrono::seconds(2),[&]{return entered;});
            independent=changed.wait_for(lock,std::chrono::milliseconds(200),[&]{return inputsIdle.load();});
            drainBlocked=!changed.wait_for(lock,std::chrono::milliseconds(50),[&]{return outputIdle.load();});release=true;}
        changed.notify_all();waiter.join();drainWaiter.join();if(idleFailure)std::rethrow_exception(idleFailure);if(drainFailure)std::rethrow_exception(drainFailure);
        require(independent,"FAIL: slow transport retained completed NVENC input slots");
        require(drainBlocked&&outputIdle,"FAIL: warmup drain completed before receiver consumed packets");
        selected={8,epoch-50000,epoch-40000,epoch-30000};
        for(uint64_t i=6;i<12;i++)encoder.submitPrepared(context.Get(),i,120,i*10000000/120,false,[&](ID3D11Texture2D* destination){context->CopyResource(destination,black.Get());return selected;});
        std::fprintf(stderr,"integration: frames submitted\n");encoder.finish();std::fprintf(stderr,"integration: encoder finished\n");
        require(delivered==12,"FAIL: encoder finish returned before compressed packet tail");
        require(timings.size()==12,"FAIL: selected source stamps not associated with output PTS");
        for(size_t i=0;i<timings.size();i++)require(timings[i].pts==i*10000000/120&&timings[i].sequence==(i<6?7U:8U),"FAIL: output selected stamp or PTS changed");
        require(encoder.imageTimings().count==12,"FAIL: output timing aggregates missing");
        std::fprintf(stderr,"{\"frames\":12,\"selectedStampPerPts\":true,\"inputsReleasedBeforeTransport\":true}\n");return 0;
    }catch(const std::exception& error){std::fprintf(stderr,"%s\n",error.what());return 1;}
}
