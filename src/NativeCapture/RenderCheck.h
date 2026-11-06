#pragma once
// Публичный режим проверки собственного GPU-рендера без чтения рабочего стола.
static int renderCheck() {
    try {
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
        dx(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
        const unsigned char pixels[]={0,0,255,255,0,255,0,255,255,0,0,255,255,255,0,255,255,0,255,255,0,255,255,255};
        D3D11_TEXTURE2D_DESC desc={};desc.Width=3;desc.Height=2;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_IMMUTABLE;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data={pixels,12,0};ComPtr<ID3D11Texture2D> source;dx(device->CreateTexture2D(&desc,&data,&source));ComPtr<ID3D11ShaderResourceView> view;dx(device->CreateShaderResourceView(source.Get(),nullptr,&view));
        GpuTransform transform(device.Get(),context.Get(),2,3);ID3D11Texture2D* result=transform.render(view.Get(),2,3,DXGI_MODE_ROTATION_ROTATE90);
        D3D11_TEXTURE2D_DESC read={};result->GetDesc(&read);read.Usage=D3D11_USAGE_STAGING;read.BindFlags=0;read.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging;dx(device->CreateTexture2D(&read,nullptr,&staging));context->CopyResource(staging.Get(),result);
        D3D11_MAPPED_SUBRESOURCE mapped;dx(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        const unsigned char expected[]={255,255,0,0,0,255,255,0,255,0,255,0,0,255,255,255,0,0};bool valid=true;
        for(UINT y=0;y<3;y++)for(UINT x=0;x<2;x++)for(UINT channel=0;channel<3;channel++){
            unsigned char actual=((unsigned char*)mapped.pData)[y*mapped.RowPitch+x*4+channel];valid&=std::abs((int)actual-expected[(y*2+x)*3+channel])<=2;
        }
        context->Unmap(staging.Get(),0);
        GpuTransform pointer(device.Get(),context.Get(),3,2);
        auto corner=[&](DXGI_OUTDUPL_POINTER_SHAPE_INFO shape,std::vector<unsigned char> bytes,std::array<unsigned char,3> expectedColor){
            pointer.pointerPixels(shape,bytes,{0,0},true);ID3D11Texture2D* output=pointer.render(view.Get(),3,2,DXGI_MODE_ROTATION_IDENTITY);
            D3D11_TEXTURE2D_DESC desc;output->GetDesc(&desc);desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
            ComPtr<ID3D11Texture2D> read;dx(device->CreateTexture2D(&desc,nullptr,&read));context->CopyResource(read.Get(),output);D3D11_MAPPED_SUBRESOURCE data;dx(context->Map(read.Get(),0,D3D11_MAP_READ,0,&data));
            bool result=true;for(int channel=0;channel<3;channel++)result&=std::abs(((unsigned char*)data.pData)[channel]-expectedColor[channel])<=2;context->Unmap(read.Get(),0);return result;
        };
        DXGI_OUTDUPL_POINTER_SHAPE_INFO cursor={};cursor.Width=cursor.Height=1;cursor.Pitch=4;cursor.Type=DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR;
        bool color=corner(cursor,{0,255,0,255},{0,255,0});
        cursor.Type=DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR;bool masked=corner(cursor,{255,255,255,255},{255,255,0});
        cursor.Type=DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME;cursor.Height=2;cursor.Pitch=1;bool mono=corner(cursor,{0x80,0x80},{255,255,0});
        std::fprintf(stdout,"{\"rotation90\":%s,\"colorCursor\":%s,\"maskedCursor\":%s,\"monochromeCursor\":%s}\n",valid?"true":"false",color?"true":"false",masked?"true":"false",mono?"true":"false");return valid&&color&&masked&&mono?0:1;
    }catch(const std::exception& error){std::fprintf(stderr,"render-check: %s\n",error.what());return 1;}
}
