const defaults = { open: false, path: "", query: "", sort: "name", direction: "asc", page: 1 };

export function readState(url = new URL(window.location.href)) {
  const pageValue = Number.parseInt(url.searchParams.get("page") ?? "1", 10);
  const sortValue = url.searchParams.get("sort") ?? defaults.sort;
  const directionValue = url.searchParams.get("direction") ?? defaults.direction;
  return {
    open: url.searchParams.get("open") === "1",
    path: url.searchParams.get("path") ?? "",
    query: url.searchParams.get("q") ?? "",
    sort: ["name", "size", "modified"].includes(sortValue) ? sortValue : defaults.sort,
    direction: directionValue === "desc" ? "desc" : "asc",
    page: Number.isSafeInteger(pageValue) && pageValue > 0 ? pageValue : 1,
  };
}

export function writeState(state, replace = false) {
  const url = new URL(window.location.href);
  const values = { open: state.open ? "1" : null, path: state.path, q: state.query, sort: state.sort, direction: state.direction, page: String(state.page) };
  for (const [key, value] of Object.entries(values)) {
    if (value === null || value === "" || (key === "page" && value === "1")) url.searchParams.delete(key);
    else url.searchParams.set(key, value);
  }
  window.history[replace ? "replaceState" : "pushState"]({}, "", url);
}
