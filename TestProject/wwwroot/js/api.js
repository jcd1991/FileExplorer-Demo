const apiOriginKey = "fileBrowser.apiOrigin";

export function getApiOrigin() {
  let value;
  try {
    value = localStorage.getItem(apiOriginKey);
  } catch {
    return "";
  }
  try { return normalizeApiOrigin(value ?? ""); }
  catch { return ""; }
}

export function setApiOrigin(value) {
  const normalized = normalizeApiOrigin(value);
  try {
    if (normalized) localStorage.setItem(apiOriginKey, normalized);
    else localStorage.removeItem(apiOriginKey);
  } catch {
    throw new Error("This browser could not save the backend choice. Allow local storage for this site and try again.");
  }
  return normalized;
}

export function apiUrl(path) {
  const origin = getApiOrigin() || window.location.origin;
  return new URL(`/api/${path.replace(/^\/+/, "")}`, `${origin}/`).toString();
}

export async function getConfig() {
  const response = await fetch(apiUrl("config"));
  if (!response.ok) throw new Error("Could not load browser configuration.");
  return response.json();
}

export async function fetchEntries(state, signal, snapshot) {
  const searching = state.query.trim().length > 0;
  const params = new URLSearchParams({ path: state.path, sort: state.sort, direction: state.direction, page: String(state.page), pageSize: "100" });
  if (snapshot) params.set("snapshot", snapshot);
  if (searching) params.set("q", state.query.trim());
  return request(`${apiUrl(`files/${searching ? "search" : "browse"}`)}?${params}`, { signal });
}

export async function uploadFile(path, file, signal) {
  const params = new URLSearchParams({ path });
  const body = new FormData();
  body.append("file", file, file.name);
  return request(`${apiUrl("files/upload")}?${params}`, { method: "POST", body, signal });
}

export async function changeEntry(operation, source, destination) {
  return request(apiUrl(`files/${operation}`), { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ source, destination }) });
}

function normalizeApiOrigin(value) {
  const candidate = value.trim();
  if (!candidate) return "";

  let url;
  try {
    url = new URL(candidate);
  } catch {
    throw new Error("Enter a complete API URL, such as https://files.example.com.");
  }

  if (!["http:", "https:"].includes(url.protocol) || url.username || url.password || url.search || url.hash || !["", "/", "/api", "/api/"].includes(url.pathname)) {
    throw new Error("Use an HTTP or HTTPS server URL without credentials, query, or extra path.");
  }

  return url.origin;
}

async function request(url, options = {}) {
  const response = await fetch(url, options);
  const contentType = response.headers.get("content-type") ?? "";
  const payload = contentType.includes("json") ? await response.json() : null;
  if (!response.ok) {
    const error = new Error(payload?.detail || `Request failed (${response.status}).`);
    error.title = payload?.title;
    throw error;
  }
  return payload;
}
