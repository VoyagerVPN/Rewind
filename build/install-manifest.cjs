const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const digest = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
function createManifest(directory, identity) {
  const files = [];
  function visit(current) {
    for (const entry of fs.readdirSync(current, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
      if (current === directory && ['Data', '.cache', 'install-manifest.json'].includes(entry.name)) continue;
      const file = path.join(current, entry.name);
      if (entry.isDirectory()) visit(file);
      else if (entry.isFile()) files.push({ path: path.relative(directory, file).replaceAll('\\', '/'), sha256: digest(file), bytes: fs.statSync(file).size });
    }
  }
  visit(directory);
  return { schema: 1, ...identity, files };
}
function verifyManifest(directory, manifest) {
  for (const entry of manifest.files) {
    if (path.isAbsolute(entry.path) || entry.path.includes('\\') || entry.path.split('/').some(part => ['..', '', 'Data', '.cache'].includes(part)))
      throw new Error('Unsafe manifest path: ' + entry.path);
    const file = path.join(directory, entry.path);
    if (!fs.existsSync(file) || digest(file) !== entry.sha256) throw new Error('Installation mismatch: ' + entry.path);
  }
}
module.exports = { createManifest, verifyManifest };
