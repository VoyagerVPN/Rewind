const fs=require('node:fs'),path=require('node:path'),cp=require('node:child_process');
const root=path.resolve(__dirname,'..'),cache=path.join(root,'.cache'),dir=path.join(cache,'capture-worker');fs.mkdirSync(dir,{recursive:true});
const identity=require('./build-identity.cjs').prepareBuildIdentity(root);
fs.mkdirSync(path.join(cache,'tmp'),{recursive:true});
function run(exe,args,extra={}){let r=cp.spawnSync(exe,args,{cwd:dir,shell:false,windowsHide:true,encoding:'utf8',...extra});if(r.status!==0)throw new Error(r.stdout+r.stderr);return r.stdout;}
const vs=run('C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe',['-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath']).trim();
const vc=path.join(vs,'VC/Tools/MSVC'),tools=path.join(vc,fs.readdirSync(vc).sort().at(-1));
const sdk='C:/Program Files (x86)/Windows Kits/10',version=fs.readdirSync(path.join(sdk,'Include')).filter(s=>s.startsWith('10.')).sort().at(-1);
const env={...process.env,INCLUDE:[path.join(tools,'include'),...['ucrt','shared','um','winrt','cppwinrt'].map(p=>path.join(sdk,'Include',version,p))].join(';'),LIB:[path.join(tools,'lib/x64'),...['ucrt','um'].map(p=>path.join(sdk,'Lib',version,p,'x64'))].join(';'),TEMP:path.join(cache,'tmp'),TMP:path.join(cache,'tmp')};
async function build(){
 const header=path.join(dir,'nvEncodeAPI.h');
 if(!fs.existsSync(header)){
  const url='https://raw.githubusercontent.com/FFmpeg/nv-codec-headers/n13.0.19.0/include/ffnvcodec/nvEncodeAPI.h';
  const response=await fetch(url);if(!response.ok)throw new Error('NVENC header download: '+response.status);
  fs.writeFileSync(header,await response.text());
 }
 console.log(run(path.join(tools,'bin/Hostx64/x64/cl.exe'),['/nologo','/c','/std:c++20','/O2','/EHsc','/MT','/I'+dir,'/Fo'+path.join(dir,'CaptureWorker.obj'),path.join(root,'src/NativeCapture/CaptureWorker.cpp')],{env}));
 console.log(run(path.join(sdk,'bin',version,'x64/rc.exe'),['/nologo','/i',identity.directory,'/fo'+path.join(dir,'RewindCapture.res'),path.join(root,'src/NativeCapture/RewindCapture.rc')],{env}));
 console.log(run(path.join(tools,'bin/Hostx64/x64/link.exe'),['/nologo','/SUBSYSTEM:WINDOWS','/ENTRY:wWinMainCRTStartup','/INCREMENTAL:NO','/OUT:'+path.join(dir,'Rewind.CaptureWorker.exe'),path.join(dir,'CaptureWorker.obj'),path.join(dir,'RewindCapture.res'),'d3d11.lib','dxgi.lib','kernel32.lib','user32.lib','shell32.lib','d3dcompiler.lib','avrt.lib','windowsapp.lib','runtimeobject.lib'],{env}));
}
build().catch(error=>{console.error(error);process.exitCode=1;});
