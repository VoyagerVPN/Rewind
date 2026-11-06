#pragma once
#include <d3dcompiler.h>

// Масштабирование, поворот и курсор выполняются одним проходом GPU.
class GpuTransform {
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VertexShader> vertex;ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11SamplerState> sampler;ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11Texture2D> target,cursor;
    ComPtr<ID3D11RenderTargetView> targetView;ComPtr<ID3D11ShaderResourceView> cursorView;
    UINT width,height,cursorWidth=0,cursorHeight=0;DXGI_OUTDUPL_POINTER_SHAPE_INFO shape={};
    POINT position={};bool visible=false;
    struct Parameters {float output[2],logical[2],cursorPosition[2],cursorSize[2];UINT rotation,mode,show,pad;};
public:
    GpuTransform(ID3D11Device* d,ID3D11DeviceContext* c,UINT w,UINT h):device(d),context(c),width(w),height(h) {
        const char* shader=R"(
Texture2D desktop:register(t0);Texture2D pointerImage:register(t1);SamplerState linearSample:register(s0);
cbuffer Params:register(b0){float2 outputSize;float2 logicalSize;float2 pointerPosition;float2 pointerSize;uint rotation;uint mode;uint show;uint pad;};
struct Vertex{float4 position:SV_Position;float2 uv:TEXCOORD0;};
Vertex vs(uint id:SV_VertexID){Vertex v;v.uv=float2((id<<1)&2,id&2);v.position=float4(v.uv*float2(2,-2)+float2(-1,1),0,1);return v;}
float4 ps(Vertex v):SV_Target{
 float2 uv=v.uv;float2 source=uv;if(rotation==2)source=float2(uv.y,1-uv.x);if(rotation==3)source=1-uv;if(rotation==4)source=float2(1-uv.y,uv.x);
 float4 color=desktop.Sample(linearSample,source);float2 p=floor(uv*logicalSize)-pointerPosition;
 if(show&&all(p>=0)&&all(p<pointerSize)){
  float4 mouse=pointerImage.Load(int3(p,0));
  if(mode==1)color.rgb=mouse.rgb*mouse.a+color.rgb*(1-mouse.a);
  if(mode==2)color.rgb=mouse.a>.5?float3((uint3(round(color.rgb*255))^uint3(round(mouse.rgb*255))))/255:mouse.rgb;
  if(mode==3){uint3 background=uint3(round(color.rgb*255));uint mask=mouse.a>.5?255:0;color.rgb=float3((background&mask)^uint3(round(mouse.rgb*255)))/255;}
 }return float4(color.rgb,1);
})";
        ComPtr<ID3DBlob> vs,ps,error;dx(D3DCompile(shader,std::strlen(shader),nullptr,nullptr,nullptr,"vs","vs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&vs,&error));
        dx(D3DCompile(shader,std::strlen(shader),nullptr,nullptr,nullptr,"ps","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&ps,&error));
        dx(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));dx(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel));
        D3D11_SAMPLER_DESC sample={};sample.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sample.AddressU=sample.AddressV=sample.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sample.MaxLOD=D3D11_FLOAT32_MAX;
        dx(device->CreateSamplerState(&sample,&sampler));D3D11_BUFFER_DESC buffer={};buffer.ByteWidth=sizeof(Parameters);buffer.Usage=D3D11_USAGE_DEFAULT;buffer.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        dx(device->CreateBuffer(&buffer,nullptr,&constants));
        D3D11_TEXTURE2D_DESC desc={};desc.Width=w;desc.Height=h;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        dx(device->CreateTexture2D(&desc,nullptr,&target));dx(device->CreateRenderTargetView(target.Get(),nullptr,&targetView));
    }
    void pointer(IDXGIOutputDuplication* duplication,const DXGI_OUTDUPL_FRAME_INFO& frame) {
        if(frame.LastMouseUpdateTime.QuadPart){position=frame.PointerPosition.Position;visible=frame.PointerPosition.Visible!=FALSE;}
        if(!frame.PointerShapeBufferSize)return;
        std::vector<unsigned char> bytes(frame.PointerShapeBufferSize);UINT required;
        DXGI_OUTDUPL_POINTER_SHAPE_INFO received={};dx(duplication->GetFramePointerShape((UINT)bytes.size(),bytes.data(),&required,&received));
        pointerPixels(received,bytes,position,visible);
    }
    void pointerPixels(DXGI_OUTDUPL_POINTER_SHAPE_INFO received,const std::vector<unsigned char>& bytes,POINT point,bool showing) {
        shape=received;position=point;visible=showing;
        cursorWidth=shape.Width;cursorHeight=shape.Type==DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME?shape.Height/2:shape.Height;
        if(!cursorWidth||!cursorHeight||cursorWidth>1024||cursorHeight>1024)throw std::runtime_error("Invalid cursor shape");
        if(shape.Pitch<(shape.Type==DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME?(cursorWidth+7)/8:cursorWidth*4)||bytes.size()<(size_t)shape.Pitch*shape.Height)throw std::runtime_error("Truncated cursor shape");
        std::vector<unsigned char> rgba((size_t)cursorWidth*cursorHeight*4);
        for(UINT y=0;y<cursorHeight;y++)for(UINT x=0;x<cursorWidth;x++){
            unsigned char* p=rgba.data()+((size_t)y*cursorWidth+x)*4;
            if(shape.Type==DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME){bool a=bytes[(size_t)y*shape.Pitch+x/8]&(0x80>>(x%8));bool b=bytes[(size_t)(y+cursorHeight)*shape.Pitch+x/8]&(0x80>>(x%8));p[0]=p[1]=p[2]=b?255:0;p[3]=a?255:0;}
            else{const unsigned char* source=bytes.data()+(size_t)y*shape.Pitch+x*4;p[0]=source[2];p[1]=source[1];p[2]=source[0];p[3]=source[3];}
        }
        D3D11_TEXTURE2D_DESC desc={};desc.Width=cursorWidth;desc.Height=cursorHeight;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_IMMUTABLE;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data={rgba.data(),cursorWidth*4,0};cursorView.Reset();cursor.Reset();dx(device->CreateTexture2D(&desc,&data,&cursor));dx(device->CreateShaderResourceView(cursor.Get(),nullptr,&cursorView));
    }
    ID3D11Texture2D* render(ID3D11ShaderResourceView* desktop,UINT logicalWidth,UINT logicalHeight,DXGI_MODE_ROTATION rotation) {
        Parameters p={{(float)width,(float)height},{(float)logicalWidth,(float)logicalHeight},{(float)position.x,(float)position.y},{(float)cursorWidth,(float)cursorHeight},(UINT)rotation,
            shape.Type==DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR?1U:shape.Type==DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR?2U:3U,visible&&cursorView?1U:0U,0};
        context->UpdateSubresource(constants.Get(),0,nullptr,&p,0,0);ID3D11Buffer* buffer=constants.Get();context->PSSetConstantBuffers(0,1,&buffer);
        ID3D11ShaderResourceView* views[]={desktop,cursorView.Get()};context->PSSetShaderResources(0,2,views);ID3D11SamplerState* sampling=sampler.Get();context->PSSetSamplers(0,1,&sampling);
        ID3D11RenderTargetView* destination=targetView.Get();context->OMSetRenderTargets(1,&destination,nullptr);D3D11_VIEWPORT viewport={0,0,(float)width,(float)height,0,1};context->RSSetViewports(1,&viewport);
        context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vertex.Get(),nullptr,0);context->PSSetShader(pixel.Get(),nullptr,0);context->Draw(3,0);
        ID3D11ShaderResourceView* empty[2]={};context->PSSetShaderResources(0,2,empty);return target.Get();
    }
};
