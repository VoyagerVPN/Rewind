const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

function createBuildIdentity(root, now = new Date()) {
  const { version, channel } = JSON.parse(fs.readFileSync(path.join(root, 'version.json'), 'utf8'));
  if (!/^\d+\.\d+\.\d+$/.test(version) || version.split('.').some(n => Number(n) > 65535) || !/^[a-z]+$/.test(channel))
    throw new Error('Invalid Rewind version.json');
  const source = crypto.createHash('sha256');
  function visit(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
      if (['.cache', '.git', '.tgrep', 'bin', 'obj', 'node_modules'].includes(entry.name)) continue;
      const file = path.join(directory, entry.name);
      if (entry.isDirectory()) visit(file);
      else if (entry.isFile()) source.update(path.relative(root, file).replaceAll('\\', '/') + '\0').update(fs.readFileSync(file)).update('\0');
    }
  }
  visit(root);
  const sourceSha256 = source.digest('hex');
  const buildId = now.toISOString().replace(/[-:.]/g, '');
  return { version, channel, buildId, builtUtc: now.toISOString(), sourceSha256,
    fileVersion: version + '.0', informationalVersion: `${version}-${channel}.${buildId}+${sourceSha256.slice(0, 12)}` };
}

function prepareBuildIdentity(root) {
  // Один идентификатор передаётся всем дочерним сборкам комплекта.
  if (process.env.REWIND_BUILD_IDENTITY) {
    const file = path.resolve(process.env.REWIND_BUILD_IDENTITY);
    const metadata = JSON.parse(fs.readFileSync(file, 'utf8'));
    return { ...metadata, directory: path.dirname(file), props: path.join(path.dirname(file), 'BuildIdentity.props') };
  }
  const metadata = createBuildIdentity(root);
  const directory = path.join(root, '.cache', 'build-identity', metadata.buildId);
  fs.mkdirSync(directory, { recursive: true });
  const file = path.join(directory, 'identity.json');
  fs.writeFileSync(file, JSON.stringify(metadata, null, 2) + '\n');
  fs.writeFileSync(path.join(directory, 'BuildIdentity.props'), `<Project><PropertyGroup><Version>${metadata.version}</Version><FileVersion>${metadata.fileVersion}</FileVersion><InformationalVersion>${metadata.informationalVersion}</InformationalVersion><IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion></PropertyGroup></Project>\n`);
  fs.writeFileSync(path.join(directory, 'BuildIdentity.h'), `#define REWIND_FILE_VERSION ${metadata.fileVersion.replaceAll('.', ',')}\n#define REWIND_FILE_VERSION_STRING "${metadata.fileVersion}"\n#define REWIND_PRODUCT_VERSION "${metadata.informationalVersion}"\n`);
  process.env.REWIND_BUILD_IDENTITY = file;
  return { ...metadata, directory, props: path.join(directory, 'BuildIdentity.props') };
}

module.exports = { createBuildIdentity, prepareBuildIdentity };
