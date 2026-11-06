#pragma once
#include <array>
#include <cstring>
#include <atomic>

// Небольшой транспорт только для уже сжатого H.264: PAT/PMT, PES с PTS и PCR.
// Временные метки идут от общего QPC; исходные пиксели через pipe не передаются.
class TransportStream {
    HANDLE output=GetStdHandle(STD_OUTPUT_HANDLE);
    unsigned char videoCounter=0,patCounter=0,pmtCounter=0;
    bool first=true;
    void write(const unsigned char* data,size_t length,const std::atomic<bool>* cancelled) {
        while(length){if(cancelled&&cancelled->load())throw std::runtime_error("Capture transport cancelled");DWORD done=0;if(!WriteFile(output,data,(DWORD)length,&done,nullptr)||!done)throw std::runtime_error("Capture transport closed");data+=done;length-=done;}
    }
    void table(int pid,std::vector<unsigned char> section,unsigned char& counter,const std::atomic<bool>* cancelled) {
        uint32_t crc=0xffffffff;
        for(unsigned char byte:section){crc^=(uint32_t)byte<<24;for(int bit=0;bit<8;bit++)crc=crc&0x80000000?(crc<<1)^0x04c11db7:crc<<1;}
        for(int shift=24;shift>=0;shift-=8)section.push_back((unsigned char)(crc>>shift));
        std::array<unsigned char,188> packet;packet.fill(0xff);packet[0]=0x47;packet[1]=0x40|(pid>>8);packet[2]=(unsigned char)pid;packet[3]=0x10|(counter++&15);packet[4]=0;
        std::memcpy(packet.data()+5,section.data(),section.size());write(packet.data(),packet.size(),cancelled);
    }
public:
    void frame(const unsigned char* bytes,size_t length,uint64_t time100ns,bool key,const std::atomic<bool>* cancelled=nullptr) {
        if(first||key){table(0,{0,0xb0,13,0,1,0xc1,0,0,0,1,0xe0,0x20},patCounter,cancelled);table(0x20,{2,0xb0,18,0,1,0xc1,0,0,0xe1,0,0xf0,0,0x1b,0xe1,0,0xf0,0},pmtCounter,cancelled);}
        // Округление сохраняет точные 750 тактов 90 кГц для каждого кадра 120 FPS.
        uint64_t pts=(time100ns*9+500)/1000;
        unsigned char header[14]={0,0,1,0xe0,0,0,0x80,0x80,5,
            (unsigned char)(0x21|((pts>>29)&14)),(unsigned char)(pts>>22),(unsigned char)(1|((pts>>14)&0xfe)),(unsigned char)(pts>>7),(unsigned char)(1|((pts<<1)&0xfe))};
        size_t total=length+14,offset=0;bool beginning=true;
        std::vector<unsigned char> batch;batch.reserve((total/176+2)*188);
        while(offset<total) {
            std::array<unsigned char,188> packet;packet.fill(0xff);
            packet[0]=0x47;packet[1]=(beginning?0x40:0)|1;packet[2]=0;packet[3]=videoCounter++&15;
            size_t payload=std::min(total-offset,(size_t)(beginning?176:184));size_t start=4;
            if(beginning||payload<184){packet[3]|=0x30;int padding=183-(int)payload;packet[4]=(unsigned char)padding;
                if(padding>0)packet[5]=beginning?(0x10|(key?0x40:0)|(first?0x80:0)):0;
                if(beginning){packet[6]=(unsigned char)(pts>>25);packet[7]=(unsigned char)(pts>>17);packet[8]=(unsigned char)(pts>>9);packet[9]=(unsigned char)(pts>>1);packet[10]=(unsigned char)((pts<<7)|0x7e);packet[11]=0;}
                start+=padding+1;
            }else packet[3]|=0x10;
            for(size_t i=0;i<payload;i++){size_t p=offset+i;packet[start+i]=p<14?header[p]:bytes[p-14];}
            batch.insert(batch.end(),packet.begin(),packet.end());offset+=payload;beginning=false;
        }
        write(batch.data(),batch.size(),cancelled);first=false;
    }
};
