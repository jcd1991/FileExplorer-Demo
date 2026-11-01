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
    nameCell.dataset.label = "Name";
    if (entry.kind === "folder") {
      const open = document.createElement("button");
      open.type = "button";
      open.className = "entry-name";
      open.dataset.action = "open-folder";
      open.setAttribute("aria-label", `📁 ${entry.name}`);
      open.draggable = true;
      open.append(icon("folder"), document.createTextNode(entry.name));
      nameCell.append(open);
    } else {
      const name = document.createElement("span");
      name.className = "file-name";
      name.append(icon("file"), document.createTextNode(entry.name));
      name.draggable = true;
      nameCell.append(name);
    }
    const size = document.createElement("td");
    size.dataset.label = "Size";
    if (entry.kind === "folder") {
      size.textContent = "—";
      size.setAttribute("aria-label", "Folder size not calculated");
      size.title = "Folder size is not calculated";
    } else {
      size.textContent = formatBytes(entry.sizeBytes ?? 0);
    }
    const modified = document.createElement("td");
    modified.dataset.label = "Modified";
    modified.textContent = new Date(entry.modifiedUtc).toLocaleString();
    const action = document.createElement("td");
    action.className = "entry-actions";
    action.dataset.label = "Actions";
    if (entry.kind === "file") {
      const download = document.createElement("a");
      download.href = `${apiUrl("files/download")}?${new URLSearchParams({ path: entry.path })}`;
      download.setAttribute("aria-label", `Download ${entry.name}`);
      download.title = `Download ${entry.name}`;
      download.className = "download-link";
      download.append(icon("download"));
      action.append(download);
    }
    const actionMenu = document.createElement("details");
    actionMenu.className = "action-menu";
    const actionSummary = document.createElement("summary");
    actionSummary.textContent = "More actions";
    actionSummary.addEventListener("click", () => {
      requestAnimationFrame(() => {
        if (!actionMenu.open) return;
        const summaryRect = actionSummary.getBoundingClientRect();
        const menuRect = actionList.getBoundingClientRect();
        const gap = 4;
        const above = summaryRect.bottom + menuRect.height > window.innerHeight - 8;
        const left = Math.max(8, Math.min(summaryRect.right - menuRect.width, window.innerWidth - menuRect.width - 8));
        const top = above ? summaryRect.top - menuRect.height - gap : summaryRect.bottom + gap;
        actionList.style.left = `${left}px`;
        actionList.style.top = `${Math.max(8, top)}px`;
        actionMenu.classList.toggle("action-menu--above", above);
      });
    });
    actionMenu.append(actionSummary);
    const actionList = document.createElement("span");
    actionList.className = "action-list";
    for (const operation of ["copy", "move", "delete"]) {
      const button = document.createElement("button");
      button.type = "button";
      button.dataset.action = operation;
      button.textContent = operation[0].toUpperCase() + operation.slice(1);
      button.setAttribute("aria-label", `${button.textContent} ${entry.name}`);
      actionList.append(button);
    }
    actionMenu.append(actionList);
    action.append(actionMenu);
    row.append(nameCell, size, modified, action);
    fragment.append(row);
  }
  target.replaceChildren(fragment);
}

function icon(kind) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("aria-hidden", "true"); svg.setAttribute("viewBox", "0 0 24 24"); svg.classList.add("entry-icon");
  const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
  path.setAttribute("d", kind === "folder" ? "M3 6.5A1.5 1.5 0 0 1 4.5 5H10l2 2h7.5A1.5 1.5 0 0 1 21 8.5v9A1.5 1.5 0 0 1 19.5 19h-15A1.5 1.5 0 0 1 3 17.5v-11Z" : kind === "download" ? "M12 3v11m0 0 4-4m-4 4-4-4M5 19h14" : "M7 3h7l4 4v14H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Zm7 1v4h4");
  path.setAttribute("fill", "none"); path.setAttribute("stroke", "currentColor"); path.setAttribute("stroke-width", "1.7"); path.setAttribute("stroke-linejoin", "round");
  svg.append(path); return svg;
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
