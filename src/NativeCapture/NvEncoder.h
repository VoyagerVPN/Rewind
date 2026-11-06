#pragma once
#include <functional>
#include <type_traits>
#include <memory>
#include "FrameTiming.h"
#include "PacketQueue.h"
// Общий проверенный NVENC-тракт. Полноразмерные изображения остаются в видеопамяти.
class Encoder {
    std::mutex metricsMutex;
    void sample(std::vector<double>& values,double value){std::lock_guard<std::mutex> lock(metricsMutex);if(values.size()>=4096)values.erase(values.begin(),values.begin()+2048);values.push_back(value);}
    static double percentile(std::vector<double> values){if(values.empty())return 0;size_t index=(values.size()-1)*95/100;std::nth_element(values.begin(),values.begin()+index,values.end());return values[index];}
    struct Slot {
        ComPtr<ID3D11Texture2D> texture;
        NV_ENC_REGISTERED_PTR registered = nullptr;
        NV_ENC_INPUT_PTR mapped = nullptr;
        NV_ENC_OUTPUT_PTR output = nullptr;
        HANDLE event = nullptr;
        ImageStamp stamp;
        int64_t submitted100ns=0;
        bool bitstreamLocked=false;
    };
    HMODULE library = nullptr;
    NV_ENCODE_API_FUNCTION_LIST api = {};
    void* encoder = nullptr;
    // Асинхронному NVENC без B-кадров и lookahead достаточно четырёх комплектов.
    // Выходные буферы также занимают RAM, даже когда транспортная очередь пуста.
    static constexpr size_t slotCount=8;
    Slot slots[slotCount];
    std::mutex mutex;
    std::condition_variable changed;
    std::deque<size_t> free, ready;
    size_t unpublished=0;
    std::exception_ptr failure;
    std::thread writer;
    std::unique_ptr<PacketQueue> packetQueue;
    bool done = false;
    HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    UINT width, height;
    bool nv12=false;
    double frequency;
    std::function<void(const unsigned char*, size_t, uint64_t, bool,const std::atomic<bool>&)> sink;
    int64_t epoch100ns=0;
    ImageTimingWindow imageWindow;
    std::function<void(ImageTiming)> imageObserver;
public:
    static constexpr size_t packetByteLimit=8*1024*1024;
    struct Timings {double queueP95Us,submitP95Us,outputP95Us,prepareP95Us,flushP95Us,mapP95Us,encodeP95Us;size_t pending;};
    std::vector<double> submitUs, outputUs, queueWaitUs;
    std::vector<double> prepareUs,flushUs,mapUs,encodeUs;
    std::vector<double> completionUs,bitstreamUs,transportUs,unmapUs;
    ImageTimingWindow::Snapshot imageTimings(){return imageWindow.snapshot();}
    void logImageTimings(){auto value=imageTimings();
        std::fprintf(stderr,"{\"selectedImageTiming\":true,\"count\":%llu,\"window\":%zu,\"compositorSamples\":%zu,\"lastPts100ns\":%llu,\"lastSequence\":%llu,\"lastSource100ns\":%lld,\"lastReceived100ns\":%lld,\"lastPrepared100ns\":%lld,\"lastSubmitted100ns\":%lld,\"lastOutput100ns\":%lld,\"compositorMinMs\":%.3f,\"compositorP95Ms\":%.3f,\"receivedP95Ms\":%.3f,\"preparedP95Ms\":%.3f,\"encodeP95Ms\":%.3f,\"outputLagP95Ms\":%.3f,\"negativeCompositorOffsets\":%llu}\n",
            (unsigned long long)value.count,value.stored,value.compositorSamples,(unsigned long long)value.last.pts,(unsigned long long)value.last.sequence,
            (long long)value.last.source100ns,(long long)value.last.received100ns,(long long)value.last.prepared100ns,(long long)value.last.submitted100ns,(long long)value.last.output100ns,
            value.compositorMinMs,value.compositorP95Ms,value.receivedP95Ms,value.preparedP95Ms,value.encodeP95Ms,value.lagP95Ms,(unsigned long long)value.negativeCompositorOffsets);
    }
    void logOutputStages(){
        {std::lock_guard<std::mutex> lock(metricsMutex);std::fprintf(stderr,"{\"encoderOutput\":true,\"completionP95Us\":%.3f,\"bitstreamP95Us\":%.3f,\"transportP95Us\":%.3f,\"unmapP95Us\":%.3f}\n",percentile(completionUs),percentile(bitstreamUs),percentile(transportUs),percentile(unmapUs));}
        if(packetQueue){auto value=packetQueue->snapshot();std::fprintf(stderr,"{\"transportQueue\":true,\"bytes\":%zu,\"peakBytes\":%zu,\"pending\":%zu,\"accepted\":%llu,\"delivered\":%llu,\"failed\":%s}\n",value.bytes,value.peakBytes,value.pending,(unsigned long long)value.accepted,(unsigned long long)value.delivered,value.failed?"true":"false");}
    }
    Timings timings(){Timings value;{std::lock_guard<std::mutex> lock(metricsMutex);value={percentile(queueWaitUs),percentile(submitUs),percentile(outputUs),percentile(prepareUs),percentile(flushUs),percentile(mapUs),percentile(encodeUs),0};}{std::lock_guard<std::mutex> lock(mutex);value.pending=slotCount-free.size();}return value;}
    Encoder(ID3D11Device* device, UINT w, UINT h, int fps, double hz,
        std::function<void(const unsigned char*, size_t, uint64_t, bool,const std::atomic<bool>&)> outputSink = {},bool useNv12=false,
        int64_t epoch=0,std::function<void(ImageTiming)> observer={}) : width(w), height(h),nv12(useNv12), frequency(hz), sink(outputSink),epoch100ns(epoch),imageObserver(std::move(observer)) {
        library = LoadLibraryExW(L"nvEncodeAPI64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!library) throw std::runtime_error("NVENC runtime not found");
        auto create = reinterpret_cast<NVENCSTATUS (NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*)>(GetProcAddress(library, "NvEncodeAPICreateInstance"));
        if (!create) throw std::runtime_error("NVENC entry point not found");
        api.version = NV_ENCODE_API_FUNCTION_LIST_VER; nv(create(&api));
        NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open = {};
        open.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
        open.device = device; open.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX; open.apiVersion = NVENCAPI_VERSION;
        nv(api.nvEncOpenEncodeSessionEx(&open, &encoder));
        NV_ENC_PRESET_CONFIG preset = {}; preset.version = NV_ENC_PRESET_CONFIG_VER; preset.presetCfg.version = NV_ENC_CONFIG_VER;
        nv(api.nvEncGetEncodePresetConfigEx(encoder, NV_ENC_CODEC_H264_GUID, NV_ENC_PRESET_P4_GUID, NV_ENC_TUNING_INFO_HIGH_QUALITY, &preset));
        NV_ENC_CONFIG config = preset.presetCfg;
        std::fprintf(stderr,"{\"presetMultipass\":%u,\"spatialAQ\":%u,\"temporalAQ\":%u}\n",(unsigned)config.rcParams.multiPass,(unsigned)config.rcParams.enableAQ,(unsigned)config.rcParams.enableTemporalAQ);
        config.profileGUID = NV_ENC_H264_PROFILE_HIGH_GUID; config.gopLength = fps*2; config.frameIntervalP = 1;
        config.rcParams.rateControlMode = NV_ENC_PARAMS_RC_VBR;
        config.rcParams.averageBitRate = 0; config.rcParams.maxBitRate = 0; config.rcParams.targetQuality = 18;
        config.rcParams.enableLookahead = 0;
        config.encodeCodecConfig.h264Config.idrPeriod = fps*2;
        config.encodeCodecConfig.h264Config.repeatSPSPPS = 1;
        config.encodeCodecConfig.h264Config.outputAUD = 1;
        auto& vui=config.encodeCodecConfig.h264Config.h264VUIParameters;
        vui.videoSignalTypePresentFlag=1;vui.videoFormat=(NV_ENC_VUI_VIDEO_FORMAT)5;vui.videoFullRangeFlag=0;
        vui.colourDescriptionPresentFlag=1;vui.colourPrimaries=(NV_ENC_VUI_COLOR_PRIMARIES)1;vui.transferCharacteristics=(NV_ENC_VUI_TRANSFER_CHARACTERISTIC)13;vui.colourMatrix=(NV_ENC_VUI_MATRIX_COEFFS)6;
        NV_ENC_INITIALIZE_PARAMS init = {}; init.version = NV_ENC_INITIALIZE_PARAMS_VER;
        init.encodeGUID = NV_ENC_CODEC_H264_GUID; init.presetGUID = NV_ENC_PRESET_P4_GUID;
        init.encodeWidth = init.darWidth = w; init.encodeHeight = init.darHeight = h;
        init.frameRateNum = fps; init.frameRateDen = 1; init.enableEncodeAsync = 1; init.enablePTD = 1;
        init.tuningInfo = NV_ENC_TUNING_INFO_HIGH_QUALITY; init.encodeConfig = &config;
        nv(api.nvEncInitializeEncoder(encoder, &init));
        std::fprintf(stderr,"encoder-initialized\n");
        for (size_t i=0; i<slotCount; i++) {
            Slot& slot = slots[i];
            D3D11_TEXTURE2D_DESC desc = {}; desc.Width=w; desc.Height=h; desc.MipLevels=desc.ArraySize=1;
            desc.Format=nv12?DXGI_FORMAT_NV12:DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count=1; desc.Usage=D3D11_USAGE_DEFAULT;
            dx(device->CreateTexture2D(&desc,nullptr,&slot.texture));
            NV_ENC_REGISTER_RESOURCE resource = {}; resource.version=NV_ENC_REGISTER_RESOURCE_VER;
            resource.resourceType=NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX; resource.resourceToRegister=slot.texture.Get();
            resource.width=w; resource.height=h; resource.bufferFormat=nv12?NV_ENC_BUFFER_FORMAT_NV12:NV_ENC_BUFFER_FORMAT_ARGB; resource.bufferUsage=NV_ENC_INPUT_IMAGE;
            nv(api.nvEncRegisterResource(encoder,&resource)); slot.registered=resource.registeredResource;
            NV_ENC_CREATE_BITSTREAM_BUFFER stream = {}; stream.version=NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
            nv(api.nvEncCreateBitstreamBuffer(encoder,&stream)); slot.output=stream.bitstreamBuffer;
            slot.event=CreateEventW(nullptr,FALSE,FALSE,nullptr); if(!slot.event)throw std::runtime_error("CreateEvent failed");
            NV_ENC_EVENT_PARAMS event = {}; event.version=NV_ENC_EVENT_PARAMS_VER; event.completionEvent=slot.event;
            nv(api.nvEncRegisterAsyncEvent(encoder,&event)); free.push_back(i);
        }
        std::fprintf(stderr,"{\"encoderResources\":true,\"slots\":%zu,\"width\":%u,\"height\":%u,\"pixelFormat\":\"%s\"}\n",slotCount,w,h,nv12?"nv12":"argb");
        packetQueue=std::make_unique<PacketQueue>(packetByteLimit,20000000,std::chrono::milliseconds(2000),
            [this](const Packet& packet,const std::atomic<bool>& cancelled){
                long long stage=tick();
                if(cancelled)throw std::runtime_error("Transport cancelled");
                if(sink)sink(packet.bytes.data(),packet.bytes.size(),packet.pts,packet.key,cancelled);
                else{size_t offset=0;while(offset<packet.bytes.size()){
                    if(cancelled)throw std::runtime_error("Transport cancelled");DWORD wrote=0;
                    if(!WriteFile(output,packet.bytes.data()+offset,(DWORD)(packet.bytes.size()-offset),&wrote,nullptr)||!wrote)throw std::runtime_error("Encoded output pipe failed");offset+=wrote;
                }}
                sample(transportUs,(tick()-stage)*1000000.0/frequency);
            },[this](std::exception_ptr error){std::lock_guard<std::mutex> lock(mutex);if(!failure)failure=error;done=true;changed.notify_all();});
        writer=std::thread([this] { try {
            for (;;) {
                size_t index;
                { std::unique_lock<std::mutex> lock(mutex); changed.wait(lock,[&]{return done||!ready.empty();}); if(ready.empty())break; index=ready.front(); ready.pop_front(); }
                Slot& slot=slots[index]; long long before=tick(),stage=before;
                if(WaitForSingleObject(slot.event,10000)!=WAIT_OBJECT_0)throw std::runtime_error("NVENC completion timeout");
                sample(completionUs,(tick()-stage)*1000000.0/frequency);stage=tick();
                NV_ENC_LOCK_BITSTREAM stream = {}; stream.version=NV_ENC_LOCK_BITSTREAM_VER;
                stream.outputBitstream=slot.output; stream.doNotWait=0; nv(api.nvEncLockBitstream(encoder,&stream));
                slot.bitstreamLocked=true;
                sample(bitstreamUs,(tick()-stage)*1000000.0/frequency);stage=tick();
                auto imageTiming=measureImageTiming(slot.stamp,stream.outputTimeStamp,epoch100ns,slot.submitted100ns,(int64_t)(tick()*10000000.0/frequency));
                imageWindow.record(imageTiming);if(imageObserver)imageObserver(imageTiming);
                bool discard;{std::lock_guard<std::mutex> lock(mutex);discard=failure!=nullptr;}
                Packet packet;std::exception_ptr packetFailure;
                if(!discard){try{
                    if(!stream.bitstreamSizeInBytes||stream.bitstreamSizeInBytes>packetByteLimit)throw std::runtime_error("Encoded packet exceeds transport byte limit");
                    packet={std::vector<unsigned char>((unsigned char*)stream.bitstreamBufferPtr,(unsigned char*)stream.bitstreamBufferPtr+stream.bitstreamSizeInBytes),stream.outputTimeStamp,stream.pictureType==NV_ENC_PIC_TYPE_IDR};
                }catch(...){packetFailure=std::current_exception();}}
                stage=tick();
                nv(api.nvEncUnlockBitstream(encoder,slot.output));slot.bitstreamLocked=false; nv(api.nvEncUnmapInputResource(encoder,slot.mapped)); slot.mapped=nullptr;
                sample(unmapUs,(tick()-stage)*1000000.0/frequency);
                sample(outputUs,(tick()-before)*1000000.0/frequency);
                {std::lock_guard<std::mutex> lock(mutex);free.push_back(index);} changed.notify_all();
                // Ресурс NVENC уже свободен; ожидание ёмкости транспорта не удерживает вход.
                if(!discard){try{if(packetFailure)std::rethrow_exception(packetFailure);packetQueue->submit(std::move(packet));}
                    catch(...){auto error=std::current_exception();{std::lock_guard<std::mutex> lock(mutex);if(!failure)failure=error;done=true;}changed.notify_all();packetQueue->abort(error);}}
                {std::lock_guard<std::mutex> lock(mutex);unpublished--;}changed.notify_all();
            }
        } catch(...) {auto error=std::current_exception();{std::lock_guard<std::mutex> lock(mutex);if(!failure)failure=error;done=true;}changed.notify_all();packetQueue->abort(error);} });
    }
    void submit(ID3D11DeviceContext* context, ID3D11Texture2D* image, int frame, int fps, uint64_t timestamp100ns = UINT64_MAX, bool forceKeyframe = false) {
        submitPrepared(context,(uint64_t)frame,fps,timestamp100ns,forceKeyframe,[&](ID3D11Texture2D* destination){context->CopyResource(destination,image);});
    }
    template<class Prepare>
    void submitPrepared(ID3D11DeviceContext* context,uint64_t frame,int fps,uint64_t timestamp100ns,bool forceKeyframe,Prepare&& prepare) {
        long long before=tick(); size_t index;
        { std::unique_lock<std::mutex> lock(mutex); if(!changed.wait_for(lock,std::chrono::seconds(2),[&]{return failure||!free.empty();}))throw std::runtime_error("NVENC input capacity deadline exceeded"); if(failure)std::rethrow_exception(failure); index=free.front();free.pop_front(); }
        sample(queueWaitUs,(tick()-before)*1000000.0/frequency);
        // Ожидание свободного слота завершено до обращения к общему canvas.
        Slot& slot=slots[index]; before=tick();long long stage=before;
        if constexpr(std::is_convertible_v<std::invoke_result_t<Prepare,ID3D11Texture2D*>,ImageStamp>)slot.stamp=prepare(slot.texture.Get());
        else {prepare(slot.texture.Get());slot.stamp={};}
        sample(prepareUs,(tick()-stage)*1000000.0/frequency);
        stage=tick();context->Flush();sample(flushUs,(tick()-stage)*1000000.0/frequency);
        NV_ENC_MAP_INPUT_RESOURCE map = {}; map.version=NV_ENC_MAP_INPUT_RESOURCE_VER; map.registeredResource=slot.registered;
        stage=tick();nv(api.nvEncMapInputResource(encoder,&map));slot.mapped=map.mappedResource;sample(mapUs,(tick()-stage)*1000000.0/frequency);
        NV_ENC_PIC_PARAMS pic = {}; pic.version=NV_ENC_PIC_PARAMS_VER; pic.inputBuffer=slot.mapped;
        pic.bufferFmt=nv12?NV_ENC_BUFFER_FORMAT_NV12:NV_ENC_BUFFER_FORMAT_ARGB; pic.inputWidth=width;pic.inputHeight=height;
        pic.outputBitstream=slot.output;pic.completionEvent=slot.event;pic.pictureStruct=NV_ENC_PIC_STRUCT_FRAME;
        pic.inputTimeStamp=timestamp100ns==UINT64_MAX?frame*10000000/fps:timestamp100ns;
        pic.inputDuration=(frame+1)*10000000/fps-frame*10000000/fps;
        if(forceKeyframe)pic.encodePicFlags=NV_ENC_PIC_FLAG_FORCEIDR;
        stage=tick();slot.submitted100ns=(int64_t)(stage*10000000.0/frequency);
        {std::lock_guard<std::mutex> lock(mutex);if(failure)std::rethrow_exception(failure);
            NVENCSTATUS status=api.nvEncEncodePicture(encoder,&pic);if(status!=NV_ENC_ERR_NEED_MORE_INPUT)nv(status);ready.push_back(index);unpublished++;}
        changed.notify_all();sample(encodeUs,(tick()-stage)*1000000.0/frequency);
        sample(submitUs,(tick()-before)*1000000.0/frequency);
    }
    void finish() {
        if(!writer.joinable()){if(packetQueue)packetQueue->finish();if(failure)std::rethrow_exception(failure);return;}
        // Один транспортный срок проходит через оставшуюся публикацию, EOS и слив очереди.
        packetQueue->beginDrain();
        HANDLE completion=CreateEventW(nullptr,FALSE,FALSE,nullptr);if(!completion)throw std::runtime_error("EOS event failed");
        NV_ENC_EVENT_PARAMS event={};event.version=NV_ENC_EVENT_PARAMS_VER;event.completionEvent=completion;
        struct CompletionGuard {
            NV_ENCODE_API_FUNCTION_LIST* api;void* encoder;NV_ENC_EVENT_PARAMS* event;HANDLE handle;bool registered=false;
            ~CompletionGuard(){if(registered)api->nvEncUnregisterAsyncEvent(encoder,event);CloseHandle(handle);}
        } cleanup{&api,encoder,&event,completion};
        nv(api.nvEncRegisterAsyncEvent(encoder,&event));cleanup.registered=true;
        NV_ENC_PIC_PARAMS eos = {};eos.version=NV_ENC_PIC_PARAMS_VER;eos.encodePicFlags=NV_ENC_PIC_FLAG_EOS;
        eos.completionEvent=completion;
        nv(api.nvEncEncodePicture(encoder,&eos));
        {std::lock_guard<std::mutex> lock(mutex);done=true;}changed.notify_all();writer.join();
        if(WaitForSingleObject(completion,10000)!=WAIT_OBJECT_0){
            auto error=std::make_exception_ptr(std::runtime_error("EOS timeout"));packetQueue->abort(error);packetQueue->finish();std::rethrow_exception(error);
        }
        // Успешный EOS включает доставку хвоста, а не только освобождение GPU-входов.
        packetQueue->finish();if(failure)std::rethrow_exception(failure);
    }
    void waitInputsIdle(){std::unique_lock<std::mutex> lock(mutex);
        if(!changed.wait_for(lock,std::chrono::seconds(10),[&]{return failure||free.size()==slotCount;}))throw std::runtime_error("NVENC warmup timeout");
        if(failure)std::rethrow_exception(failure);
    }
    void waitIdle(){
        {std::unique_lock<std::mutex> lock(mutex);
            // Между возвратом входа и публикацией пакета существует промежуток: пустой очереди недостаточно.
            if(!changed.wait_for(lock,std::chrono::seconds(10),[&]{return failure||(free.size()==slotCount&&unpublished==0);}))throw std::runtime_error("NVENC output publication timeout");
            if(failure)std::rethrow_exception(failure);
        }
        packetQueue->waitIdle();
    }
    ~Encoder() {
        if(writer.joinable()){
            if(packetQueue)packetQueue->abort(std::make_exception_ptr(std::runtime_error("Encoder stopped before drain")));
            {std::lock_guard<std::mutex> lock(mutex);done=true;} changed.notify_all();writer.join();
        }
        if(packetQueue){try{packetQueue->finish();}catch(...){}}
        if(encoder) {
            for(auto& slot:slots) {
                if(slot.bitstreamLocked)api.nvEncUnlockBitstream(encoder,slot.output);
                if(slot.mapped)api.nvEncUnmapInputResource(encoder,slot.mapped);
                if(slot.registered)api.nvEncUnregisterResource(encoder,slot.registered);
                if(slot.output)api.nvEncDestroyBitstreamBuffer(encoder,slot.output);
                if(slot.event){NV_ENC_EVENT_PARAMS e={};e.version=NV_ENC_EVENT_PARAMS_VER;e.completionEvent=slot.event;api.nvEncUnregisterAsyncEvent(encoder,&e);CloseHandle(slot.event);}
            }
            api.nvEncDestroyEncoder(encoder);
        }
        if(library)FreeLibrary(library);
    }
};
