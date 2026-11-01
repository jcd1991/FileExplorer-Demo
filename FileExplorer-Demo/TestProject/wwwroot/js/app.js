import { changeEntry, fetchEntries, getApiOrigin, getConfig, setApiOrigin, uploadFile } from "./api.js";
import { formatBytes, renderBreadcrumbs, renderEntries } from "./render.js?v=menu-5";
import { readState, writeState } from "./url-state.js";

const dialog = document.querySelector("#browser-dialog");
const openButton = document.querySelector("#open-browser");
const closeButton = document.querySelector("#close-browser");
const searchForm = document.querySelector("#search-form");
const searchInput = document.querySelector("#search-input");
const breadcrumbs = document.querySelector("#breadcrumbs");
const summary = document.querySelector("#summary");
const status = document.querySelector("#status");
const statusRow = status.closest(".status-row");
const undoButton = document.querySelector("#undo-move");
const entries = document.querySelector("#entries");
const pageLabel = document.querySelector("#page-label");
const previousPage = document.querySelector("#previous-page");
const nextPage = document.querySelector("#next-page");
const sortSelect = document.querySelector("#sort-select");
const uploadForm = document.querySelector("#upload-form");
const uploadInput = document.querySelector("#upload-file");
const apiForm = document.querySelector("#api-form");
const connectionSettings = document.querySelector(".connection-settings");
const apiMode = document.querySelector("#api-mode");
const apiOriginInput = document.querySelector("#api-origin");
const customApiRow = document.querySelector("#custom-api-row");
const sortHeaders = [...document.querySelectorAll("th[data-sort-column]")];
let state = readState();
let activeRequest;
let requestVersion = 0;
let searchTimer;
let diagnosticLogging = false;
let snapshot;
let snapshotKey;
let mutationBusy = false;
let viewReady = false;
let draggedPath;
let undoMove;

if (["localhost", "127.0.0.1", "::1"].includes(window.location.hostname) && !new URLSearchParams(window.location.search).has("showConnection")) {
  connectionSettings.hidden = true;
}

try { diagnosticLogging = (await getConfig()).diagnosticsEnabled === true; }
catch (error) { showError(error); }

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
    const origin = setApiOrigin(apiMode.value === "custom" ? apiOriginInput.value : "");
    const config = await getConfig();
    diagnosticLogging = config.diagnosticsEnabled === true;
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
function closeActionMenus(except) {
  entries.querySelectorAll("details.action-menu[open]").forEach(menu => {
    if (menu === except) return;
    menu.open = false;
    const list = menu.querySelector(".action-list");
    if (list) {
      list.style.left = "";
      list.style.top = "";
    }
  });
}
document.addEventListener("toggle", event => {
  const menu = event.target;
  if (menu instanceof HTMLDetailsElement && menu.matches(".action-menu") && menu.open) closeActionMenus(menu);
}, true);
document.addEventListener("scroll", closeActionMenus, true);
entries.addEventListener("dragstart", event => {
  const row = event.target.closest("tr[data-path]");
  if (!row || mutationBusy) { event.preventDefault(); return; }
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
    event.preventDefault();
    event.dataTransfer.dropEffect = "move";
    target.classList.add("drop-target");
  });
  zone.addEventListener("dragleave", event => event.target.closest(".drop-target")?.classList.remove("drop-target"));
  zone.addEventListener("drop", event => {
    const selector = zone === entries ? 'tr[data-kind="folder"]' : "button[data-path]";
    const target = event.target.closest(selector);
    if (!draggedPath || !target || !validDrop(draggedPath, target.dataset.path)) return;
    event.preventDefault();
    const source = draggedPath;
    clearDrag();
    executeMove(source, joinPath(target.dataset.path, source.split("/").pop()));
  });
}
document.querySelector("thead").addEventListener("click", (event) => {
  const button = event.target.closest("button[data-sort]");
  if (!button) return;
  const sort = button.dataset.sort;
  state = { ...state, sort, direction: state.sort === sort && state.direction === "asc" ? "desc" : "asc", page: 1 };
  writeState(state);
  loadEntries();
});
sortSelect.addEventListener("change", () => {
  state = { ...state, sort: sortSelect.value, direction: "asc", page: 1 };
  writeState(state);
  loadEntries();
});
undoButton.addEventListener("click", () => {
  if (undoMove && !mutationBusy) executeMove(undoMove.destination, undoMove.source, true);
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
  sortSelect.value = state.sort;
  syncSortAccessibility();
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
    syncSortAccessibility();
    if (result.page !== readState().page) writeState(state, true);
    setStatus(result.entries.length === 0 ? "No matching entries." : "");
  }, () => currentVersion === requestVersion);
}

function syncSortAccessibility() {
  for (const header of sortHeaders) header.setAttribute("aria-sort", header.dataset.sortColumn === state.sort ? state.direction : "none");
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
  statusRow.hidden = !message && !undoMove;
}

function showError(error) {
  setStatus(error.message || "The request failed.", true);
}

function setReady(ready) {
  for (const control of uploadForm.querySelectorAll("input, button")) control.disabled = !ready || mutationBusy;
  for (const control of entries.querySelectorAll("button")) control.disabled = !ready || mutationBusy;
  undoButton.disabled = mutationBusy || !undoMove;
}

function syncApiMode() {
  const custom = apiMode.value === "custom";
  customApiRow.hidden = !custom;
  apiOriginInput.disabled = !custom;
  apiOriginInput.required = custom;
}

async function executeMove(source, destination, isUndo = false) {
  if (mutationBusy || !validDrop(source, parentPath(destination))) return;
  mutationBusy = true;
  setReady(false);
  setStatus("Moving…");
  await measure("move", () => changeEntry("move", source, destination));
  const error = status.classList.contains("error") ? status.textContent : null;
  mutationBusy = false;
  snapshot = undefined;
  await loadEntries();
  if (!error) {
    if (isUndo) { undoMove = undefined; undoButton.hidden = true; setStatus(`Move undone. Returned ${source} to ${destination}.`); }
    else { undoMove = { source, destination }; undoButton.hidden = false; setStatus(`Moved ${source} to ${destination}.`); }
    setReady(viewReady);
  } else setStatus(error, true);
}

function parentPath(path) { const index = path.lastIndexOf("/"); return index < 0 ? "" : path.slice(0, index); }
function joinPath(folder, name) { return folder ? `${folder}/${name}` : name; }
function validDrop(source, folder) { return Boolean(source) && folder !== source && !folder.startsWith(`${source}/`) && joinPath(folder, source.split("/").pop()) !== source; }
function clearDrag() {
  draggedPath = undefined;
  entries.querySelectorAll(".drop-target").forEach(item => item.classList.remove("drop-target"));
  breadcrumbs.querySelectorAll(".drop-target").forEach(item => item.classList.remove("drop-target"));
}

document.querySelector("#refresh").addEventListener("click", () => {
  snapshot = undefined;
  loadEntries();
});

async function mutateEntry(button) {
  const operation = button.dataset.action;
  const source = button.closest("tr").dataset.path;
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
