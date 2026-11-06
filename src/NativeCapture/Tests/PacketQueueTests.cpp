#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define NOMINMAX
#include <windows.h>
#include <vector>
#include <deque>
#include <functional>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <chrono>
#include <atomic>
#include <stdexcept>
#include <string>
#include <cstdio>
#include <algorithm>
#include "PacketQueue.h"
#include "TransportStream.h"
using namespace std::chrono_literals;
static void require(bool v,const char* message){if(!v)throw std::runtime_error(message);}
static void slowReceiver(){
    std::mutex mutex;std::condition_variable changed;bool entered=false,release=false,producerDone=false;std::vector<int> received;
    PacketQueue output(1024,10000000,1000ms,[&](const Packet& packet,const std::atomic<bool>& stop){
        std::unique_lock<std::mutex> lock(mutex);entered=true;changed.notify_all();
        while(!release&&!stop)changed.wait_for(lock,5ms);
        if(!stop)received.push_back(packet.bytes[0]);
    });
    std::exception_ptr failure;
    std::thread producer([&]{try{for(int value=1;value<=4;value++)output.submit(Packet{{(unsigned char)value},(uint64_t)value*750,true});}
        catch(...){failure=std::current_exception();}{std::lock_guard<std::mutex> lock(mutex);producerDone=true;}changed.notify_all();});
    bool independent=false;{std::unique_lock<std::mutex> lock(mutex);require(changed.wait_for(lock,1s,[&]{return entered;}),"Receiver did not start");
        independent=changed.wait_for(lock,150ms,[&]{return producerDone;});release=true;}changed.notify_all();producer.join();output.finish();
    if(failure)std::rethrow_exception(failure);
    require(independent,"FAIL: slow receiver blocked packet producer");require(received==std::vector<int>({1,2,3,4}),"Packet order or tail changed");
}
static void byteLimit(){
    std::mutex mutex;std::condition_variable changed;bool entered=false,release=false;int received=0;
    PacketQueue output(8,10000000,50ms,[&](const Packet&,const std::atomic<bool>& stop){std::unique_lock<std::mutex> lock(mutex);entered=true;changed.notify_all();while(!release&&!stop)changed.wait_for(lock,5ms);received++;});
    output.submit(Packet{{1,2,3,4},0,true});{std::unique_lock<std::mutex> lock(mutex);require(changed.wait_for(lock,1s,[&]{return entered;}),"Byte-limit receiver did not start");}
    output.submit(Packet{{5,6,7,8},750,false});bool rejected=false;auto before=std::chrono::steady_clock::now();
    try{output.submit(Packet{{9},1500,false});}catch(const std::runtime_error&){rejected=true;}
    auto elapsed=std::chrono::steady_clock::now()-before;
    {std::lock_guard<std::mutex> lock(mutex);release=true;}changed.notify_all();output.finish();
    require(rejected,"FAIL: byte budget ignored in-flight packet");require(elapsed<300ms,"Byte budget wait exceeded deadline");require(received==2,"Accepted packets lost at byte limit");
}
static void timeLimit(){
    std::mutex mutex;std::condition_variable changed;bool entered=false,release=false;
    PacketQueue output(1024,200000,50ms,[&](const Packet&,const std::atomic<bool>& stop){std::unique_lock<std::mutex> lock(mutex);entered=true;changed.notify_all();while(!release&&!stop)changed.wait_for(lock,5ms);});
    output.submit(Packet{{1},0,true});{std::unique_lock<std::mutex> lock(mutex);require(changed.wait_for(lock,1s,[&]{return entered;}),"Time-limit receiver did not start");}
    bool rejected=false;try{output.submit(Packet{{2},300000,false});}catch(const std::runtime_error&){rejected=true;}
    {std::lock_guard<std::mutex> lock(mutex);release=true;}changed.notify_all();output.finish();require(rejected,"FAIL: timestamp-span budget ignored in-flight packet");
}
static void finishDeadline(){
    std::atomic<bool> entered=false,release=false;PacketQueue output(1024,10000000,50ms,[&](const Packet&,const std::atomic<bool>& stop){entered=true;while(!release&&!stop)Sleep(2);});
    output.submit(Packet{{1},0,true});while(!entered)Sleep(1);
    std::thread safety([&]{Sleep(300);release=true;});bool rejected=false;auto before=std::chrono::steady_clock::now();
    try{output.finish();}catch(const std::runtime_error&){rejected=true;}auto elapsed=std::chrono::steady_clock::now()-before;safety.join();
    require(rejected&&elapsed<200ms,"FAIL: transport finish did not honor its deadline");
}
static void sinkFailure(){
    PacketQueue output(1024,10000000,200ms,[](const Packet&,const std::atomic<bool>&){throw std::runtime_error("Injected receiver failure");});
    output.submit(Packet{{1},0,true});bool seen=false;try{output.finish();}catch(const std::runtime_error& error){seen=std::string(error.what())=="Injected receiver failure";}
    require(seen,"Receiver error was swallowed");
}
static void blockedPipe(){
    HANDLE reader=nullptr,writer=nullptr;require(CreatePipe(&reader,&writer,nullptr,4096)!=0,"Pipe creation failed");std::atomic<bool> entered=false;
    bool rejected=false;auto elapsed=0ms;
    {
        PacketQueue output(2*1024*1024,10000000,50ms,[&](const Packet& packet,const std::atomic<bool>&){entered=true;DWORD wrote=0;if(!WriteFile(writer,packet.bytes.data(),(DWORD)packet.bytes.size(),&wrote,nullptr))throw std::runtime_error("Pipe write cancelled");});
        output.submit(Packet{std::vector<unsigned char>(1024*1024,42),0,true});while(!entered)Sleep(1);auto before=std::chrono::steady_clock::now();
        try{output.finish();}catch(const std::runtime_error&){rejected=true;}elapsed=std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-before);
    }
    CloseHandle(writer);CloseHandle(reader);require(rejected&&elapsed<300ms,"Blocking WriteFile was not cancelled within deadline");
}
static void delayedBlockingPipe(){
    HANDLE reader=nullptr,writer=nullptr;require(CreatePipe(&reader,&writer,nullptr,4096)!=0,"Delayed pipe creation failed");
    std::atomic<bool> entered=false,done=false,safetyClosed=false;bool rejected=false;auto elapsed=0ms;
    std::thread safety([&]{Sleep(400);if(!done){CloseHandle(reader);safetyClosed=true;}});
    {
        PacketQueue output(2*1024*1024,10000000,50ms,[&](const Packet& packet,const std::atomic<bool>&){
            entered=true;Sleep(100);DWORD wrote=0;
            if(!WriteFile(writer,packet.bytes.data(),(DWORD)packet.bytes.size(),&wrote,nullptr))throw std::runtime_error("Delayed pipe write cancelled");
        });
        output.submit(Packet{std::vector<unsigned char>(1024*1024,42),0,true});while(!entered)Sleep(1);
        auto before=std::chrono::steady_clock::now();try{output.finish();}catch(const std::runtime_error&){rejected=true;}
        elapsed=std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-before);done=true;
    }
    safety.join();CloseHandle(writer);if(!safetyClosed)CloseHandle(reader);
    require(rejected&&elapsed<250ms,"FAIL: cancellation missed a synchronous write starting after the deadline");
}
static void explicitAbort(){
    std::atomic<bool> entered=false;
    PacketQueue output(1024,10000000,100ms,[&](const Packet&,const std::atomic<bool>& stop){entered=true;while(!stop)Sleep(1);});
    output.submit(Packet{{1},0,true});while(!entered)Sleep(1);output.abort({});
    bool rejected=false;try{output.finish();}catch(const std::runtime_error&){rejected=true;}
    require(rejected&&output.snapshot().delivered==0,"FAIL: cancellation reported successful tail delivery");
}
static void inputContract(){
    PacketQueue output(8,10000000,100ms,[](const Packet&,const std::atomic<bool>&){});
    auto rejected=[&](Packet packet){try{output.submit(std::move(packet));return false;}catch(const std::runtime_error&){return true;}};
    require(rejected(Packet{{},0,false})&&rejected(Packet{std::vector<unsigned char>(9,1),0,true}),"FAIL: invalid packet bypassed byte budget");
    output.submit(Packet{{1},10,true});output.waitIdle();
    require(rejected(Packet{{2},10,false})&&rejected(Packet{{2},9,false}),"FAIL: duplicate or reversed PTS accepted after delivery");
    output.finish();require(rejected(Packet{{2},20,false}),"FAIL: closed queue accepted a packet");
    auto state=output.snapshot();require(state.accepted==1&&state.delivered==1&&state.bytes==0,"FAIL: rejected packet affected delivery");
}
static void packetCountLimit(){
    std::atomic<bool> entered=false,release=false;
    PacketQueue output(1024,10000000,50ms,[&](const Packet&,const std::atomic<bool>& stop){entered=true;while(!release&&!stop)Sleep(1);});
    for(uint64_t i=0;i<256;i++)output.submit(Packet{{1},i,i==0});
    while(!entered)Sleep(1);bool rejected=false;try{output.submit(Packet{{2},256,false});}catch(const std::runtime_error&){rejected=true;}
    auto full=output.snapshot();release=true;output.finish();
    require(rejected&&full.pending==256&&full.bytes==256&&output.snapshot().delivered==256,"FAIL: tiny packets bypassed queue object limit");
}
static void blockedTransportStream(){
    HANDLE reader=nullptr,writer=nullptr;require(CreatePipe(&reader,&writer,nullptr,4096)!=0,"TS pipe creation failed");
    HANDLE original=GetStdHandle(STD_OUTPUT_HANDLE);require(SetStdHandle(STD_OUTPUT_HANDLE,writer)!=0,"TS stdout redirect failed");
    TransportStream transport;SetStdHandle(STD_OUTPUT_HANDLE,original);std::atomic<bool> entered=false;
    bool rejected=false;auto before=std::chrono::steady_clock::now();
    {
        PacketQueue output(2*1024*1024,10000000,50ms,[&](const Packet& packet,const std::atomic<bool>& stop){entered=true;transport.frame(packet.bytes.data(),packet.bytes.size(),packet.pts,packet.key,&stop);});
        output.submit(Packet{std::vector<unsigned char>(1024*1024,42),0,true});while(!entered)Sleep(1);before=std::chrono::steady_clock::now();
        try{output.finish();}catch(const std::runtime_error&){rejected=true;}
    }
    auto elapsed=std::chrono::steady_clock::now()-before;CloseHandle(writer);CloseHandle(reader);
    require(rejected&&elapsed<300ms,"FAIL: product TS transport could not cancel blocked pipe");
}
static void sharedDrainDeadline(){
    std::atomic<bool> entered=false;
    PacketQueue output(1024,10000000,100ms,[&](const Packet&,const std::atomic<bool>& stop){entered=true;while(!stop)Sleep(1);});
    output.submit(Packet{{1},0,true});while(!entered)Sleep(1);output.beginDrain();Sleep(80);
    auto before=std::chrono::steady_clock::now();bool rejected=false;try{output.finish();}catch(const std::runtime_error&){rejected=true;}
    require(rejected&&std::chrono::steady_clock::now()-before<70ms,"FAIL: finish reset an existing transport drain deadline");
}
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    try{slowReceiver();byteLimit();timeLimit();finishDeadline();sinkFailure();blockedPipe();delayedBlockingPipe();explicitAbort();inputContract();packetCountLimit();blockedTransportStream();sharedDrainDeadline();std::fprintf(stderr,"{\"slowReceiverIndependent\":true,\"orderedPackets\":4,\"tailDrained\":true,\"byteBudgetIncludesInFlight\":true,\"timeBudgetIncludesInFlight\":true,\"finishDeadline\":true,\"sinkErrorPropagated\":true,\"blockingWriteCancelled\":true,\"delayedWriteCancelled\":true,\"abortIsFailure\":true,\"inputContract\":true,\"packetCountBound\":true,\"productTsCancelled\":true,\"sharedDrainDeadline\":true}\n");return 0;}
    catch(const std::exception& error){std::fprintf(stderr,"%s\n",error.what());return 1;}
}
