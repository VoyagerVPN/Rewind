const path = require('node:path');
function restoreWindowsEnvironment() {
  const drive = path.parse(process.env.SystemRoot || 'C:\\Windows').root;
  const defaults = {
    ProgramData: path.join(drive, 'ProgramData'),
    ProgramFiles: path.join(drive, 'Program Files'),
    'ProgramFiles(x86)': path.join(drive, 'Program Files (x86)'),
    ProgramW6432: path.join(drive, 'Program Files'),
    CommonProgramFiles: path.join(drive, 'Program Files', 'Common Files'),
    'CommonProgramFiles(x86)': path.join(drive, 'Program Files (x86)', 'Common Files'),
    APPDATA: path.join(process.env.USERPROFILE, 'AppData', 'Roaming'),
  };
  // Некоторые среды запуска передают сокращённый набор системных переменных.
  for (const [key, value] of Object.entries(defaults)) if (!process.env[key]) process.env[key] = value;
}
module.exports = { restoreWindowsEnvironment };
