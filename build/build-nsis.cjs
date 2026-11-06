const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { createManifest, verifyManifest } = require('./install-manifest.cjs');
const { prepareBuildIdentity } = require('./build-identity.cjs');
const { restoreWindowsEnvironment } = require('./windows-build-env.cjs');

const root = path.resolve(__dirname, '..');
const cache = path.join(root, '.cache');
const nsisCache = path.join(cache, 'nsis');

function run(exe, args, env) {
  execFileSync(exe, args, { cwd: root, shell: false, windowsHide: true, env, stdio: 'inherit' });
}

function nsisText(value) {
  if (/["\r\n]/.test(value)) throw new Error('Недопустимый путь для NSIS: ' + value);
  return value.replaceAll('$', () => '$$');
}

function buildSetup(compiler, payload, output, product = 'Rewind') {
  if (!/^[A-Za-z0-9 -]+$/.test(product)) throw new Error('Недопустимое имя пакета NSIS');
  payload = path.resolve(payload);
  output = path.resolve(output);
  fs.mkdirSync(nsisCache, { recursive: true });
  const work = fs.mkdtempSync(path.join(nsisCache, 'compiler-'));
  const version = JSON.parse(fs.readFileSync(path.join(root, 'version.json'), 'utf8')).version;
  const manifest = createManifest(payload, { version });
  verifyManifest(payload, manifest);
  if (!manifest.files.some(file => file.path === 'Rewind.exe')) throw new Error('В пакете отсутствует Rewind.exe');

  const copy = [];
  const bootstrap = [];
  const remove = [];
  const folders = new Set();
  for (const file of manifest.files) {
    const relative = nsisText(file.path.replaceAll('/', '\\'));
    const directory = path.win32.dirname(relative);
    const destination = directory === '.' ? '$INSTDIR' : '$INSTDIR\\' + directory;
    if (directory === '.' || file.path === 'ru/Rewind.resources.dll') {
      const bootstrapDirectory = directory === '.' ? '$PLUGINSDIR\\ui' : '$PLUGINSDIR\\ui\\' + directory;
      bootstrap.push('SetOutPath "' + bootstrapDirectory + '"', 'File "' + nsisText(path.join(payload, file.path)) + '"', 'IfErrors uiFailed');
    }
    copy.push('SetOutPath "' + destination + '"', 'File "' + nsisText(path.join(payload, file.path)) + '"', 'IfErrors failed');
    remove.push('Delete "$INSTDIR\\' + relative + '"');
    for (let current = directory; current !== '.'; current = path.win32.dirname(current)) folders.add(current);
  }
  const deletes = [...folders].sort((a, b) => b.length - a.length).map(folder => 'RMDir "$INSTDIR\\' + folder + '"');
  const copies = path.join(work, 'copy.nsh');
  const cleanup = path.join(work, 'cleanup.nsh');
  const bootstrapFiles = path.join(work, 'bootstrap.nsh');
  fs.writeFileSync(bootstrapFiles, '\uFEFF' + bootstrap.join('\r\n'));
  fs.writeFileSync(copies, '\uFEFF' + copy.join('\r\n'));
  fs.writeFileSync(cleanup, '\uFEFF' + [
    '!macro RemovePayload', 'Delete "$INSTDIR\\install-paths.txt"', ...remove, 'Push $0', 'StrCpy $0 0', 'IfErrors 0 +2', 'StrCpy $0 1',
    ...deletes, 'ClearErrors', 'StrCmp $0 0 +2', 'SetErrors', 'Pop $0', '!macroend',
    '!macro RemoveRegistration',
    'Delete "$INSTDIR\\Uninstall.exe"',
    'Delete "$SMPROGRAMS\\' + product + '\\Rewind.lnk"',
    'RMDir "$SMPROGRAMS\\' + product + '"',
    'Delete "$SMSTARTUP\\' + product + ' (Setup).lnk"',
    'DeleteRegKey HKCU "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\' + product + '"',
    'SetOutPath "$TEMP"',
    'RMDir "$INSTDIR"', '!macroend', '',
  ].join('\r\n'));

  run(path.resolve(compiler), [
    '/V3', '/WX', '/INPUTCHARSET', 'UTF8', '/DPRODUCT=' + product, '/DVERSION=' + version,
    '/DOUTPUT=' + nsisText(output),
    '/DICON=' + nsisText(path.join(root, 'src', 'ReplayUi', 'Assets', 'Rewind.ico')),
    '/DCOPY_LIST=' + nsisText(copies), '/DCLEANUP=' + nsisText(cleanup),
    '/DBOOTSTRAP_LIST=' + nsisText(bootstrapFiles),
    path.join(root, 'installer', 'rewind.nsi'),
  ], { ...process.env, TEMP: work, TMP: work });
}

function addLicenseFiles(payload, nuget, compiler) {
  const licenses = path.join(payload, 'licenses');
  fs.cpSync(path.join(root, 'licenses'), licenses, { recursive: true });
  const packageFile = (pkg, version, filename) => path.join(nuget, pkg, version, filename);
  const copy = (source, name) => fs.copyFileSync(source, path.join(licenses, name));

  const dependencies = JSON.parse(fs.readFileSync(path.join(payload, 'Rewind.deps.json'), 'utf8')).libraries;
  for (const [name, version] of Object.keys(dependencies).map(library => library.split('/'))) {
    const pkg = name.toLowerCase();
    if (pkg === 'wpf-ui' || pkg === 'wpf-ui.abstractions') {
      copy(packageFile(pkg, version, 'LICENSE.md'), name + '-LICENSE.md');
      copy(packageFile(pkg, version, 'ThirdPartyNotices.txt'), name + '-ThirdPartyNotices.txt');
    }
    if (pkg === 'system.numerics.tensors') {
      copy(packageFile(pkg, version, 'LICENSE.TXT'), 'System.Numerics.Tensors-LICENSE.txt');
      copy(packageFile(pkg, version, 'THIRD-PARTY-NOTICES.TXT'), 'System.Numerics.Tensors-ThirdPartyNotices.txt');
    }
    if (pkg === 'runtimepack.microsoft.netcore.app.runtime.win-x64') {
      const pack = pkg.slice('runtimepack.'.length);
      copy(packageFile(pack, version, 'LICENSE.TXT'), 'NET-Runtime-LICENSE.txt');
      copy(packageFile(pack, version, 'THIRD-PARTY-NOTICES.TXT'), 'NET-Runtime-ThirdPartyNotices.txt');
    }
    if (pkg === 'runtimepack.microsoft.windowsdesktop.app.runtime.win-x64') {
      copy(packageFile(pkg.slice('runtimepack.'.length), version, 'LICENSE'), 'NET-Desktop-LICENSE.txt');
    }
  }
  copy(path.join(path.dirname(path.resolve(compiler)), 'COPYING'), 'NSIS-COPYING.txt');
}

function main(compiler) {
  if (!compiler) throw new Error('Использование: node build/build-nsis.cjs <путь к makensis.exe>');
  restoreWindowsEnvironment();
  const identity = prepareBuildIdentity(root);
  const output = path.join(nsisCache, identity.buildId);
  const temp = path.join(cache, 'tmp');
  fs.mkdirSync(temp, { recursive: true });
  fs.mkdirSync(output, { recursive: true });
  const env = {
    ...process.env,
    REWIND_BUILD_PROPS: identity.props,
    DOTNET_CLI_HOME: path.join(cache, 'dotnet-home'),
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    NUGET_PACKAGES: path.join(cache, 'nuget-packages'),
    NUGET_HTTP_CACHE_PATH: path.join(cache, 'nuget-http'),
    MSBUILDDISABLENODEREUSE: '1',
    TEMP: temp,
    TMP: temp,
  };
  run(process.execPath, [path.join(__dirname, 'build-native-capture.cjs')], env);
  const payload = path.join(output, 'payload');
  run(path.join(process.env.ProgramFiles, 'dotnet', 'dotnet.exe'), [
    'publish', 'src/ReplayUi/ReplayUi.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', '-p:Platform=x64',
    '--configfile', 'src/ReplayUi/NuGet.Config', '--disable-build-servers',
    '--artifacts-path', path.join(cache, 'nsis', 'dotnet'), '--output', payload,
  ], env);
  for (const name of ['LICENSE', 'THIRD_PARTY_NOTICES.md']) fs.copyFileSync(path.join(root, name), path.join(payload, name));
  addLicenseFiles(payload, env.NUGET_PACKAGES, compiler);
  for (const name of ['libvlc.lib', 'libvlccore.lib'])
    fs.rmSync(path.join(payload, 'libvlc', 'win-x64', name), { force: true });
  const setup = path.join(output, 'Rewind-Setup.exe');
  buildSetup(compiler, payload, setup);
  console.log(setup);
}

if (require.main === module) main(process.argv[2]);
module.exports = { buildSetup };
