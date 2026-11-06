#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi1_2.h>
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
#include <chrono>
#include <memory>
#include <avrt.h>
#include <psapi.h>
using Microsoft::WRL::ComPtr;
static void dx(HRESULT hr){if(FAILED(hr))throw std::runtime_error("DirectX HRESULT="+std::to_string((unsigned long)hr));}
static void nv(NVENCSTATUS status){if(status!=NV_ENC_SUCCESS)throw std::runtime_error("NVENC status="+std::to_string(status));}
static long long tick(){LARGE_INTEGER t;QueryPerformanceCounter(&t);return t.QuadPart;}
#include "NvEncoder.h"
#include "TransportStream.h"
#include "GpuTransform.h"
#include "RenderCheck.h"
#include "GpuVideoConvert.h"
#include "DesktopCanvas.h"
#include "WindowsCanvas.h"
#include "OverlayCompatibility.h"

int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int) {
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    int argc=0;LPWSTR* args=CommandLineToArgvW(GetCommandLineW(),&argc);
    if(argc==2&&std::wstring(args[1])==L"--render-check"){LocalFree(args);return renderCheck();}
    if(argc==2&&std::wstring(args[1])==L"--nv12-check"){LocalFree(args);try{return videoConvertCheck();}catch(const std::exception& error){std::fprintf(stderr,"%s\n",error.what());return 1;}}
    if(argc!=8){if(args)LocalFree(args);return 2;}
    int adapterIndex=_wtoi(args[1]),outputIndex=_wtoi(args[2]),fps=_wtoi(args[3]),width=_wtoi(args[4]),height=_wtoi(args[5]);DWORD parentId=(DWORD)_wtoi(args[6]);long long epoch=_wtoi64(args[7]);LocalFree(args);
    if(adapterIndex<0||outputIndex<0||(fps!=30&&fps!=60&&fps!=120)||width<2||width>16384||height<2||height>1440||width%2||height%2||epoch<=0)return 2;
    HANDLE parent=OpenProcess(SYNCHRONIZE,FALSE,parentId),timer=nullptr,input=GetStdHandle(STD_INPUT_HANDLE),scheduling=nullptr;
    std::atomic<bool> stopping=false;std::thread controls;
    try {
        LARGE_INTEGER hz;QueryPerformanceFrequency(&hz);double frequency=(double)hz.QuadPart;
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        controls=std::thread([&]{char byte;DWORD count;while(ReadFile(input,&byte,1,&count,nullptr)&&count){if(byte=='q')break;}stopping=true;});
        ComPtr<IDXGIFactory1> factory;dx(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));ComPtr<IDXGIAdapter1> adapter;dx(factory->EnumAdapters1(adapterIndex,&adapter));
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
        bool nv12=GetEnvironmentVariableW(L"REWIND_CAPTURE_ARGB",nullptr,0)==0;
        dx(D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
        ComPtr<ID3D10Multithread> threaded;dx(context.As(&threaded));threaded->SetMultithreadProtected(TRUE);
        ComPtr<IDXGIOutput> output;dx(adapter->EnumOutputs(outputIndex,&output));
        std::atomic<bool> publishing=false;
        TransportStream transport;Encoder encoder(device.Get(),width,height,fps,frequency,[&](const unsigned char* bytes,size_t size,uint64_t pts,bool key,const std::atomic<bool>& cancelled){if(publishing)transport.frame(bytes,size,pts,key,&cancelled);},nv12,epoch);
        CaptureCanvas canvas(device.Get(),context.Get(),output.Get(),width,height,frequency,stopping,nv12,fps);
        OverlayCompatibility overlay(device.Get(),factory.Get(),GetModuleHandleW(L"nvspcap64.dll")!=nullptr);
        timer=CreateWaitableTimerExW(nullptr,nullptr,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS);if(!timer)throw std::runtime_error("Capture timer failed");
        SetThreadDescription(GetCurrentThread(),L"Rewind CFR encoder clock");DWORD taskIndex=0;scheduling=AvSetMmThreadCharacteristicsW(L"Capture",&taskIndex);
        auto clock100ns=[&]{return std::max(0LL,(long long)(tick()*10000000.0/frequency)-epoch);};
        bool initial=canvas.waitForImage();
        if(initial){
            // Первый GPU/IDR прогрев заканчивается до начала публичной CFR-шкалы.
            encoder.submitPrepared(context.Get(),0,fps,0,true,[&](ID3D11Texture2D* destination){canvas.copyTo(destination,true);});
            encoder.waitIdle();publishing=true;
        }
        uint64_t frameIndex=(clock100ns()*fps+9999999)/10000000;
        uint64_t firstFrameIndex=frameIndex;
        uint64_t frames=0,lateFrames=0;long long lastLog=tick();double maxLagMs=0;
        if(initial)std::fprintf(stderr,"{\"capture\":\"started\",\"seconds\":%.9f,\"firstFrame\":%llu,\"fps\":%d,\"mmcss\":%s}\n",
            (frameIndex*10000000/fps)/10000000.0,(unsigned long long)frameIndex,fps,scheduling?"true":"false");
        while(!stopping&&(!parent||WaitForSingleObject(parent,0)!=WAIT_OBJECT_0)) {
            if(!overlay.pump()){stopping=true;break;}
            uint64_t timestamp=frameIndex*10000000/fps;
            long long remaining=(long long)timestamp-clock100ns();
            if(remaining>0){LARGE_INTEGER due;due.QuadPart=-remaining;dx(SetWaitableTimer(timer,&due,0,nullptr,nullptr,FALSE)?S_OK:HRESULT_FROM_WIN32(GetLastError()));WaitForSingleObject(timer,INFINITE);}
            if(stopping)break;
            long long elapsed=clock100ns();uint64_t dueFrame=(uint64_t)elapsed*fps/10000000;
            bool late=dueFrame>frameIndex;if(late)lateFrames++;
            double lagMs=std::max(0.0,(elapsed-(long long)timestamp)/10000.0);maxLagMs=std::max(maxLagMs,lagMs);
            if(lagMs>2000)throw std::runtime_error("Capture encoder cannot keep up with the bounded CFR clock");
            // Просроченные позиции получают последнее уже отрисованное изображение; свежий кадр не переносится в прошлое.
            encoder.submitPrepared(context.Get(),frameIndex,fps,timestamp,frames==0||frameIndex%((uint64_t)fps*2)==0,
                [&](ID3D11Texture2D* destination){
                    long long preparedAt=clock100ns();bool preparedLate=(uint64_t)preparedAt*fps/10000000>frameIndex;
                    if(preparedLate&&!late){lateFrames++;late=true;}
                    lagMs=std::max(lagMs,std::max(0.0,(preparedAt-(long long)timestamp)/10000.0));maxLagMs=std::max(maxLagMs,lagMs);
                    return canvas.copyTo(destination,!preparedLate);
                });
            frames++;frameIndex++;
            long long now=tick();
            if(now-lastLog>=hz.QuadPart){auto source=canvas.snapshot();auto timing=encoder.timings();
                std::fprintf(stderr,"{\"capture\":\"progress\",\"frames\":%llu,\"fresh\":%llu,\"skipped\":%llu,\"lateFrames\":%llu,\"acquired\":%llu,\"accumulated\":%llu,\"transforms\":%llu,\"seconds\":%.6f,\"lastPtsSeconds\":%.9f,\"lagMs\":%.3f,\"maxLagMs\":%.3f,\"sourceAgeMs\":%.3f,\"acquireP95Us\":%.3f,\"queueP95Us\":%.3f,\"submitP95Us\":%.3f,\"outputP95Us\":%.3f,\"prepareP95Us\":%.3f,\"flushP95Us\":%.3f,\"mapP95Us\":%.3f,\"encodeP95Us\":%.3f,\"pending\":%zu}\n",
                    (unsigned long long)frames,(unsigned long long)source.fresh,(unsigned long long)(frameIndex-firstFrameIndex-frames),(unsigned long long)lateFrames,(unsigned long long)source.acquired,(unsigned long long)source.accumulated,(unsigned long long)source.transforms,
                    clock100ns()/10000000.0,timestamp/10000000.0,lagMs,maxLagMs,source.imageAgeMs,source.acquireP95Us,timing.queueP95Us,timing.submitP95Us,timing.outputP95Us,timing.prepareP95Us,timing.flushP95Us,timing.mapP95Us,timing.encodeP95Us,timing.pending);encoder.logOutputStages();encoder.logImageTimings();
                // Commit и общий working set различаются; резидентность может снижать сама Windows.
                PROCESS_MEMORY_COUNTERS_EX memory={};memory.cb=sizeof(memory);DWORD handles=0;
                if(K32GetProcessMemoryInfo(GetCurrentProcess(),reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory),sizeof(memory))&&GetProcessHandleCount(GetCurrentProcess(),&handles))
                    std::fprintf(stderr,"{\"captureMemory\":true,\"privateCommitBytes\":%zu,\"workingSetBytes\":%zu,\"handles\":%lu}\n",memory.PrivateUsage,memory.WorkingSetSize,(unsigned long)handles);
                lastLog=now;}
        }
        canvas.stop();encoder.finish();stopping=true;CancelSynchronousIo(controls.native_handle());if(controls.joinable())controls.join();if(scheduling)AvRevertMmThreadCharacteristics(scheduling);CloseHandle(timer);if(parent)CloseHandle(parent);return 0;
    }catch(const std::exception& error){std::fprintf(stderr,"capture-error: %s\n",error.what());stopping=true;if(controls.joinable()){CancelSynchronousIo(controls.native_handle());controls.join();}if(scheduling)AvRevertMmThreadCharacteristics(scheduling);if(timer)CloseHandle(timer);if(parent)CloseHandle(parent);return 1;}
}
