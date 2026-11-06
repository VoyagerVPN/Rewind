#pragma once
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <memory>
#include "FrameTiming.h"

class WindowsCanvas {
    using Pool=winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool;
    using Session=winrt::Windows::Graphics::Capture::GraphicsCaptureSession;
    struct State {
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<ID3D11Texture2D> latest;
        ComPtr<ID3D11ShaderResourceView> view;
        winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice direct{nullptr};
        std::mutex mutex;std::condition_variable ready;
        HANDLE updated=CreateEventW(nullptr,FALSE,FALSE,nullptr);
        std::atomic<bool> closed=false;
        std::exception_ptr failure;
        uint64_t sequence=0,accumulated=0;
        UINT width=0,height=0;
        long long relativeTime=0,received100ns=0;
        std::vector<double> copyUs;
        ~State(){if(updated)CloseHandle(updated);}
        void resize(UINT w,UINT h){
            D3D11_TEXTURE2D_DESC desc={};desc.Width=w;desc.Height=h;desc.MipLevels=desc.ArraySize=1;
            desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;
            view.Reset();latest.Reset();dx(device->CreateTexture2D(&desc,nullptr,&latest));dx(device->CreateShaderResourceView(latest.Get(),nullptr,&view));width=w;height=h;
        }
    };
    std::shared_ptr<State> state;
    Pool pool{nullptr};Session session{nullptr};winrt::event_token token{},closedToken{};
    winrt::Windows::Graphics::Capture::GraphicsCaptureItem item{nullptr};
    std::unique_ptr<GpuTransform> transform;
    std::unique_ptr<GpuVideoConvert> conversion;
    UINT outputWidth,outputHeight;
    double frequency;
    uint64_t renderedSequence=0,transforms=0;
    ImageStamp renderedStamp;
    ID3D11Texture2D* rendered=nullptr;
    HANDLE timer=nullptr;
    std::vector<std::pair<ID3D11Texture2D*,uint64_t>> destinationRevisions;
public:
    WindowsCanvas(ID3D11Device* d,ID3D11DeviceContext* c,IDXGIOutput* output,UINT width,UINT height,double hz,bool nv12,int fps)
        :state(std::make_shared<State>()),outputWidth(width),outputHeight(height),frequency(hz){
        try{
            if(nv12)conversion=std::make_unique<GpuVideoConvert>(d,c,width,height);
            if(!state->updated)throw std::runtime_error("WGC notification failed");
            state->device=d;state->context=c;
            ComPtr<IDXGIDevice> dxgi;dx(d->QueryInterface(IID_PPV_ARGS(&dxgi)));winrt::com_ptr<IInspectable> inspectable;
            dx(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(),inspectable.put()));state->direct=inspectable.as<winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();
            DXGI_OUTPUT_DESC display={};dx(output->GetDesc(&display));
            auto factory=winrt::get_activation_factory<winrt::Windows::Graphics::Capture::GraphicsCaptureItem,IGraphicsCaptureItemInterop>();
            dx(factory->CreateForMonitor(display.Monitor,winrt::guid_of<winrt::Windows::Graphics::Capture::GraphicsCaptureItem>(),winrt::put_abi(item)));
            closedToken=item.Closed([shared=state](auto const&,auto const&){std::lock_guard<std::mutex> lock(shared->mutex);
                if(!shared->closed)shared->failure=std::make_exception_ptr(std::runtime_error("Captured monitor is unavailable"));
                SetEvent(shared->updated);shared->ready.notify_all();});
            auto size=item.Size();state->resize(size.Width,size.Height);
            pool=Pool::CreateFreeThreaded(state->direct,winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,2,size);
            // Обработчик владеет состоянием: уже поставленное событие не обращается к уничтоженному canvas.
            token=pool.FrameArrived([shared=state,hz](Pool const& sender,winrt::Windows::Foundation::IInspectable const&){
                if(shared->closed)return;
                try{
                    winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame newest{nullptr};uint64_t count=0;
                    while(auto frame=sender.TryGetNextFrame()){if(newest)newest.Close();newest=frame;count++;}
                    if(!newest)return;
                    auto size=newest.ContentSize();
                    if(size.Width<=0||size.Height<=0){newest.Close();return;}
                    std::unique_lock<std::mutex> lock(shared->mutex);if(shared->closed){newest.Close();return;}
                    if((UINT)size.Width!=shared->width||(UINT)size.Height!=shared->height){
                        newest.Close();shared->resize(size.Width,size.Height);lock.unlock();
                        sender.Recreate(shared->direct,winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,2,size);return;
                    }
                    auto access=newest.Surface().as<Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();ComPtr<ID3D11Texture2D> image;
                    // Общий context отправляет накопленные копии вместе с подготовкой следующего NVENC-кадра.
                    dx(access->GetInterface(IID_PPV_ARGS(&image)));long long before=tick();shared->context->CopyResource(shared->latest.Get(),image.Get());
                    if(shared->copyUs.size()>=2048)shared->copyUs.erase(shared->copyUs.begin(),shared->copyUs.begin()+1024);
                    shared->copyUs.push_back((tick()-before)*1000000.0/hz);
                    shared->relativeTime=newest.SystemRelativeTime().count();shared->received100ns=(int64_t)(tick()*10000000.0/hz);shared->sequence++;shared->accumulated+=count;
                    newest.Close();lock.unlock();SetEvent(shared->updated);shared->ready.notify_all();
                }catch(...){std::lock_guard<std::mutex> lock(shared->mutex);if(!shared->closed)shared->failure=std::current_exception();SetEvent(shared->updated);shared->ready.notify_all();}
            });
            session=pool.CreateCaptureSession(item);session.IsCursorCaptureEnabled(true);
            // Запрос безрамочного режима проходит через системное разрешение Windows.
            bool borderless=false;int borderAccess=-1;
            if(auto border=session.try_as<winrt::Windows::Graphics::Capture::IGraphicsCaptureSession3>()){
                try{
                    auto access=winrt::Windows::Graphics::Capture::GraphicsCaptureAccess::RequestAccessAsync(
                        winrt::Windows::Graphics::Capture::GraphicsCaptureAccessKind::Borderless).get();
                    borderAccess=(int)access;
                    if(access==winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus::Allowed){
                        border.IsBorderRequired(false);borderless=!border.IsBorderRequired();
                    }
                }catch(const winrt::hresult_error& error){std::fprintf(stderr,"{\"capture\":\"border-unavailable\",\"hresult\":%ld}\n",(long)error.code().value);}
            }
            std::fprintf(stderr,"{\"capture\":\"border-state\",\"access\":%d,\"borderless\":%s}\n",borderAccess,borderless?"true":"false");
            // Запас частоты источника нужен для независимой фазы encoder clock; на быстрых экранах не копируем лишние сотни кадров.
            double interval=.001;DEVMODEW mode={};mode.dmSize=sizeof(mode);
            if(EnumDisplaySettingsW(display.DeviceName,ENUM_CURRENT_SETTINGS,&mode)&&mode.dmDisplayFrequency>1){
                UINT periods=std::max(1U,(UINT)mode.dmDisplayFrequency/((UINT)fps*2));interval=(periods-.5)/mode.dmDisplayFrequency;
            }
            if(auto options=session.try_as<winrt::Windows::Graphics::Capture::IGraphicsCaptureSession5>())options.MinUpdateInterval(std::chrono::microseconds((long long)(interval*1000000)));
            timer=CreateWaitableTimerExW(nullptr,nullptr,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS);
            if(!timer)throw std::runtime_error("WGC freshness timer failed");session.StartCapture();
            std::fprintf(stderr,"{\"capture\":\"attached\",\"source\":\"wgc-monitor\",\"width\":%u,\"height\":%u,\"minimumIntervalMs\":%.3f,\"encodedFps\":%d,\"pixelFormat\":\"%s\"}\n",state->width,state->height,interval*1000,fps,nv12?"nv12":"argb");
        }catch(...){stop();throw;}
    }
    bool waitForImage(){std::unique_lock<std::mutex> lock(state->mutex);
        if(!state->ready.wait_for(lock,std::chrono::seconds(10),[&]{return state->sequence||state->failure||state->closed;}))throw std::runtime_error("WGC first image timeout");
        if(state->failure)std::rethrow_exception(state->failure);return state->sequence&&!state->closed;
    }
    ImageStamp copyTo(ID3D11Texture2D* destination,bool useLatest){
        std::unique_lock<std::mutex> lock(state->mutex);
        if(useLatest&&rendered&&state->sequence==renderedSequence&&!state->failure&&!state->closed){
            ResetEvent(state->updated);lock.unlock();LARGE_INTEGER due;due.QuadPart=-15000;
            dx(SetWaitableTimer(timer,&due,0,nullptr,nullptr,FALSE)?S_OK:HRESULT_FROM_WIN32(GetLastError()));
            HANDLE events[]={state->updated,timer};WaitForMultipleObjects(2,events,FALSE,INFINITE);lock.lock();
        }
        if(state->failure)std::rethrow_exception(state->failure);
        if(!rendered||(useLatest&&state->sequence!=renderedSequence)){
            // WGC уже нормализует ориентацию монитора и рисует системный курсор.
            if(outputWidth==state->width&&outputHeight==state->height){
                if(conversion)rendered=conversion->convert(state->latest.Get());
                else{
                    // При родном размере подготовленный NVENC-слот одновременно хранит последний готовый кадр.
                    state->context->CopyResource(destination,state->latest.Get());rendered=destination;
                }
            }else{
                if(!transform)transform=std::make_unique<GpuTransform>(state->device.Get(),state->context.Get(),outputWidth,outputHeight);
                rendered=transform->render(state->view.Get(),state->width,state->height,DXGI_MODE_ROTATION_IDENTITY);if(conversion)rendered=conversion->convert(rendered);
            }
            renderedSequence=state->sequence;transforms++;
            renderedStamp={renderedSequence,state->relativeTime,state->received100ns,(int64_t)(tick()*10000000.0/frequency)};
        }
        auto cached=std::find_if(destinationRevisions.begin(),destinationRevisions.end(),[&](const auto& value){return value.first==destination;});
        if(cached==destinationRevisions.end()){destinationRevisions.push_back({destination,0});cached=destinationRevisions.end()-1;}
        if(cached->second!=renderedSequence){if(destination!=rendered)state->context->CopyResource(destination,rendered);cached->second=renderedSequence;}
        return renderedStamp;
    }
    DesktopCanvas::Snapshot snapshot(){std::lock_guard<std::mutex> lock(state->mutex);auto samples=state->copyUs;double p95=0;
        if(!samples.empty()){size_t index=(samples.size()-1)*95/100;std::nth_element(samples.begin(),samples.begin()+index,samples.end());p95=samples[index];}
        return {state->sequence,state->accumulated,transforms,transforms,p95,state->relativeTime?std::max(0.0,(tick()*10000000.0/frequency-state->relativeTime)/10000.0):0};
    }
    void stop(){if(state->closed.exchange(true))return;
        if(item)item.Closed(closedToken);
        if(pool){pool.FrameArrived(token);if(session)session.Close();pool.Close();}state->ready.notify_all();SetEvent(state->updated);
        if(timer){CloseHandle(timer);timer=nullptr;}
    }
    ~WindowsCanvas(){stop();}
};

class CaptureCanvas {
    std::unique_ptr<DesktopCanvas> desktop;
    std::unique_ptr<WindowsCanvas> windows;
public:
    CaptureCanvas(ID3D11Device* device,ID3D11DeviceContext* context,IDXGIOutput* output,UINT width,UINT height,double hz,std::atomic<bool>& stop,bool nv12,int fps){
        if(GetEnvironmentVariableW(L"REWIND_CAPTURE_DDA",nullptr,0)>0||!winrt::Windows::Graphics::Capture::GraphicsCaptureSession::IsSupported())
            desktop=std::make_unique<DesktopCanvas>(device,context,output,width,height,hz,stop,nv12);
        else windows=std::make_unique<WindowsCanvas>(device,context,output,width,height,hz,nv12,fps);
    }
    bool waitForImage(){return windows?windows->waitForImage():desktop->waitForImage();}
    ImageStamp copyTo(ID3D11Texture2D* destination,bool useLatest){return windows?windows->copyTo(destination,useLatest):desktop->copyTo(destination,useLatest);}
    DesktopCanvas::Snapshot snapshot(){return windows?windows->snapshot():desktop->snapshot();}
    void stop(){if(windows)windows->stop();else desktop->stop();}
};
