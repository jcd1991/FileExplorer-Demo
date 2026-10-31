const { defineConfig } = require('@playwright/test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const root = process.env.BROWSER_FIXTURE_DIR || fs.mkdtempSync(path.join(os.tmpdir(), 'file-browser-e2e-'));
process.env.BROWSER_FIXTURE_DIR = root;
fs.mkdirSync(path.join(root, 'Alpha', 'Nested'), { recursive: true });
fs.mkdirSync(path.join(root, 'Many'), { recursive: true });
fs.mkdirSync(path.join(root, 'Perf'), { recursive: true });
fs.writeFileSync(path.join(root, 'Alpha', 'Nested', 'report.txt'), 'report bytes');
for (let i = 0; i < 220; i++) fs.writeFileSync(path.join(root, 'Many', `item-${String(i).padStart(3, '0')}.txt`), 'fixture');
for (let i = 0; i < 6000; i++) fs.writeFileSync(path.join(root, 'Perf', `perf-${String(i).padStart(4, '0')}.txt`), 'fixture');
module.exports = defineConfig({
  testDir: '.', testMatch: '*.spec.cjs', workers: 1, timeout: 30000,
  use: { baseURL: 'http://127.0.0.1:5187', browserName: 'chromium' },
  globalTeardown: require.resolve('./teardown.cjs'),
  webServer: {
    command: 'dotnet run --project ../TestProject.csproj -c Release --no-launch-profile --no-build --urls http://127.0.0.1:5187',
    url: 'http://127.0.0.1:5187/api/config', reuseExistingServer: false,
    env: { FileBrowser__RootPath: root, ASPNETCORE_ENVIRONMENT: 'Production' }
  }
});
