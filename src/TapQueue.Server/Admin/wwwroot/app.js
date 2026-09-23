// The shell: sidebar, and a router that maps /admin/<page>/<id> to pages/<page>.js.
// A page module exports render(root, ctx); see ctx below for what it gets.

import { api } from "./api.js";
import { h, icon, closeDrawer, empty } from "./ui.js";

const nav = [
  { group: "Operate", items: [
    { id: "overview", label: "Overview", icon: "overview" },
    { id: "jobs", label: "Jobs", icon: "jobs" },
    { id: "activity", label: "Activity", icon: "activity", soon: true },
  ] },
  { group: "People", items: [
    { id: "users", label: "Users", icon: "users" },
    { id: "groups", label: "Groups", icon: "groups", soon: true },
    { id: "cards", label: "Cards", icon: "cards" },
  ] },
  { group: "Fleet", items: [
    { id: "printers", label: "Printers", icon: "printers" },
    { id: "queues", label: "Queues", icon: "queues" },
    { id: "stations", label: "Stations", icon: "stations" },
    { id: "workstations", label: "Workstations", icon: "workstations" },
  ] },
  { group: "System", items: [
    { id: "updates", label: "Updates", icon: "updates" },
    { id: "server", label: "Server", icon: "server" },
    { id: "admins", label: "Admins", icon: "admins", soon: true },
  ] },
];
const pages = new Set(nav.flatMap((g) => g.items.filter((i) => !i.soon).map((i) => i.id)));

const root = document.getElementById("page");
let timers = [];
let renderCount = 0;

function route() {
  const [page = "overview", id] = location.pathname.replace(/^\/admin\/?/, "").split("/").filter(Boolean).map(decodeURIComponent);
  return { page, id };
}

/** Goes to a console path like "users/jthy1" (relative to /admin/). */
function navigate(path, { replace } = {}) {
  history[replace ? "replaceState" : "pushState"](null, "", `/admin/${path}`);
  render();
}

async function render() {
  const { page, id } = route();
  const thisRender = ++renderCount;
  timers.forEach(clearInterval);
  timers = [];
  closeDrawer();
  drawNav(page);

  const item = nav.flatMap((g) => g.items).find((i) => i.id === page);
  document.title = `${item?.label ?? "Not found"} · TapQueue Admin`;
  if (!pages.has(page)) {
    root.replaceChildren(empty("Page not found", `There's no "${page}" page.`, h("a", { class: "btn", href: "overview" }, "Go to Overview")));
    return;
  }

  const ctx = {
    id,
    /** Records which item's drawer is open (or none), without re-rendering the page. */
    setId(next) {
      const path = next ? `${page}/${encodeURIComponent(next)}` : page;
      if (location.pathname !== `/admin/${path}`) history.pushState(null, "", `/admin/${path}`);
    },
    /** Runs fn every ms while this page is showing and the tab is visible. */
    every(ms, fn) {
      timers.push(setInterval(() => { if (!document.hidden) fn(); }, ms));
    },
    /** False once the admin has moved to another page, so late responses don't draw over it. */
    get current() { return thisRender === renderCount; },
    navigate,
  };

  try {
    const module = await import(`./pages/${page}.js`);
    if (!ctx.current) return;
    root.replaceChildren();
    await module.render(root, ctx);
  } catch (err) {
    if (!ctx.current) return;
    root.replaceChildren(empty("Couldn't load this page", err.message, h("button", { class: "btn", onclick: render }, "Try again")));
  }
}

function drawNav(active) {
  document.getElementById("nav").replaceChildren(...nav.map((g) =>
    h("div", { class: "nav-group" },
      h("div", { class: "nav-group-title" }, g.group),
      g.items.map((i) => h("a", {
        href: i.id,
        class: ["nav-link", i.id === active && "active", i.soon && "disabled"].filter(Boolean).join(" "),
        "aria-current": i.id === active ? "page" : null,
        "aria-disabled": i.soon ? "true" : null,
      }, icon(i.icon), i.label, i.soon && h("span", { class: "soon" }, "Soon"))))));
}

// Links inside the console navigate without reloading.
document.addEventListener("click", (e) => {
  const a = e.target.closest("a[href]");
  if (!a || e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || a.target) return;
  const url = new URL(a.href, location.href);
  if (url.origin !== location.origin || !url.pathname.startsWith("/admin/")) return;
  e.preventDefault();
  navigate(url.pathname.slice("/admin/".length));
});

window.addEventListener("popstate", render);

api.get("server").then((s) => {
  document.getElementById("server-version").textContent = `Server ${s.version}`;
}).catch(() => {});

render();
