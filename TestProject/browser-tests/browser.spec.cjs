const { test, expect } = require('@playwright/test');

async function enableTurnstileDemo(page, interactionRequired = false) {
  await page.route('**/api/config', async route => {
    const response = await route.fetch();
    const config = await response.json();
    config.demo = {
      uploadLimitMiB: 100, requestBurst: 30, requestRefillCount: 30, requestRefillSeconds: 30,
      maxConcurrentRequests: 4, creationsPerWindow: 2, creationWindowMinutes: 5,
      mutationsPerWindow: 20, idleSleepMinutes: 15, turnstileReady: true,
      turnstileSiteKey: 'test-site-key'
    };
    await route.fulfill({ response, json: config });
  });
  await page.addInitScript((requiresInteraction) => {
    let callbacks;
    globalThis.window.turnstile = {
      render(_container, options) { callbacks = options; return 'test-widget'; },
      execute() {
        if (requiresInteraction) callbacks['before-interactive-callback']();
        setTimeout(() => callbacks.callback('test-token'), 600);
      },
      reset() {}
    };
  }, interactionRequired);
}

test('deep links, reload, navigation, history, and dialog closure', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('/?open=1&path=Alpha&q=report&sort=size&direction=desc');
  await expect(page.locator('#browser-dialog')).toBeVisible();
  await expect(page.locator('th[data-sort-column="size"]')).toHaveAttribute('aria-sort', 'descending');
  await expect(page.locator('#entries')).toContainText('report.txt');
  await page.reload();
  await expect(page.locator('#search-input')).toHaveValue('report');
  await expect(page.locator('#entries')).toContainText('report.txt');
  await page.locator('#search-input').fill('');
  await expect(page.getByRole('button', { name: '📁 Nested', exact: true })).toBeVisible();
  await page.getByRole('button', { name: '📁 Nested', exact: true }).click();
  await expect(page.locator('#entries')).toContainText('report.txt');
  await page.goBack();
  await expect(page.locator('#entries')).toContainText('Nested');
  await page.goForward();
  await expect(page.locator('#entries')).toContainText('report.txt');
  await page.keyboard.press('Escape');
  await expect(page.locator('#browser-dialog')).not.toBeVisible();
  await expect(page.locator('#open-browser')).toBeFocused();
  expect(new URL(page.url()).searchParams.has('open')).toBe(false);
  expect(errors).toEqual([]);
});

test('generated-folder search exclusion defaults on and can be disabled in a deep link', async ({ page }) => {
  await page.goto('/?open=1&q=generated-target');
  const checkbox = page.locator('#skip-generated');
  await expect(checkbox).toBeChecked();
  await expect(page.locator('#entries')).not.toContainText('generated-target.txt');
  await expect(page.locator('#summary')).toContainText('generated folders skipped');

  await checkbox.uncheck();
  await expect(page.locator('#entries')).toContainText('generated-target.txt');
  expect(new URL(page.url()).searchParams.get('includeGenerated')).toBe('1');
  await page.reload();
  await expect(checkbox).not.toBeChecked();
  await expect(page.locator('#entries')).toContainText('generated-target.txt');
});

test('failed navigation clears stale entries and disables uploads', async ({ page }) => {
  await page.goto('/?open=1&path=Alpha');
  await expect(page.locator('#entries')).toContainText('Nested');
  await page.evaluate(() => {
    globalThis.history.pushState({}, '', '/?open=1&path=Missing');
    globalThis.dispatchEvent(new globalThis.PopStateEvent('popstate'));
  });
  await expect(page.locator('#status')).toContainText('does not exist');
  await expect(page.locator('#entries tr')).toHaveCount(0);
  await expect(page.locator('#summary')).toBeEmpty();
  await expect(page.locator('#upload-file')).toBeDisabled();
  await expect(page.locator('#next-page')).toBeDisabled();
  await page.goBack();
  await expect(page.locator('#entries')).toContainText('Nested');
  await expect(page.locator('#upload-file')).toBeEnabled();
});

test('browser upload/download plus copy, move, cancel delete, and delete', async ({ page }) => {
  await page.goto('/?open=1&path=Alpha');
  await expect(page.locator('#upload-file')).toBeEnabled();
  await page.locator('#upload-file').setInputFiles({ name: 'browser.txt', mimeType: 'text/plain', buffer: Buffer.from('browser transfer bytes') });
  await page.getByRole('button', { name: 'Upload', exact: true }).click();
  let row = page.locator('tr[data-path="Alpha/browser.txt"]');
  await expect(row).toBeVisible();
  const downloading = page.waitForEvent('download');
  await row.getByRole('link', { name: 'Download browser.txt', exact: true }).click();
  const download = await downloading;
  const chunks = [];
  for await (const chunk of await download.createReadStream()) chunks.push(chunk);
  expect(Buffer.concat(chunks).toString()).toBe('browser transfer bytes');
  await row.locator('summary').click();
  page.once('dialog', dialog => dialog.accept('Alpha/copied.txt'));
  await row.getByRole('button', { name: 'Copy browser.txt', exact: true }).click();
  row = page.locator('tr[data-path="Alpha/copied.txt"]');
  await expect(row).toBeVisible();
  await row.locator('summary').click();
  await row.getByRole('button', { name: 'Move copied.txt', exact: true }).click();
  await page.locator('#move-breadcrumbs').getByRole('button', { name: 'Root', exact: true }).click();
  await page.locator('#move-submit').click();
  await page.locator('#breadcrumbs').getByRole('button', { name: 'Root', exact: true }).click();
  row = page.locator('tr[data-path="copied.txt"]');
  await expect(row).toBeVisible();
  await row.locator('summary').click();
  page.once('dialog', dialog => dialog.dismiss());
  await row.getByRole('button', { name: 'Delete copied.txt', exact: true }).click();
  await expect(row).toBeVisible();
  page.once('dialog', dialog => dialog.accept());
  await row.getByRole('button', { name: 'Delete copied.txt', exact: true }).click();
  await expect(row).toHaveCount(0);
  await page.getByRole('button', { name: '📁 Alpha', exact: true }).click();
  await expect(page.locator('tr[data-path="Alpha/browser.txt"]')).toBeVisible();
});

test('routine Turnstile checks use the app loading mask without showing the widget spinner', async ({ page }) => {
  await enableTurnstileDemo(page);
  await page.goto('/?open=1&path=Alpha/Nested');
  const source = page.locator('tr[data-path="Alpha/Nested/report.txt"]');
  await expect(source).toBeVisible();
  await source.locator('summary').click();
  let suggestedDestination;
  page.once('dialog', dialog => {
    suggestedDestination = dialog.defaultValue();
    dialog.accept(suggestedDestination);
  });
  await source.getByRole('button', { name: 'Copy report.txt', exact: true }).click();
  expect(suggestedDestination).toBe('Alpha/Nested/report (copy).txt');
  await expect(page.locator('#security-check-overlay')).toBeHidden();
  await expect(page.locator('#browser-loading-mask')).toBeVisible();
  await expect(page.locator('tr[data-path="Alpha/Nested/report (copy).txt"]')).toBeVisible();
  await expect(page.locator('#browser-loading-mask')).toBeHidden();
});

test('interactive Turnstile challenges replace the loading mask and hand back to it after success', async ({ page }) => {
  await enableTurnstileDemo(page, true);
  await page.request.post('/api/files/upload?path=Alpha/Nested', {
    multipart: { file: { name: 'interactive-source.txt', mimeType: 'text/plain', buffer: Buffer.from('interactive challenge bytes') } }
  });
  await page.route('**/api/files/copy', async route => {
    await new Promise(resolve => setTimeout(resolve, 500));
    await route.continue();
  });
  await page.goto('/?open=1&path=Alpha/Nested');
  const source = page.locator('tr[data-path="Alpha/Nested/interactive-source.txt"]');
  await source.locator('summary').click();
  page.once('dialog', dialog => dialog.accept('Alpha/Nested/interactive-copy.txt'));
  await source.getByRole('button', { name: 'Copy interactive-source.txt', exact: true }).click();
  await expect(page.locator('#security-check-overlay')).toBeVisible();
  await expect(page.locator('#browser-loading-mask')).toBeHidden();
  await expect(page.locator('#security-check-overlay')).toBeHidden();
  await expect(page.locator('#browser-loading-mask')).toBeVisible();
  await expect(page.locator('tr[data-path="Alpha/Nested/interactive-copy.txt"]')).toBeVisible();
  await expect(page.locator('#browser-loading-mask')).toBeHidden();
});

test('drag and drop moves entries and Undo restores the previous path', async ({ page }) => {
  await enableTurnstileDemo(page);
  await page.route('**/api/files/move', async route => {
    await new Promise(resolve => setTimeout(resolve, 500));
    await route.continue();
  });
  await page.goto('/?open=1');
  await expect(page.locator('.drag-hint')).toContainText('Drag a file or folder');
  await page.locator('#upload-file').setInputFiles({ name: 'drag-me.txt', mimeType: 'text/plain', buffer: Buffer.from('drag bytes') });
  await page.getByRole('button', { name: 'Upload', exact: true }).click();
  const source = page.locator('tr[data-path="drag-me.txt"]');
  await source.locator('.file-name').dragTo(page.locator('tr[data-path="Alpha"] .entry-name'));
  await expect(page.locator('#status')).toContainText('Completing the security check');
  await expect(page.locator('#security-check-overlay')).toBeHidden();
  await expect(page.locator('#browser-loading-mask')).toBeVisible();
  await expect(page.locator('#status')).toContainText('Moved drag-me.txt to Alpha/drag-me.txt.');
  await expect(page.locator('#browser-loading-mask')).toBeHidden();
  await page.getByRole('button', { name: 'Undo move' }).click();
  await expect(page.locator('#status')).toContainText('Completing the security check');
  await expect(page.locator('#security-check-overlay')).toBeHidden();
  await expect(page.locator('#browser-loading-mask')).toBeVisible();
  await expect(page.locator('#status')).toContainText('Move undone. Returned Alpha/drag-me.txt to drag-me.txt.');
  await expect(page.locator('#browser-loading-mask')).toBeHidden();
  await expect(page.locator('tr[data-path="drag-me.txt"]')).toBeVisible();
});

test('paging reuses a stable snapshot and Refresh sees changes', async ({ page, request }) => {
  await page.goto('/?open=1&path=Many');
  await expect(page.locator('#summary')).toContainText('220 files');
  await request.post('/api/files/upload?path=Many', { multipart: { file: { name: 'new-external.txt', mimeType: 'text/plain', buffer: Buffer.from('new') } } });
  const next = page.waitForRequest(request => request.url().includes('/browse?') && new URL(request.url()).searchParams.get('page') === '2');
  await page.locator('#next-page').click();
  expect(new URL((await next).url()).searchParams.get('snapshot')).toBeTruthy();
  await expect(page.locator('#page-label')).toHaveText('Page 2 of 3');
  await expect(page.locator('#summary')).toContainText('220 files');
  await page.locator('#refresh').click();
  await expect(page.locator('#summary')).toContainText('221 files');
  for (let index = 0; index < 20; index++) {
    await request.get(`/api/files/search?path=Many&q=missing-${index}`);
  }
  const expired = page.waitForResponse(response => response.url().includes('/browse?') && response.status() === 409);
  await page.locator('#next-page').click();
  await expired;
  await expect(page.locator('#page-label')).toHaveText('Page 3 of 3');
  await expect(page.locator('#summary')).toContainText('221 files');
});

test('obsolete search responses cannot replace the latest query', async ({ page }) => {
  await page.goto('/?open=1&path=Many');
  await expect(page.locator('#summary')).toContainText('files');
  let intercepted;
  const seen = new Promise(resolve => { intercepted = resolve; });
  await page.route('**/api/files/search?**', async route => {
    if (new URL(route.request().url()).searchParams.get('q') === 'old-query') {
      intercepted();
      await new Promise(resolve => setTimeout(resolve, 500));
      await route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'obsolete error' }) });
    } else await route.continue();
  });
  await page.locator('#search-input').fill('old-query');
  await seen;
  await page.locator('#search-input').fill('item-219');
  await expect(page.locator('#entries')).toContainText('item-219.txt');
  await expect(page.locator('#entries tr')).toHaveCount(1);
  await expect(page.locator('#status')).toBeEmpty();
});

test('measure bounded browse/search scans versus snapshot paging', async ({ request }) => {
  for (const endpoint of ['browse?path=Perf', 'search?path=Perf&q=perf']) {
    const scans = [], pages = [];
    for (let i = 0; i < 5; i++) {
      let start = performance.now();
      const response = await request.get(`/api/files/${endpoint}`);
      expect(response.ok()).toBe(true);
      const body = await response.json();
      scans.push(performance.now() - start);
      expect(body.summary.fileCount).toBe(6000);
      start = performance.now();
      const next = await request.get(`/api/files/${endpoint}&page=2&snapshot=${body.snapshotId}`);
      expect((await next.json()).snapshotId).toBe(body.snapshotId);
      pages.push(performance.now() - start);
    }
    const median = values => values.sort((a,b) => a-b)[2].toFixed(1);
    console.log(`${endpoint}: 6000 files, scan median ${median(scans)} ms, snapshot page median ${median(pages)} ms (5 samples)`);
  }
});

test('deployment profiles keep local defaults separate from the packaged demo', async ({ page }) => {
  await page.goto('/?open=1&path=Alpha');
  await expect(page.locator('#entries')).toContainText('Nested');
  await expect(page.locator('.free-demo-limits').first()).toBeHidden();
  await expect(page.locator('#upload-file')).toBeEnabled();

  const fs = require('node:fs');
  const path = require('node:path');
  const demoProfile = fs.readFileSync(path.resolve(__dirname, '../../RenderDemo/frontend-deployment.js'), 'utf8');
  await page.route('**/js/deployment.js', route => route.fulfill({ contentType: 'text/javascript', body: demoProfile }));
  const proxiedReads = [];
  await page.route('https://file-explorer-demo-api.onrender.com/**', async route => {
    const remote = new URL(route.request().url());
    expect(route.request().method()).toBe('GET');
    proxiedReads.push(remote.pathname);
    if (remote.pathname === '/api/demo-config') {
      await route.fulfill({ status: 404, headers: { 'access-control-allow-origin': '*' } });
      return;
    }
    const localUrl = new URL(remote.pathname + remote.search, 'http://127.0.0.1:5187');
    const response = await route.fetch({ url: localUrl.toString() });
    await route.fulfill({ response, headers: { ...response.headers(), 'access-control-allow-origin': '*' } });
  });
  await page.reload();
  await expect(page.locator('#entries')).toContainText('Nested');
  await expect(page.locator('.free-demo-limits').first()).toBeVisible();
  await expect(page.locator('#upload-file')).toBeDisabled();
  expect(proxiedReads).toContain('/api/config');
  expect(proxiedReads).toContain('/api/files/browse');
});
