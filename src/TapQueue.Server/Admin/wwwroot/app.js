// The shell: sign-in, sidebar, and a router that maps /admin/<page>/<id> to pages/<page>.js.
// A page module exports render(root, ctx); see ctx below for what it gets.
//
// Each page belongs to an admin area (docs/admin-roles.md); the sidebar only shows pages the admin
// has a role in. The server checks every request anyway, so this is about not showing dead ends.

import { api, auth } from "./api.js";
import { h, icon, closeDrawer, empty, button, field, input, formDialog, toast } from "./ui.js";

const nav = [
  { group: "Operate", items: [
    { id: "overview", label: "Overview", icon: "overview" },
    { id: "jobs", label: "Jobs", icon: "jobs", area: "jobs" },
    { id: "activity", label: "Activity", icon: "activity" },
  ] },
  { group: "People", items: [
    { id: "users", label: "Users", icon: "users", area: "people" },
    { id: "groups", label: "Groups", icon: "groups", area: "people" },
    { id: "cards", label: "Cards", icon: "cards", area: "people" },
    { id: "directory", label: "Active Directory", icon: "directory", area: "directory" },
  ] },
  { group: "Fleet", items: [
    { id: "printers", label: "Printers", icon: "printers", area: "fleet" },
    { id: "queues", label: "Queues", icon: "queues", area: "fleet" },
    { id: "stations", label: "Stations", icon: "stations", area: "fleet" },
    { id: "workstations", label: "Workstations", icon: "workstations", area: "fleet" },
  ] },
  { group: "System", items: [
    { id: "updates", label: "Updates", icon: "updates", area: "updates" },
    { id: "server", label: "Server", icon: "server", area: "server" },
    { id: "admins", label: "Admins", icon: "admins", area: "full" },
  ] },
];
const pages = new Set(nav.flatMap((g) => g.items.map((i) => i.id)));
const ranks = { viewer: 1, operator: 2, admin: 3 };

/** Who is signed in (GET admin-auth/me): { authMode, username, displayName, roles: { area: role }, fullAdmin, hasPassword }. */
let session = null;

/** Does the admin have at least `role` in `area`? No area: any admin. "full": a full admin. */
function can(area, role = "viewer") {
  if (!session) return false;
  if (!area) return true;
  if (area === "full") return session.fullAdmin;
  return (ranks[session.roles[area]] ?? 0) >= ranks[role];
}

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
  if (!can(item.area)) {
    root.replaceChildren(empty("Not for your role", item.area === "full"
      ? "Only full admins (admin in every area) can open this page."
      : "Your admin role doesn't include this part of the console. A full admin can change that.",
    h("a", { class: "btn", href: "overview" }, "Go to Overview")));
    return;
  }

  const ctx = {
    id,
    /** Records which item's drawer is open (or none), without re-rendering the page. */
    setId(next) {
      const path = next ? `${page}/${encodeURIComponent(next)}` : page;
      if (location.pathname !== `/admin/${path}`) history.pushState(null, "", `/admin/${path}`);
    },
    /** Runs fn every ms while this page is showing and the tab is visible. A failed refresh just waits for the next one. */
    every(ms, fn) {
      timers.push(setInterval(() => { if (!document.hidden) Promise.resolve().then(fn).catch(() => {}); }, ms));
    },
    /** False once the admin has moved to another page, so late responses don't draw over it. */
    get current() { return thisRender === renderCount; },
    navigate,
    /** can(area, role): whether the admin may see (or, with a role, do) something. See can() above. */
    can,
    session,
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
  document.getElementById("nav").replaceChildren(...nav
    .map((g) => ({ ...g, items: g.items.filter((i) => can(i.area)) }))
    .filter((g) => g.items.length)
    .map((g) =>
      h("div", { class: "nav-group" },
        h("div", { class: "nav-group-title" }, g.group),
        g.items.map((i) => h("a", {
          href: i.id,
          class: ["nav-link", i.id === active && "active"].filter(Boolean).join(" "),
          "aria-current": i.id === active ? "page" : null,
        }, icon(i.icon), i.label)))));
}

function drawAccount() {
  const account = document.getElementById("account");
  if (!session.username) {
    account.replaceChildren(h("div", { class: "account-name" }, session.authMode === "dev" ? "Dev mode: not signed in" : "Admin token"),
      session.authMode === "dev" && h("div", { class: "account-actions" }, button("Sign in", { small: true, kind: "ghost", onclick: () => showSignIn() })));
    return;
  }
  account.replaceChildren(
    h("div", { class: "account-name", title: session.username }, session.displayName || session.username),
    h("div", { class: "account-role" }, roleSummary()),
    h("div", { class: "account-actions" },
      session.hasPassword && button("Password", { small: true, kind: "ghost", onclick: changePassword }),
      button("Sign out", { small: true, kind: "ghost", onclick: signOut })));
}

function roleSummary() {
  if (session.fullAdmin) return "Full admin";
  const roles = Object.values(session.roles);
  const top = ["admin", "operator", "viewer"].find((r) => roles.includes(r));
  return top ? `${top[0].toUpperCase()}${top.slice(1)} (some areas)` : "No role";
}

function changePassword() {
  formDialog({
    title: "Change your password",
    description: "For the admin console. You stay signed in here; other browsers are signed out.",
    submitLabel: "Change password",
    body: [
      field("Current password", input("current", { type: "password", required: true, autocomplete: "current-password" })),
      field("New password", input("next", { type: "password", required: true, minlength: 10, autocomplete: "new-password" }), "At least 10 characters."),
    ],
    onSubmit: async ({ current, next }) => {
      await auth.changePassword(current, next);
      toast("Password changed");
    },
  });
}

async function signOut() {
  await auth.signOut().catch(() => {});
  session = null;
  await start();
}

/** The sign-in screen, instead of the console. In dev mode it can be closed to go back to the open console. */
function showSignIn(message) {
  const error = h("div", { class: "form-error", hidden: !message }, message ?? "");
  const submit = button("Sign in", { type: "submit", kind: "primary" });
  const form = h("form", { onsubmit: async (e) => {
    e.preventDefault();
    error.hidden = true;
    submit.disabled = true;
    try {
      session = await auth.signIn(form.elements.username.value.trim(), form.elements.password.value);
      showConsole();
    } catch (err) {
      error.textContent = err.message;
      error.hidden = false;
      form.elements.password.value = "";
      form.elements.password.focus();
    } finally {
      submit.disabled = false;
    }
  } },
  h("div", { class: "sign-in-brand" }, h("img", { src: "/icons/128.png", alt: "" }), h("div", null, h("b", null, "TapQueue"), h("span", null, "Admin console"))),
  error,
  field("Username", input("username", { required: true, autocomplete: "username", autocapitalize: "none" }), "Your TapQueue username. Domain accounts can also use DOMAIN\\name."),
  field("Password", input("password", { type: "password", required: true, autocomplete: "current-password" }), "Domain accounts use their domain password."),
  submit,
  session?.authMode === "dev" && button("Back to the open console", { kind: "ghost", onclick: () => showConsole() }));

  document.getElementById("shell").hidden = true;
  const box = document.getElementById("sign-in");
  box.replaceChildren(h("div", { class: "sign-in-card" }, form));
  box.hidden = false;
  document.title = "Sign in · TapQueue Admin";
  form.elements.username.focus();
}

function showConsole() {
  document.getElementById("sign-in").hidden = true;
  document.getElementById("shell").hidden = false;
  document.getElementById("dev-banner").hidden = session.authMode !== "dev";
  drawAccount();
  render();
}

/** Finds out who is signed in (if anyone) and shows the console or the sign-in screen. */
async function start() {
  try {
    session = await auth.me();
  } catch (err) {
    session = null;
    showSignIn(err.status === 401 ? null : err.message);
    return;
  }
  showConsole();
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

window.addEventListener("popstate", () => { if (session) render(); });

// A request came back 401: the session ended (timed out, signed out elsewhere, or disabled).
window.addEventListener("tapqueue:signed-out", () => {
  if (!session) return;
  session = null;
  closeDrawer();
  showSignIn("Your sign-in has ended. Sign in again.");
});

fetch("/healthz").then((r) => r.json()).then((s) => {
  document.getElementById("server-version").textContent = `Server ${s.version}`;
}).catch(() => {});

start();
