#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <stdexcept>
#include "FrameTiming.h"
static void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    try{
        const int64_t epoch=954400000000;
        ImageStamp selected{7,epoch+100000,epoch+120000,epoch+150000};
        auto first=measureImageTiming(selected,200000,epoch,epoch+200000,epoch+1200000);
        require(first.source100ns==954400100000&&first.received100ns==954400120000&&first.prepared100ns==954400150000&&first.submitted100ns==954400200000&&first.output100ns==954401200000,"FAIL: raw selected timestamps lost at output PTS");
        require(first.sequence==7&&first.pts==200000&&first.compositorDelta100ns==1100000&&first.receivedAge100ns==1080000&&first.preparedAge100ns==1050000&&first.encode100ns==1000000&&first.outputLag100ns==1000000,"FAIL: output PTS did not retain selected image and clock domains");
        auto repeat=measureImageTiming(selected,283333,epoch,epoch+1400000,epoch+1500000);
        require(repeat.sequence==7&&repeat.compositorDelta100ns==1400000&&repeat.preparedAge100ns==1350000,"FAIL: repeat replaced selected source stamp");
        auto ahead=measureImageTiming({8,epoch+1520000,epoch+1490000,epoch+1495000},1500000,epoch,epoch+1495000,epoch+1500000);
        require(ahead.compositorDelta100ns==-20000&&ahead.receivedAge100ns==10000&&ahead.preparedAge100ns==5000,"FAIL: signed compositor offset lost");
        auto large=measureImageTiming({12,7954437080000,7954437085000,7954437090000},954437180000,7000000000000,7954437190000,7954437280000);
        require(large.compositorDelta100ns==200000&&large.outputLag100ns==100000,"FAIL: QPC epoch leaked into relative PTS");
        ImageTimingWindow window;
        for(int i=0;i<600;i++)window.record(measureImageTiming({9,epoch+1520000,epoch+1490000,epoch+1495000},1500000+i,epoch,epoch+1495000,epoch+1500000));
        auto summary=window.snapshot();
        require(summary.count==600&&summary.stored==512&&summary.negativeCompositorOffsets==600&&summary.last.pts==1500599&&summary.compositorMinMs==-2&&summary.compositorP95Ms==-2&&summary.receivedP95Ms==1,"FAIL: timing history is absent or unbounded");
        std::fprintf(stderr,"{\"selectedImage\":true,\"clockDomains\":true}\n");return 0;
    }catch(const std::exception& error){std::fprintf(stderr,"%s\n",error.what());return 1;}
}
