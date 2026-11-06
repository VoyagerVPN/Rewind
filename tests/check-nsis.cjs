const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { buildSetup } = require('../build/build-nsis.cjs');

const root = path.resolve(__dirname, '..');
const cache = path.join(root, '.cache', 'nsis');
fs.mkdirSync(cache, { recursive: true });
const fixture = fs.mkdtempSync(path.join(cache, 'test-'));
const payload = path.join(fixture, 'payload');
const target = path.join(fixture, 'installed');
const setup = path.join(fixture, 'Rewind-QA-Setup.exe');
const product = 'Rewind NSIS QA ' + path.basename(fixture);
const run = (exe, args) => spawnSync(exe, args, {
  cwd: fixture, shell: false, windowsHide: true, windowsVerbatimArguments: true, timeout: 30_000,
  env: { ...process.env, TEMP: fixture, TMP: fixture },
});
const dataDirectory = path.join(fixture, 'Данные Rewind');
const replayDirectory = path.join(fixture, 'Мои повторы');
const install = () => run(setup, ['/S', '/DATA="' + dataDirectory + '"', '/REPLAYS="' + replayDirectory + '"', '/D=' + target]);
fs.mkdirSync(path.join(payload, 'capture'), { recursive: true });
fs.writeFileSync(path.join(payload, 'Rewind.exe'), 'fixture app');
fs.writeFileSync(path.join(payload, 'capture', 'worker.exe'), 'fixture worker');
buildSetup(process.argv[2], payload, setup, product);

fs.mkdirSync(target);
const foreign = path.join(target, 'keep.txt');
fs.writeFileSync(foreign, 'unrelated data');
const refused = install();
assert.equal(refused.status, 1, refused.error?.message);
assert.equal(fs.readFileSync(foreign, 'utf8'), 'unrelated data');
assert.equal(fs.existsSync(path.join(target, 'Rewind.exe')), false);
fs.unlinkSync(foreign);

const options = path.join(fixture, 'Выбранные папки.txt');
fs.writeFileSync(options, '\uFEFF' + [target, dataDirectory, replayDirectory, '0', ''].join('\r\n'), 'utf16le');
const installed = run(setup, ['/S', '/OPTIONS="' + options + '"']);
assert.equal(installed.status, 0, installed.error?.message);
assert.equal(fs.readFileSync(path.join(target, 'capture', 'worker.exe'), 'utf8'), 'fixture worker');
assert.equal(install().status, 1, 'Повторная установка должна сохранить текущую копию');

assert.equal(fs.readFileSync(path.join(target, 'install-paths.txt'), 'utf16le'), '\uFEFF' + dataDirectory + '\r\n' + replayDirectory + '\r\n');
const settings = path.join(dataDirectory, 'settings.json');
const clip = path.join(replayDirectory, 'saved.mp4');
fs.mkdirSync(replayDirectory);
fs.writeFileSync(clip, 'clip to preserve');
fs.mkdirSync(path.dirname(settings));
fs.writeFileSync(settings, '{"test":true}');
const uninstall = path.join(target, 'Uninstall.exe');
assert.equal(run(uninstall, ['/S']).status, 0);
const timer = new Int32Array(new SharedArrayBuffer(4));
for (let attempt = 0; attempt < 100 && fs.existsSync(uninstall); attempt++) {
  Atomics.wait(timer, 0, 0, 100);
}
assert.equal(fs.existsSync(uninstall), false);
assert.equal(fs.existsSync(path.join(target, 'Rewind.exe')), false);
assert.equal(fs.existsSync(path.join(target, 'capture')), false);
assert.equal(fs.readFileSync(settings, 'utf8'), '{"test":true}');
assert.equal(fs.readFileSync(clip, 'utf8'), 'clip to preserve');
assert.equal(fs.existsSync(path.join(target, 'install-paths.txt')), false);
console.log('PASS NSIS: отказ для занятой папки, установка, повторный запуск, удаление с сохранением Data');
