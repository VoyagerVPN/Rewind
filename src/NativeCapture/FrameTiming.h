#pragma once
#include <cstdint>
#include <array>
#include <algorithm>
#include <mutex>

// Все времена изображения абсолютные QPC в 100 нс; PTS остаётся относительным к epoch.
struct ImageStamp {
    uint64_t sequence=0;
    int64_t compositor100ns=0,received100ns=0,prepared100ns=0;
};
struct ImageTiming {
    uint64_t pts=0,sequence=0;
    int64_t compositorDelta100ns=0,receivedAge100ns=0,preparedAge100ns=0,encode100ns=0,outputLag100ns=0;
    bool compositorKnown=false;
    int64_t source100ns=0,received100ns=0,prepared100ns=0,submitted100ns=0,output100ns=0;
};
inline ImageTiming measureImageTiming(ImageStamp stamp,uint64_t pts,int64_t epoch,int64_t submitted,int64_t output){
    // Смещение метки композитора сохраняется со знаком: это не физическая задержка дисплея.
    return {pts,stamp.sequence,stamp.compositor100ns?output-stamp.compositor100ns:0,
        stamp.received100ns?output-stamp.received100ns:0,stamp.prepared100ns?output-stamp.prepared100ns:0,
        output-submitted,output-epoch-(int64_t)pts,stamp.compositor100ns!=0,
        stamp.compositor100ns,stamp.received100ns,stamp.prepared100ns,submitted,output};
}

class ImageTimingWindow {
    std::mutex mutex;
    std::array<ImageTiming,512> values{};
    uint64_t count=0,negative=0;
    size_t cursor=0,stored=0;
    template<class Get> static double percentile(const std::array<ImageTiming,512>& samples,size_t length,Get get){
        if(!length)return 0;
        std::array<int64_t,512> sorted{};for(size_t i=0;i<length;i++)sorted[i]=get(samples[i]);
        size_t index=(length-1)*95/100;std::nth_element(sorted.begin(),sorted.begin()+index,sorted.begin()+length);return sorted[index]/10000.0;
    }
public:
    static constexpr size_t capacity=512;
    struct Snapshot {
        uint64_t count=0,negativeCompositorOffsets=0;
        size_t stored=0,compositorSamples=0;
        ImageTiming last;
        double compositorMinMs=0,compositorP95Ms=0,receivedP95Ms=0,preparedP95Ms=0,encodeP95Ms=0,lagP95Ms=0;
    };
    void record(ImageTiming value){
        if(!value.sequence)return;
        std::lock_guard<std::mutex> lock(mutex);values[cursor]=value;cursor=(cursor+1)%capacity;stored=(std::min)(stored+1,capacity);count++;
        if(value.compositorKnown&&value.compositorDelta100ns<0)negative++;
    }
    Snapshot snapshot(){
        std::array<ImageTiming,512> samples;Snapshot result;
        {std::lock_guard<std::mutex> lock(mutex);samples=values;result.count=count;result.negativeCompositorOffsets=negative;result.stored=stored;if(stored)result.last=values[(cursor+capacity-1)%capacity];}
        if(!result.stored)return result;
        std::array<ImageTiming,512> known{};size_t length=0;
        for(size_t i=0;i<result.stored;i++)if(samples[i].compositorKnown)known[length++]=samples[i];
        result.compositorSamples=length;
        if(length){auto minimum=known[0].compositorDelta100ns;for(size_t i=1;i<length;i++)minimum=(std::min)(minimum,known[i].compositorDelta100ns);result.compositorMinMs=minimum/10000.0;}
        result.compositorP95Ms=percentile(known,length,[](auto x){return x.compositorDelta100ns;});
        result.receivedP95Ms=percentile(samples,result.stored,[](auto x){return x.receivedAge100ns;});
        result.preparedP95Ms=percentile(samples,result.stored,[](auto x){return x.preparedAge100ns;});
        result.encodeP95Ms=percentile(samples,result.stored,[](auto x){return x.encode100ns;});
        result.lagP95Ms=percentile(samples,result.stored,[](auto x){return x.outputLag100ns;});return result;
    }
};
