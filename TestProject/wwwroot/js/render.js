import { apiUrl } from "./api.js";

export function renderBreadcrumbs(target, path) {
  target.replaceChildren();
  let current = "";
  addCrumb(target, "Root", "");
  for (const segment of path ? path.split("/") : []) {
    const separator = document.createElement("span");
    separator.textContent = "/";
    target.append(separator);
    current = current ? `${current}/${segment}` : segment;
    addCrumb(target, segment, current);
  }
}

export function renderEntries(target, entries) {
  const fragment = document.createDocumentFragment();
  for (const entry of entries) {
    const row = document.createElement("tr");
    row.dataset.path = entry.path;
    row.dataset.kind = entry.kind;
    const nameCell = document.createElement("td");
    if (entry.kind === "folder") {
      const open = document.createElement("button");
      open.type = "button";
      open.className = "entry-name";
      open.dataset.action = "open-folder";
      open.textContent = `📁 ${entry.name}`;
      nameCell.append(open);
    } else {
      const name = document.createElement("span");
      name.className = "file-name";
      name.textContent = `📄 ${entry.name}`;
      nameCell.append(name);
    }
    const size = document.createElement("td");
    if (entry.kind === "folder") {
      size.textContent = "—";
      size.setAttribute("aria-label", "Folder size not calculated");
      size.title = "Folder size is not calculated";
    } else {
      size.textContent = formatBytes(entry.sizeBytes ?? 0);
    }
    const modified = document.createElement("td");
    modified.textContent = new Date(entry.modifiedUtc).toLocaleString();
    const action = document.createElement("td");
    if (entry.kind === "file") {
      const download = document.createElement("a");
      download.textContent = "Download";
      download.href = `${apiUrl("files/download")}?${new URLSearchParams({ path: entry.path })}`;
      download.setAttribute("aria-label", `Download ${entry.name}`);
      action.append(download);
    }
    for (const operation of ["copy", "move", "delete"]) {
      const button = document.createElement("button");
      button.type = "button";
      button.dataset.action = operation;
      button.textContent = operation[0].toUpperCase() + operation.slice(1);
      button.setAttribute("aria-label", `${button.textContent} ${entry.name}`);
      action.append(button);
    }
    row.append(nameCell, size, modified, action);
    fragment.append(row);
  }
  target.replaceChildren(fragment);
}

function addCrumb(target, label, path) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "crumb";
  button.dataset.path = path;
  button.textContent = label;
  target.append(button);
}

export function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KiB", "MiB", "GiB", "TiB"];
  let value = bytes;
  let unit = -1;
  do { value /= 1024; unit++; } while (value >= 1024 && unit < units.length - 1);
  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[unit]}`;
}
