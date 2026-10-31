const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
module.exports = async () => {
  const root = process.env.BROWSER_FIXTURE_DIR;
  if (root && path.dirname(root) === os.tmpdir() && path.basename(root).startsWith('file-browser-e2e-'))
    fs.rmSync(root, { recursive: true, force: true });
};
