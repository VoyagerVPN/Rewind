#pragma once
#include "FrameTiming.h"

// DDA обновляет только последнее изображение. Очередь кодировщика не задерживает получение следующего кадра.
class DesktopCanvas {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGIOutput> output;
    ComPtr<IDXGIOutput1> output1;
    ComPtr<IDXGIOutputDuplication> duplication;
    ComPtr<ID3D11Texture2D> captureTexture,latest;
    ComPtr<ID3D11ShaderResourceView> captureView,latestView;
    DXGI_OUTPUT_DESC incomingDisplay={},display={};
    DXGI_OUTDUPL_DESC incomingInfo={},info={};
    GpuTransform transform;
    std::unique_ptr<GpuVideoConvert> conversion;
    std::atomic<bool>& stopping;
    std::mutex mutex;
    std::condition_variable ready;
    std::thread capture;
    HANDLE updated=nullptr,freshTimer=nullptr;
    std::exception_ptr failure;
    bool have=false;
    ID3D11Texture2D* rendered=nullptr;
    uint64_t desktopSequence=0,pointerSequence=0,renderedDesktop=0,renderedPointer=0;
    uint64_t renderedRevision=0;
    std::vector<std::pair<ID3D11Texture2D*,uint64_t>> destinationRevisions;
    uint64_t acquiredFrames=0,accumulatedFrames=0,usedFrames=0,renderedFrames=0;
    long long lastPresent=0,lastPointer=0,received100ns=0;
    ImageStamp renderedStamp;
    double frequency;
    std::vector<double> acquisitionUs;

    void attach() {
        duplication.Reset();captureView.Reset();captureTexture.Reset();
        dx(output1->DuplicateOutput(device.Get(),&duplication));duplication->GetDesc(&incomingInfo);dx(output->GetDesc(&incomingDisplay));
        D3D11_TEXTURE2D_DESC desc={};desc.Width=incomingInfo.ModeDesc.Width;desc.Height=incomingInfo.ModeDesc.Height;
        desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        dx(device->CreateTexture2D(&desc,nullptr,&captureTexture));
        dx(device->CreateShaderResourceView(captureTexture.Get(),nullptr,&captureView));
        std::fprintf(stderr,"{\"capture\":\"attached\",\"width\":%u,\"height\":%u}\n",desc.Width,desc.Height);
    }

    void run() {
        SetThreadDescription(GetCurrentThread(),L"Rewind desktop acquisition");
        HANDLE polling=CreateWaitableTimerExW(nullptr,nullptr,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS);
        try {
            if(!polling)throw std::runtime_error("Desktop acquisition timer failed");
            while(!stopping) {
                DXGI_OUTDUPL_FRAME_INFO frame={};ComPtr<IDXGIResource> resource;
                // Ненулевой timeout DDA удерживает драйверный контекст и задерживает NVENC на этом же устройстве.
                long long before=tick();HRESULT result=duplication->AcquireNextFrame(0,&frame,&resource);
                double waited=(tick()-before)*1000000.0/frequency;
                if(result==DXGI_ERROR_ACCESS_LOST||result==DXGI_ERROR_ACCESS_DENIED) {
                    std::fprintf(stderr,"{\"capture\":\"reconnecting\"}\n");
                    long long recovery=tick();
                    while(!stopping){try{attach();break;}catch(...){if(FAILED(device->GetDeviceRemovedReason())||tick()-recovery>frequency*15)throw;Sleep(100);}}
                    continue;
                }
                if(result==DXGI_ERROR_WAIT_TIMEOUT){LARGE_INTEGER due;due.QuadPart=-10000;
                    dx(SetWaitableTimer(polling,&due,0,nullptr,nullptr,FALSE)?S_OK:HRESULT_FROM_WIN32(GetLastError()));WaitForSingleObject(polling,INFINITE);continue;}
                dx(result);
                try {
                    std::lock_guard<std::mutex> lock(mutex);
                    if(acquisitionUs.size()>=2048)acquisitionUs.erase(acquisitionUs.begin(),acquisitionUs.begin()+1024);
                    acquisitionUs.push_back(waited);
                    if(frame.LastPresentTime.QuadPart||!have||latest.Get()!=captureTexture.Get()) {
                        ComPtr<ID3D11Texture2D> image;dx(resource.As(&image));context->CopyResource(captureTexture.Get(),image.Get());
                        latest=captureTexture;latestView=captureView;display=incomingDisplay;info=incomingInfo;
                        have=true;desktopSequence++;acquiredFrames++;accumulatedFrames+=frame.AccumulatedFrames;
                        if(frame.LastPresentTime.QuadPart)lastPresent=frame.LastPresentTime.QuadPart;
                    }
                    transform.pointer(duplication.Get(),frame);
                    if(frame.LastMouseUpdateTime.QuadPart||frame.PointerShapeBufferSize)pointerSequence++;
                    if(frame.LastMouseUpdateTime.QuadPart)lastPointer=frame.LastMouseUpdateTime.QuadPart;
                    received100ns=(int64_t)(tick()*10000000.0/frequency);
                }catch(...){duplication->ReleaseFrame();throw;}
                dx(duplication->ReleaseFrame());SetEvent(updated);ready.notify_all();
            }
        }catch(...) {std::lock_guard<std::mutex> lock(mutex);failure=std::current_exception();}
        if(polling)CloseHandle(polling);SetEvent(updated);ready.notify_all();
    }

public:
    struct Snapshot {uint64_t acquired,accumulated,fresh,transforms;double acquireP95Us,imageAgeMs;};
    DesktopCanvas(ID3D11Device* d,ID3D11DeviceContext* c,IDXGIOutput* o,UINT width,UINT height,double hz,std::atomic<bool>& stop,bool nv12=false)
        :device(d),context(c),output(o),transform(d,c,width,height),stopping(stop),frequency(hz) {
        if(nv12)conversion=std::make_unique<GpuVideoConvert>(d,c,width,height);
        dx(output.As(&output1));attach();
        updated=CreateEventW(nullptr,FALSE,FALSE,nullptr);freshTimer=CreateWaitableTimerExW(nullptr,nullptr,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS);
        if(!updated||!freshTimer)throw std::runtime_error("Desktop canvas notification failed");
        capture=std::thread([this]{run();});
    }
    bool waitForImage() {
        std::unique_lock<std::mutex> lock(mutex);
        if(!ready.wait_for(lock,std::chrono::seconds(10),[&]{return have||failure||stopping;}))throw std::runtime_error("First desktop image timeout");
        if(failure)std::rethrow_exception(failure);return have&&!stopping;
    }
    ImageStamp copyTo(ID3D11Texture2D* destination,bool useLatest) {
        std::unique_lock<std::mutex> lock(mutex);
        if(useLatest&&rendered&&desktopSequence==renderedDesktop&&pointerSequence==renderedPointer&&!stopping&&!failure) {
            // Короткое ожидание нового изображения не удерживает ни canvas, ни драйвер DDA.
            ResetEvent(updated);lock.unlock();LARGE_INTEGER due;due.QuadPart=-15000;
            dx(SetWaitableTimer(freshTimer,&due,0,nullptr,nullptr,FALSE)?S_OK:HRESULT_FROM_WIN32(GetLastError()));
            HANDLE waits[]={updated,freshTimer};WaitForMultipleObjects(2,waits,FALSE,INFINITE);lock.lock();
        }
        if(failure)std::rethrow_exception(failure);
        if(!rendered||(useLatest&&(desktopSequence!=renderedDesktop||pointerSequence!=renderedPointer))) {
            rendered=transform.render(latestView.Get(),display.DesktopCoordinates.right-display.DesktopCoordinates.left,
                display.DesktopCoordinates.bottom-display.DesktopCoordinates.top,info.Rotation);
            if(conversion)rendered=conversion->convert(rendered);
            if(renderedDesktop!=desktopSequence)usedFrames++;
            renderedDesktop=desktopSequence;renderedPointer=pointerSequence;renderedFrames++;renderedRevision++;
            // Ревизия включает курсор; без LastPresentTime время источника остаётся неизвестным.
            long long sourceTick=std::max(lastPresent,lastPointer);
            renderedStamp={renderedRevision,sourceTick?(int64_t)(sourceTick*10000000.0/frequency):0,received100ns,(int64_t)(tick()*10000000.0/frequency)};
        }
        auto cached=std::find_if(destinationRevisions.begin(),destinationRevisions.end(),[&](const auto& value){return value.first==destination;});
        if(cached==destinationRevisions.end()){destinationRevisions.push_back({destination,0});cached=destinationRevisions.end()-1;}
        // Повтор готового кадра не ставит одинаковое копирование снова в занятую графическую очередь.
        if(cached->second!=renderedRevision){context->CopyResource(destination,rendered);cached->second=renderedRevision;}
        return renderedStamp;
    }
    Snapshot snapshot() {
        std::lock_guard<std::mutex> lock(mutex);std::vector<double> sorted=acquisitionUs;
        double p95=0;if(!sorted.empty()){size_t index=(sorted.size()-1)*95/100;std::nth_element(sorted.begin(),sorted.begin()+index,sorted.end());p95=sorted[index];}
        return {acquiredFrames,accumulatedFrames,usedFrames,renderedFrames,p95,lastPresent?(tick()-lastPresent)*1000.0/frequency:0};
    }
    void stop(){stopping=true;if(capture.joinable())capture.join();}
    ~DesktopCanvas(){stop();if(updated)CloseHandle(updated);if(freshTimer)CloseHandle(freshTimer);}
};
