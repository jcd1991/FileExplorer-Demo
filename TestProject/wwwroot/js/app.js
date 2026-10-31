import { changeEntry, fetchEntries, getApiOrigin, getConfig, setApiOrigin, setMutationTokenProvider, uploadFile } from "./api.js";
import { formatBytes, renderBreadcrumbs, renderEntries, renderMoveFolders } from "./render.js";
import { readState, writeState } from "./url-state.js";

const dialog = document.querySelector("#browser-dialog");
const openButton = document.querySelector("#open-browser");
const closeButton = document.querySelector("#close-browser");
const searchForm = document.querySelector("#search-form");
const searchInput = document.querySelector("#search-input");
const breadcrumbs = document.querySelector("#breadcrumbs");
const summary = document.querySelector("#summary");
const status = document.querySelector("#status");
const undoButton = document.querySelector("#undo-move");
const entries = document.querySelector("#entries");
const pageLabel = document.querySelector("#page-label");
const previousPage = document.querySelector("#previous-page");
const nextPage = document.querySelector("#next-page");
const uploadForm = document.querySelector("#upload-form");
const uploadInput = document.querySelector("#upload-file");
const apiForm = document.querySelector("#api-form");
const apiMode = document.querySelector("#api-mode");
const apiOriginInput = document.querySelector("#api-origin");
const customApiRow = document.querySelector("#custom-api-row");
const renderApiOrigin = "https://file-explorer-demo-api.onrender.com";
const moveDialog = document.querySelector("#move-dialog");
const moveFolders = document.querySelector("#move-folders");
const moveCrumbs = document.querySelector("#move-breadcrumbs");
const moveStatus = document.querySelector("#move-status");
let moveSource;
let moveDestination = "";
let moveVersion = 0;
let movePage = 1;
let draggedPath;
let undoMove;
if (window.location.hostname === "jcd1991.github.io") {
  apiMode.querySelector('option[value="same-origin"]').disabled = true;
}
let state = readState();
let activeRequest;
let requestVersion = 0;
let searchTimer;
let diagnosticLogging = false;
let snapshot;
let snapshotKey;
let mutationBusy = false;
let viewReady = false;
const initialBackendOrigin = getApiOrigin() || window.location.origin;
let demoWritesEnabled = initialBackendOrigin !== renderApiOrigin;

getConfig().then((config) => {
  if ((getApiOrigin() || window.location.origin) !== initialBackendOrigin) return;
  diagnosticLogging = config.diagnosticsEnabled === true;
  configureDemo(config, initialBackendOrigin);
}).catch(showError);

apiOriginInput.value = getApiOrigin();
apiMode.value = apiOriginInput.value ? "custom" : "same-origin";
syncApiMode();
apiMode.addEventListener("change", syncApiMode);
apiForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  if (apiMode.value === "custom" && !apiOriginInput.value.trim()) {
    showError(new Error("Enter the custom API server URL."));
    apiOriginInput.focus();
    return;
  }

  const previousOrigin = getApiOrigin();
  try {
    setApiOrigin(apiMode.value === "custom" ? apiOriginInput.value : "");
    const config = await getConfig();
    diagnosticLogging = config.diagnosticsEnabled === true;
    const origin = getApiOrigin();
    configureDemo(config, origin || window.location.origin);
    apiOriginInput.value = origin;
    apiMode.value = origin ? "custom" : "same-origin";
    syncApiMode();
    snapshot = undefined;
    setStatus(`Connected to ${origin || window.location.origin}.`);
    if (state.open) await loadEntries();
  } catch (error) {
    setApiOrigin(previousOrigin);
    showError(error);
  }
});

openButton.addEventListener("click", () => {
  state = { ...state, open: true };
  writeState(state);
  openDialog();
});
closeButton.addEventListener("click", () => dialog.close());
dialog.addEventListener("close", () => {
  activeRequest?.abort();
  requestVersion++;
  if (state.open) {
    state = { ...state, open: false };
    writeState(state);
  }
  openButton.focus();
});
window.addEventListener("popstate", () => {
  state = readState();
  syncFromState();
});

searchInput.addEventListener("input", () => {
  state = { ...state, query: searchInput.value, page: 1 };
  activeRequest?.abort();
  requestVersion++;
  writeState(state, true);
  clearTimeout(searchTimer);
  searchTimer = setTimeout(loadEntries, 250);
});
searchForm.addEventListener("submit", (event) => {
  event.preventDefault();
  clearTimeout(searchTimer);
  state = { ...state, query: searchInput.value, page: 1 };
  writeState(state, true);
  loadEntries();
});
breadcrumbs.addEventListener("click", (event) => {
  const button = event.target.closest("button[data-path]");
  if (!button) return;
  state = { ...state, path: button.dataset.path, page: 1 };
  writeState(state);
  loadEntries();
});
entries.addEventListener("click", (event) => {
  const action = event.target.closest("button[data-action]");
  if (mutationBusy || !viewReady) return;
  if (action && action.dataset.action !== "open-folder") { mutateEntry(action); return; }
  const button = action;
  if (!button) return;
  state = { ...state, path: button.closest("tr").dataset.path, page: 1 };
  writeState(state);
  loadEntries();
});
entries.addEventListener("dragstart", event => {
  const row = event.target.closest("tr[data-path]");
  if (!row || mutationBusy || !demoWritesEnabled) { event.preventDefault(); return; }
  draggedPath = row.dataset.path;
  event.dataTransfer.effectAllowed = "move";
  event.dataTransfer.setData("text/plain", draggedPath);
});
entries.addEventListener("dragend", clearDrag);
for (const zone of [entries, breadcrumbs]) {
  zone.addEventListener("dragover", event => {
    const selector = zone === entries ? 'tr[data-kind="folder"]' : "button[data-path]";
    const target = event.target.closest(selector);
    if (!draggedPath || !target || !validDrop(draggedPath, target.dataset.path)) return;
    event.preventDefault(); event.dataTransfer.dropEffect = "move"; target.classList.add("drop-target");
  });
  zone.addEventListener("dragleave", event => event.target.closest(".drop-target")?.classList.remove("drop-target"));
  zone.addEventListener("drop", event => {
    const selector = zone === entries ? 'tr[data-kind="folder"]' : "button[data-path]";
    const target = event.target.closest(selector);
    if (!draggedPath || !target || !validDrop(draggedPath, target.dataset.path)) return;
    event.preventDefault(); const source = draggedPath; clearDrag();
    executeMove(source, joinPath(target.dataset.path, source.split("/").pop()));
  });
}
document.querySelector("#move-close").addEventListener("click", closeMovePicker);
moveDialog.addEventListener("cancel", closeMovePicker);
moveDialog.addEventListener("close", () => { moveVersion++; moveSource = undefined; });
moveFolders.addEventListener("click", event => {
  const button = event.target.closest("button[data-path]");
  if (button) loadMoveFolders(button.dataset.path, 1);
});
moveCrumbs.addEventListener("click", event => {
  const button = event.target.closest("button[data-path]");
  if (button) loadMoveFolders(button.dataset.path, 1);
});
document.querySelector("#move-previous").addEventListener("click", () => loadMoveFolders(moveDestination, movePage - 1));
document.querySelector("#move-next").addEventListener("click", () => loadMoveFolders(moveDestination, movePage + 1));
document.querySelector("#move-submit").addEventListener("click", () => {
  const source = moveSource;
  closeMovePicker();
  executeMove(source, joinPath(moveDestination, source.split("/").pop()));
});
undoButton.addEventListener("click", () => {
  if (undoMove && !mutationBusy) executeMove(undoMove.destination, undoMove.source, true);
});
document.querySelector("thead").addEventListener("click", (event) => {
  const button = event.target.closest("button[data-sort]");
  if (!button) return;
  const sort = button.dataset.sort;
  state = { ...state, sort, direction: state.sort === sort && state.direction === "asc" ? "desc" : "asc", page: 1 };
  writeState(state);
  loadEntries();
});
previousPage.addEventListener("click", () => changePage(-1));
nextPage.addEventListener("click", () => changePage(1));
uploadForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  const file = uploadInput.files?.[0];
  if (!file || mutationBusy || !viewReady) return;
  mutationBusy = true;
  setReady(false);
  setStatus("Uploading…");
  const result = await measure("upload", () => uploadFile(state.path, file));
  mutationBusy = false;
  setReady(viewReady);
  if (!result) return;
  snapshot = undefined;
  setStatus(`Uploaded ${file.name}.`);
  uploadForm.reset();
  await loadEntries();
});

if (state.open) openDialog();
else syncFromState();

function openDialog() {
  if (!dialog.open) dialog.showModal();
  syncFromState();
  searchInput.focus();
}

function syncFromState() {
  searchInput.value = state.query;
  renderBreadcrumbs(breadcrumbs, state.path);
  if (state.open) openDialogIfNeeded();
  else if (dialog.open) dialog.close();
  else entries.replaceChildren();
}

function openDialogIfNeeded() {
  if (!dialog.open) dialog.showModal();
  loadEntries();
}

async function loadEntries() {
  if (!state.open) return;
  activeRequest?.abort();
  activeRequest = new AbortController();
  const currentVersion = ++requestVersion;
  const key = JSON.stringify([state.path, state.query, state.sort, state.direction]);
  if (key !== snapshotKey) snapshot = undefined;
  snapshotKey = key;
  viewReady = false;
  setReady(false);
  entries.replaceChildren();
  summary.textContent = "";
  pageLabel.textContent = "";
  renderBreadcrumbs(breadcrumbs, state.path);
  previousPage.disabled = nextPage.disabled = true;
  setStatus("Loading…");
  await measure(state.query.trim() ? "search" : "browse", async () => {
    let result;
    try {
      result = await fetchEntries(state, activeRequest.signal, snapshot);
    } catch (error) {
      if (currentVersion !== requestVersion) return;
      if (error.title !== "View expired") throw error;
      snapshot = undefined;
      result = await fetchEntries(state, activeRequest.signal);
    }
    if (currentVersion !== requestVersion) return;
    snapshot = result.snapshotId;
    viewReady = true;
    renderEntries(entries, result.entries);
    setReady(true);
    renderBreadcrumbs(breadcrumbs, state.path);
    const qualifier = result.partial ? "at least " : "";
    summary.textContent = `${qualifier}${result.summary.fileCount} files · ${qualifier}${result.summary.folderCount} folders · ${formatBytes(result.summary.totalFileBytes)} total file size`;
    if (result.warnings.length) summary.textContent += ` · ${result.warnings.map((warning) => `${warning.count} ${warning.code}`).join(", ")}`;
    pageLabel.textContent = `Page ${result.page} of ${result.totalPages}`;
    previousPage.disabled = result.page <= 1;
    nextPage.disabled = result.page >= result.totalPages;
    state = { ...state, page: result.page };
    if (result.page !== readState().page) writeState(state, true);
    setStatus(result.entries.length === 0 ? "No matching entries." : "");
  }, () => currentVersion === requestVersion);
}

function changePage(delta) {
  state = { ...state, page: Math.max(1, state.page + delta) };
  writeState(state);
  loadEntries();
}

async function measure(operation, action, isCurrent = () => true) {
  const started = diagnosticLogging ? performance.now() : 0;
  let outcome = "complete";
  try {
    return await action();
  } catch (error) {
    outcome = error.name === "AbortError" ? "cancelled" : "error";
    if (outcome === "error" && isCurrent()) showError(error);
  } finally {
    if (diagnosticLogging && started && outcome !== "cancelled") {
      console.debug("file-browser", { operation, outcome, durationMs: performance.now() - started });
    }
  }
}

function setStatus(message, error = false) {
  status.textContent = message;
  status.classList.toggle("error", error);
}

function showError(error) {
  setStatus(error.message || "The request failed.", true);
}

function configureDemo(config, backendOrigin) {
  const demo = config.demo;
  const notices = document.querySelectorAll(".free-demo-limits");
  const securityCheck = document.querySelector("#demo-security-check");
  securityCheck.hidden = true;
  securityCheck.querySelector("p").textContent = "A security check may appear before file changes.";
  demoWritesEnabled = backendOrigin !== renderApiOrigin;
  setMutationTokenProvider(undefined);
  if (!demo) {
    for (const notice of notices) {
      notice.hidden = backendOrigin !== renderApiOrigin;
      if (notice.hidden) notice.textContent = "";
    }
    setReady(viewReady);
    return;
  }

  for (const notice of notices) {
    renderDemoLimits(notice, demo);
  }

  if (!demo.turnstileReady || !demo.turnstileSiteKey) {
    demoWritesEnabled = false;
    securityCheck.hidden = false;
    securityCheck.querySelector("p").textContent = "File changes are temporarily disabled while the demo security check is configured.";
    setReady(viewReady);
    return;
  }

  demoWritesEnabled = true;
  securityCheck.hidden = false;
  setMutationTokenProvider(createTurnstileTokenProvider(demo.turnstileSiteKey));
  setReady(viewReady);
}

function renderDemoLimits(notice, demo) {
  const header = document.createElement("div");
  header.className = "free-demo-limits__header";

  const icon = document.createElement("span");
  icon.className = "free-demo-limits__icon";
  icon.setAttribute("aria-hidden", "true");
  icon.textContent = "✦";

  const titleGroup = document.createElement("div");
  const eyebrow = document.createElement("p");
  eyebrow.className = "free-demo-limits__eyebrow";
  eyebrow.textContent = "FREE PLAN FIELD GUIDE";
  const title = document.createElement("h3");
  title.textContent = "A few things to know";
  titleGroup.append(eyebrow, title);
  header.append(icon, titleGroup);

  const list = document.createElement("ul");
  list.className = "free-demo-limits__list";
  const uploadLimitBytes = demo.uploadLimitMiB * 1024 * 1024;
  appendLimitItem(list, `${demo.uploadLimitMiB} MiB per file (${uploadLimitBytes.toLocaleString("en-US")} bytes).`, "That’s the maximum upload size.");
  appendLimitItem(list, "Shared request pool.", `${demo.requestBurst} requests in a burst, then ${demo.requestRefillCount} every ${demo.requestRefillSeconds} seconds; up to ${demo.maxConcurrentRequests} at a time.`);
  appendLimitItem(list, "Shared file-change limits.", `${demo.creationsPerWindow} uploads or copies per ${demo.creationWindowMinutes} minutes, and ${demo.mutationsPerWindow} moves or deletes per minute.`);
  appendLimitItem(list, "The demo may nap.", `Render Free can sleep after ${demo.idleSleepMinutes} minutes idle; demo files reset after sleep, restart, or redeploy.`);

  notice.replaceChildren(header, list);
  notice.hidden = false;
}

function appendLimitItem(list, heading, detail) {
  const item = document.createElement("li");
  const label = document.createElement("strong");
  label.textContent = heading;
  item.append(label, document.createTextNode(` ${detail}`));
  list.append(item);
}

function createTurnstileTokenProvider(siteKey) {
  const container = document.querySelector("#turnstile-widget");
  let scriptPromise;
  let widgetPromise;
  let widgetId;
  let pending;

  function loadTurnstile() {
    if (window.turnstile) return Promise.resolve(window.turnstile);
    if (scriptPromise) return scriptPromise;

    scriptPromise = new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.src = "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit";
      script.async = true;
      script.defer = true;
      script.addEventListener("load", () => {
        if (window.turnstile) resolve(window.turnstile);
        else reject(new Error("The security check could not be loaded."));
      }, { once: true });
      script.addEventListener("error", () => reject(new Error("The security check could not be loaded. Check your connection and try again.")), { once: true });
      document.head.append(script);
    }).catch((error) => {
      scriptPromise = undefined;
      throw error;
    });
    return scriptPromise;
  }

  async function getWidget() {
    if (widgetId !== undefined) return { api: window.turnstile, id: widgetId };
    if (!widgetPromise) {
      widgetPromise = loadTurnstile().then((api) => {
        widgetId = api.render(container, {
          sitekey: siteKey,
          action: "file_mutation",
          execution: "execute",
          appearance: "interaction-only",
          size: "compact",
          callback: (token) => {
            setStatus("Security check passed. Sending the file change…");
            finish(token);
          },
          "error-callback": () => fail("The security check failed. Try the file change again."),
          "expired-callback": () => fail("The security check expired. Try the file change again."),
          "timeout-callback": () => fail("The security check timed out. Try the file change again."),
          "unsupported-callback": () => fail("This browser cannot run the security check.")
        });
        return { api, id: widgetId };
      }).catch((error) => {
        widgetPromise = undefined;
        throw error;
      });
    }
    return widgetPromise;
  }

  function finish(token) {
    if (!pending) return;
    const current = pending;
    pending = undefined;
    clearTimeout(current.timeout);
    current.resolve(token);
  }

  function fail(message) {
    if (!pending) return;
    const current = pending;
    pending = undefined;
    clearTimeout(current.timeout);
    current.reject(new Error(message));
  }

  return async () => {
    const widget = await getWidget();
    return new Promise((resolve, reject) => {
      if (pending) {
        reject(new Error("A security check is already in progress."));
        return;
      }
      const timeout = setTimeout(() => fail("The security check took too long. Try the file change again."), 120_000);
      pending = {
        resolve: (token) => resolve({ token, reset: () => widget.api.reset(widget.id) }),
        reject,
        timeout
      };
      setStatus("Completing the security check…");
      try { widget.api.execute(widget.id); }
      catch { fail("The security check could not start. Try the file change again."); }
    });
  };
}

function setReady(ready) {
  for (const control of uploadForm.querySelectorAll("input, button")) control.disabled = !ready || mutationBusy || !demoWritesEnabled;
  for (const control of entries.querySelectorAll("button")) control.disabled = !ready || mutationBusy || !demoWritesEnabled;
  for (const control of [searchInput, searchForm.querySelector("button[type=submit]"), closeButton, previousPage, nextPage,
    document.querySelector("#refresh"), apiMode, apiOriginInput, apiForm.querySelector("button[type=submit]"),
    ...document.querySelectorAll("thead button[data-sort]"), ...breadcrumbs.querySelectorAll("button[data-path]")]) control.disabled = mutationBusy;
  undoButton.disabled = mutationBusy || !demoWritesEnabled;
}

function syncApiMode() {
  const custom = apiMode.value === "custom";
  customApiRow.hidden = !custom;
  apiOriginInput.disabled = !custom;
  apiOriginInput.required = custom;
}

document.querySelector("#refresh").addEventListener("click", () => {
  snapshot = undefined;
  loadEntries();
});

async function mutateEntry(button) {
  const operation = button.dataset.action;
  const source = button.closest("tr").dataset.path;
  if (operation === "move") {
    moveSource = source; moveDestination = parentPath(source);
    moveDialog.showModal(); await loadMoveFolders(moveDestination, 1); return;
  }
  let destination = null;
  if (operation === "delete") {
    if (!window.confirm(`Permanently delete "${source}" and all its contents?`)) return;
  } else {
    destination = window.prompt(`${button.textContent} "${source}" to root-relative path (including name):`, source);
    if (destination === null) return;
  }
  mutationBusy = true;
  setReady(false);
  setStatus(`Working: ${operation}…`);
  await measure(operation, () => changeEntry(operation, source, destination));
  const error = status.classList.contains("error") ? status.textContent : null;
  mutationBusy = false;
  snapshot = undefined;
  await loadEntries();
  if (error) setStatus(error, true);
}

async function loadMoveFolders(path, page) {
  const version = ++moveVersion; moveDestination = path; movePage = Math.max(1, page);
  moveStatus.textContent = "Loading folders…"; renderBreadcrumbs(moveCrumbs, path); moveFolders.replaceChildren();
  document.querySelector("#move-submit").disabled = !validDrop(moveSource, path);
  try {
    const result = await fetchEntries({ path, query: "", sort: "name", direction: "asc", page: movePage }, undefined);
    if (version !== moveVersion || !moveDialog.open) return;
    movePage = result.page; renderMoveFolders(moveFolders, result.entries);
    document.querySelector("#move-page").textContent = `Page ${result.page} of ${result.totalPages}`;
    document.querySelector("#move-previous").disabled = result.page <= 1;
    document.querySelector("#move-next").disabled = result.page >= result.totalPages;
    moveStatus.textContent = result.entries.some(entry => entry.kind === "folder") ? "Choose a folder or move here." : "No folders on this page. Use the page controls to continue.";
  } catch (error) { if (version === moveVersion) moveStatus.textContent = error.message; }
}

function closeMovePicker() { if (moveDialog.open) moveDialog.close(); }
function parentPath(path) { const index = path.lastIndexOf("/"); return index < 0 ? "" : path.slice(0, index); }
function joinPath(folder, name) { return folder ? `${folder}/${name}` : name; }
function validDrop(source, folder) { return Boolean(source) && folder !== source && !folder.startsWith(`${source}/`) && joinPath(folder, source.split("/").pop()) !== source; }
function clearDrag() { draggedPath = undefined; entries.querySelectorAll(".drop-target").forEach(item => item.classList.remove("drop-target")); breadcrumbs.querySelectorAll(".drop-target").forEach(item => item.classList.remove("drop-target")); }

async function executeMove(source, destination, isUndo = false) {
  if (mutationBusy || !demoWritesEnabled || !validDrop(source, parentPath(destination))) return;
  mutationBusy = true; setReady(false); setStatus("Moving…");
  let succeeded = false;
  await measure("move", async () => { await changeEntry("move", source, destination); succeeded = true; });
  const failed = status.classList.contains("error"); mutationBusy = false; snapshot = undefined;
  if (succeeded && !failed) {
    if (isUndo) { undoMove = undefined; undoButton.hidden = true; }
    else { undoMove = { source, destination }; undoButton.hidden = false; }
    await loadEntries();
    setStatus(isUndo ? `Move undone. Returned ${source} to ${destination}.` : `Moved ${source} to ${destination}.`);
  } else if (!failed) { await loadEntries(); setStatus("Move outcome is uncertain. Check the listing before retrying.", true); }
  setReady(viewReady);
}
