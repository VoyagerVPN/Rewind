#pragma once
#include <windows.h>
#include <cstdint>
#include <vector>
#include <functional>
#include <atomic>
#include <chrono>
#include <deque>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <stdexcept>
#include <exception>
struct Packet {std::vector<unsigned char> bytes;uint64_t pts;bool key;};
class PacketQueue {
    using Clock=std::chrono::steady_clock;
    std::function<void(const Packet&,const std::atomic<bool>&)> sink;
    std::function<void(std::exception_ptr)> notifyFailure;
    std::atomic<bool> stopped=false;
    std::mutex mutex;std::condition_variable changed;
    std::deque<Packet> packets;std::deque<uint64_t> pendingPts;
    size_t bytes=0,peakBytes=0;
    uint64_t accepted=0,delivered=0,lastPts=0;
    size_t maxBytes;
    uint64_t maxSpan;
    std::chrono::milliseconds waitLimit;
    Clock::time_point drainDeadline=Clock::time_point::max();
    bool closed=false,writerDone=false,havePts=false;
    std::exception_ptr failure;
    std::thread writer;
    Clock::time_point waitDeadline(){const auto local=Clock::now()+waitLimit;return local<drainDeadline?local:drainDeadline;}
    void consume(){
        try{
            for(;;){
                Packet packet;
                {std::unique_lock<std::mutex> lock(mutex);changed.wait(lock,[&]{return closed||stopped||!packets.empty();});
                    if(stopped)break;if(packets.empty())break;
                    if(Clock::now()>=drainDeadline)throw std::runtime_error("Transport drain deadline exceeded");
                    packet=std::move(packets.front());packets.pop_front();}
                sink(packet,stopped);
                {std::lock_guard<std::mutex> lock(mutex);if(stopped)break;bytes-=packet.bytes.size();pendingPts.pop_front();delivered++;}
                changed.notify_all();
            }
        }catch(...){
            std::exception_ptr error=std::current_exception();
            {std::lock_guard<std::mutex> lock(mutex);if(!failure)failure=error;closed=true;stopped=true;}
            // Уведомление снаружи mutex: кодировщик может одновременно ожидать ёмкость очереди.
            if(notifyFailure){try{notifyFailure(error);}catch(...){}}
        }
        {std::lock_guard<std::mutex> lock(mutex);packets.clear();pendingPts.clear();bytes=0;writerDone=true;}
        changed.notify_all();
    }
public:
    struct Snapshot {size_t bytes,peakBytes,pending;uint64_t accepted,delivered;bool failed;};
    PacketQueue(size_t byteLimit,uint64_t spanLimit,std::chrono::milliseconds deadline,
        std::function<void(const Packet&,const std::atomic<bool>&)> target,std::function<void(std::exception_ptr)> onFailure={})
        :sink(std::move(target)),notifyFailure(std::move(onFailure)),maxBytes(byteLimit),maxSpan(spanLimit),waitLimit(deadline){
        if(!maxBytes||waitLimit.count()<=0||!sink)throw std::invalid_argument("Invalid transport queue limits or sink");
        writer=std::thread([this]{consume();});
    }
    void submit(Packet packet){
        std::unique_lock<std::mutex> lock(mutex);
        if(packet.bytes.empty()||packet.bytes.size()>maxBytes)throw std::runtime_error("Transport packet size outside byte limit");
        if(havePts&&packet.pts<=lastPts)throw std::runtime_error("Transport PTS must increase");
        if(Clock::now()>=drainDeadline)throw std::runtime_error("Transport drain deadline exceeded");
        if(!changed.wait_until(lock,waitDeadline(),[&]{return failure||closed||stopped||
            (packet.bytes.size()<=maxBytes-bytes&&pendingPts.size()<256&&(pendingPts.empty()||packet.pts-pendingPts.front()<=maxSpan));}))
            throw std::runtime_error("Transport capacity deadline exceeded");
        if(failure)std::rethrow_exception(failure);if(closed||stopped)throw std::runtime_error("Transport is closed");
        // Один producer; бюджет включает очередь и пакет, уже находящийся у потребителя.
        packets.push_back(std::move(packet));
        try{pendingPts.push_back(packets.back().pts);}catch(...){packets.pop_back();throw;}
        bytes+=packets.back().bytes.size();if(bytes>peakBytes)peakBytes=bytes;
        lastPts=packets.back().pts;havePts=true;accepted++;changed.notify_all();
    }
    void abort(std::exception_ptr error){
        if(!error)error=std::make_exception_ptr(std::runtime_error("Transport aborted"));
        {std::lock_guard<std::mutex> lock(mutex);if(!failure)failure=error;closed=true;stopped=true;}changed.notify_all();
    }
    Snapshot snapshot(){std::lock_guard<std::mutex> lock(mutex);return {bytes,peakBytes,pendingPts.size(),accepted,delivered,failure!=nullptr};}
    void beginDrain(){std::lock_guard<std::mutex> lock(mutex);if(drainDeadline==Clock::time_point::max())drainDeadline=Clock::now()+waitLimit;}
    void waitIdle(){
        std::unique_lock<std::mutex> lock(mutex);
        if(!changed.wait_until(lock,waitDeadline(),[&]{return failure||pendingPts.empty();}))throw std::runtime_error("Transport idle deadline exceeded");
        if(failure)std::rethrow_exception(failure);
    }
    void finish(){
        std::exception_ptr error;
        {std::unique_lock<std::mutex> lock(mutex);closed=true;changed.notify_all();
            if(!stopped&&!changed.wait_until(lock,waitDeadline(),[&]{return writerDone||stopped;})){
                if(!failure)failure=std::make_exception_ptr(std::runtime_error("Transport drain deadline exceeded"));stopped=true;changed.notify_all();
            }
            // I/O может начаться после первой отмены. Sink проверяет stop перед каждой следующей записью.
            while(!writerDone){lock.unlock();CancelSynchronousIo(writer.native_handle());lock.lock();changed.wait_for(lock,std::chrono::milliseconds(5),[&]{return writerDone;});}
            error=failure;
        }
        if(writer.joinable())writer.join();if(error)std::rethrow_exception(error);
    }
    ~PacketQueue(){if(writer.joinable()){try{finish();}catch(...){}}}
};
