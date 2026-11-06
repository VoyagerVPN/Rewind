#pragma once

// Два коротких shader-прохода записывают плоскости NV12 непосредственно на GPU.
class GpuVideoConvert {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> luminance,chrominance;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11Texture2D> target;
    ComPtr<ID3D11ShaderResourceView> inputView;
    ComPtr<ID3D11RenderTargetView> yView,uvView;
    ID3D11Texture2D* inputTexture=nullptr;
    UINT width,height;
public:
    GpuVideoConvert(ID3D11Device* d,ID3D11DeviceContext* c,UINT w,UINT h):device(d),context(c),width(w),height(h) {
        const char* shader=R"(
Texture2D image:register(t0);SamplerState sampling:register(s0);
struct Vertex{float4 position:SV_Position;float2 uv:TEXCOORD0;};
Vertex vs(uint id:SV_VertexID){Vertex v;v.uv=float2((id<<1)&2,id&2);v.position=float4(v.uv*float2(2,-2)+float2(-1,1),0,1);return v;}
float4 psY(Vertex v):SV_Target{float3 rgb=image.Sample(sampling,v.uv).rgb;return float4(dot(rgb,float3(.256788,.504129,.097906))+16.0/255,0,0,1);}
float4 psUV(Vertex v):SV_Target{float3 rgb=image.Sample(sampling,v.uv).rgb;return float4(dot(rgb,float3(-.148223,-.290993,.439216))+128.0/255,dot(rgb,float3(.439216,-.367788,-.071427))+128.0/255,0,1);}
)";
        ComPtr<ID3DBlob> vs,y,uv,error;
        dx(D3DCompile(shader,std::strlen(shader),nullptr,nullptr,nullptr,"vs","vs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&vs,&error));
        dx(D3DCompile(shader,std::strlen(shader),nullptr,nullptr,nullptr,"psY","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&y,&error));
        dx(D3DCompile(shader,std::strlen(shader),nullptr,nullptr,nullptr,"psUV","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&uv,&error));
        dx(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));
        dx(device->CreatePixelShader(y->GetBufferPointer(),y->GetBufferSize(),nullptr,&luminance));
        dx(device->CreatePixelShader(uv->GetBufferPointer(),uv->GetBufferSize(),nullptr,&chrominance));
        D3D11_TEXTURE2D_DESC desc={};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_NV12;
        desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_RENDER_TARGET;
        dx(device->CreateTexture2D(&desc,nullptr,&target));
        D3D11_RENDER_TARGET_VIEW_DESC view={};view.ViewDimension=D3D11_RTV_DIMENSION_TEXTURE2D;view.Format=DXGI_FORMAT_R8_UNORM;
        dx(device->CreateRenderTargetView(target.Get(),&view,&yView));view.Format=DXGI_FORMAT_R8G8_UNORM;dx(device->CreateRenderTargetView(target.Get(),&view,&uvView));
        D3D11_SAMPLER_DESC sample={};sample.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sample.AddressU=sample.AddressV=sample.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sample.MaxLOD=D3D11_FLOAT32_MAX;dx(device->CreateSamplerState(&sample,&sampler));
    }
    ID3D11Texture2D* convert(ID3D11Texture2D* source) {
        if(inputTexture!=source){inputView.Reset();dx(device->CreateShaderResourceView(source,nullptr,&inputView));inputTexture=source;}
        // Результат предыдущего GPU-прохода ещё привязан на запись; иначе D3D11 обнулит входной SRV.
        context->OMSetRenderTargets(0,nullptr,nullptr);
        context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vertex.Get(),nullptr,0);
        context->OMSetBlendState(nullptr,nullptr,0xffffffff);context->OMSetDepthStencilState(nullptr,0);
        ID3D11ShaderResourceView* input=inputView.Get();ID3D11SamplerState* sampling=sampler.Get();context->PSSetShaderResources(0,1,&input);context->PSSetSamplers(0,1,&sampling);
        ID3D11RenderTargetView* views[]={yView.Get(),uvView.Get()};ID3D11PixelShader* shaders[]={luminance.Get(),chrominance.Get()};
        for(UINT plane=0;plane<2;plane++){D3D11_VIEWPORT viewport={0,0,(float)(width>>plane),(float)(height>>plane),0,1};
            context->RSSetViewports(1,&viewport);context->OMSetRenderTargets(1,&views[plane],nullptr);context->PSSetShader(shaders[plane],nullptr,0);context->Draw(3,0);}
        ID3D11ShaderResourceView* empty=nullptr;context->PSSetShaderResources(0,1,&empty);context->OMSetRenderTargets(0,nullptr,nullptr);return target.Get();
    }
};

static int videoConvertCheck(){
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    dx(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
    const unsigned char colors[4][3]={{0,0,0},{255,255,255},{0,0,255},{28,28,28}};
    std::vector<unsigned char> pixels(8*2*4);
    for(UINT y=0;y<2;y++)for(UINT x=0;x<8;x++){auto offset=(y*8+x)*4;for(int c=0;c<3;c++)pixels[offset+c]=colors[x/2][c];pixels[offset+3]=255;}
    D3D11_TEXTURE2D_DESC desc={};desc.Width=8;desc.Height=2;desc.ArraySize=desc.MipLevels=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;
    D3D11_SUBRESOURCE_DATA data={pixels.data(),8*4,0};ComPtr<ID3D11Texture2D> input;dx(device->CreateTexture2D(&desc,&data,&input));
    GpuVideoConvert converter(device.Get(),context.Get(),8,2);ID3D11Texture2D* output=converter.convert(input.Get());
    output->GetDesc(&desc);desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;dx(device->CreateTexture2D(&desc,nullptr,&staging));context->CopyResource(staging.Get(),output);
    D3D11_MAPPED_SUBRESOURCE mapped={};dx(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
    const int expectedY[]={16,235,81,40},expectedU[]={128,128,90,128},expectedV[]={128,128,240,128};bool correct=true;
    const unsigned char* bytes=(const unsigned char*)mapped.pData;
    for(UINT block=0;block<4;block++){
        for(UINT row=0;row<2;row++)for(UINT column=0;column<2;column++)correct&=std::abs((int)bytes[row*mapped.RowPitch+block*2+column]-expectedY[block])<=2;
        correct&=std::abs((int)bytes[mapped.RowPitch*2+block*2]-expectedU[block])<=2;
        correct&=std::abs((int)bytes[mapped.RowPitch*2+block*2+1]-expectedV[block])<=2;
    }
    context->Unmap(staging.Get(),0);if(!correct)throw std::runtime_error("NV12 limited-range BT.601 colors changed");
    // В реальном уменьшенном профиле перед NV12 выполняется shader-проход масштабирования.
    ComPtr<ID3D11ShaderResourceView> sourceView;dx(device->CreateShaderResourceView(input.Get(),nullptr,&sourceView));
    GpuTransform transform(device.Get(),context.Get(),8,2);
    output=converter.convert(transform.render(sourceView.Get(),8,2,DXGI_MODE_ROTATION_IDENTITY));
    context->CopyResource(staging.Get(),output);dx(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
    bytes=(const unsigned char*)mapped.pData;correct=true;
    for(UINT block=0;block<4;block++){
        for(UINT row=0;row<2;row++)for(UINT column=0;column<2;column++)correct&=std::abs((int)bytes[row*mapped.RowPitch+block*2+column]-expectedY[block])<=2;
        correct&=std::abs((int)bytes[mapped.RowPitch*2+block*2]-expectedU[block])<=2;
        correct&=std::abs((int)bytes[mapped.RowPitch*2+block*2+1]-expectedV[block])<=2;
    }
    context->Unmap(staging.Get(),0);if(!correct)throw std::runtime_error("NV12 conversion after GPU transform produced incorrect colors");
    std::printf("{\"nv12Color\":true,\"afterGpuTransform\":true,\"tinyReadbackOnly\":true}\n");return 0;
}
