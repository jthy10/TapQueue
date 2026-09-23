// Small building blocks shared by every page. No framework: pages build elements with h() and
// re-render their own sections when data changes.

import { icon } from "./icons.js";

/** h("td", { class: "num", onclick: fn }, "text", child, [more]) */
export function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs ?? {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === "class") el.className = value;
    else if (key.startsWith("on")) el.addEventListener(key.slice(2), value);
    else if (key in el && typeof value !== "string") el[key] = value;
    else el.setAttribute(key, value === true ? "" : value);
  }
  append(el, children);
  return el;
}

function append(el, children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
}

export { icon };

// ---------- Formatting ----------

export function ago(value) {
  if (!value) return "never";
  const seconds = Math.round((Date.now() - new Date(value)) / 1000);
  if (seconds < 0) return in_(-seconds);
  if (seconds < 45) return "just now";
  if (seconds < 90) return "1 min ago";
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)} h ago`;
  if (seconds < 86400 * 30) return `${Math.round(seconds / 86400)} d ago`;
  return dateTime(value);
}

function in_(seconds) {
  if (seconds < 3600) return `in ${Math.max(1, Math.round(seconds / 60))} min`;
  if (seconds < 86400) return `in ${Math.round(seconds / 3600)} h`;
  return `in ${Math.round(seconds / 86400)} d`;
}

export const dateTime = (value) =>
  value ? new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" }) : "—";

/** A relative time with the exact time on hover. */
export const time = (value) => h("span", { title: value ? dateTime(value) : "" }, ago(value));

export function bytes(n) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

export const plural = (n, noun) => `${n} ${noun}${n === 1 ? "" : "s"}`;

export function duration(from) {
  const seconds = Math.max(0, Math.round((Date.now() - new Date(from)) / 1000));
  const d = Math.floor(seconds / 86400), hr = Math.floor((seconds % 86400) / 3600), m = Math.floor((seconds % 3600) / 60);
  return d ? `${d}d ${hr}h` : hr ? `${hr}h ${m}m` : `${m}m`;
}

// ---------- Layout ----------

export function pageHead(title, description, ...actions) {
  return h("div", { class: "page-head" },
    h("div", null, h("h1", null, title), description && h("p", null, description)),
    h("div", { class: "actions" }, actions));
}

export function panel({ title, description, actions, body, flush }) {
  return h("section", { class: "panel" },
    title && h("div", { class: "panel-head" },
      h("div", null, h("h2", null, title), description && h("p", null, description)),
      actions && h("div", { class: "actions" }, actions)),
    flush ? body : h("div", { class: "panel-body" }, body));
}

export function button(label, { onclick, kind, small, iconName, title, type = "button", disabled } = {}) {
  return h("button", {
    type, title, disabled, onclick,
    class: ["btn", kind, small && "small", !label && "icon-only"].filter(Boolean).join(" "),
  }, iconName && icon(iconName), label);
}

export function pill(text, tone) {
  return h("span", { class: `pill ${tone ?? ""}` }, text);
}

export function empty(title, text, action) {
  return h("div", { class: "empty" }, h("b", null, title), text, action && h("div", null, action));
}

export function props(entries) {
  return h("dl", { class: "props" },
    entries.filter(Boolean).map(([term, value]) => [h("dt", null, term), h("dd", null, value ?? "—")]));
}

export const sectionTitle = (text) => h("div", { class: "section-title" }, text);

export const loading = () => h("div", { class: "loading" }, "Loading…");

// ---------- Tables ----------

/**
 * columns: [{ label, value: row => node, class }]
 * Pass `search: row => "text to match"` to get a filter box, `onRowClick` to make rows open something.
 */
export function table({ columns, rows, onRowClick, empty: emptyNode, search, toolbar, selected }) {
  const tbody = h("tbody");
  const filter = search && h("input", { class: "search", type: "search", placeholder: "Filter…", oninput: () => fill() });

  function fill() {
    const q = filter?.value.trim().toLowerCase() ?? "";
    const shown = q ? rows.filter((r) => search(r).toLowerCase().includes(q)) : rows;
    tbody.replaceChildren(...shown.map((row) =>
      h("tr", {
        class: [onRowClick && "clickable", selected?.(row) && "selected"].filter(Boolean).join(" "),
        onclick: onRowClick && (() => onRowClick(row)),
      }, columns.map((c) => h("td", { class: c.class }, c.value(row))))));
    body.hidden = shown.length === 0;
    none.hidden = shown.length > 0;
  }

  const body = h("div", { class: "table-wrap" },
    h("table", null, h("thead", null, h("tr", null, columns.map((c) => h("th", { class: c.class }, c.label)))), tbody));
  const none = h("div", null, rows.length === 0 ? emptyNode ?? empty("Nothing here yet") : empty("No matches", "Try a different filter."));
  const bar = (filter || toolbar) && h("div", { class: "toolbar" }, filter, h("div", { class: "grow" }), toolbar);
  fill();
  return h("div", null, bar, body, none);
}

// ---------- Drawer ----------

let openDrawer = null;

/** A side panel for one item's details. Only one is open at a time. */
export function drawer({ title, subtitle, body, footer, onClose }) {
  closeDrawer();
  const scrim = h("div", { class: "scrim", onclick: () => close() });
  const el = h("aside", { class: "drawer", role: "dialog", "aria-label": title },
    h("div", { class: "drawer-head" },
      h("div", null, h("h2", null, title), subtitle && h("p", null, subtitle)),
      button(null, { kind: "ghost", iconName: "x", title: "Close", onclick: () => close() })),
    h("div", { class: "drawer-body" }, body),
    footer && h("div", { class: "drawer-foot" }, footer));
  const onKey = (e) => { if (e.key === "Escape" && !document.querySelector("dialog[open]")) close(); };

  function close(silent) {
    if (openDrawer !== handle) return;
    scrim.remove();
    el.remove();
    document.removeEventListener("keydown", onKey);
    openDrawer = null;
    if (!silent) onClose?.();
  }

  const handle = { close, el };
  document.body.append(scrim, el);
  document.addEventListener("keydown", onKey);
  openDrawer = handle;
  return handle;
}

/** Closes whatever drawer is open without running its onClose (used when navigating away). */
export function closeDrawer() {
  openDrawer?.close(true);
}

// ---------- Dialogs and forms ----------

export function field(label, control, hint) {
  return h("label", { class: "field" }, h("span", null, label), control, hint && h("small", null, hint));
}

export function checkField(label, control, hint) {
  return h("label", { class: "field check" }, control, h("div", null, h("span", null, label), hint && h("small", null, hint)));
}

export const input = (name, attrs = {}) => h("input", { name, autocomplete: "off", ...attrs });

export function select(name, options, value) {
  return h("select", { name },
    options.map(([v, label]) => h("option", { value: v, selected: v === value }, label)));
}

export const checkbox = (name, checked) => h("input", { type: "checkbox", name, checked });

/** Every named control's value: checkboxes as booleans, the rest as trimmed strings. */
export function readForm(form) {
  const data = {};
  for (const el of form.elements) {
    if (!el.name) continue;
    data[el.name] = el.type === "checkbox" ? el.checked : el.type === "file" ? el.files[0] ?? null : el.value.trim();
  }
  return data;
}

/**
 * A modal form. onSubmit(values) runs the change; throw to show the error in the form. If it returns
 * an element, the dialog shows it with a Done button (for things like a token that's only shown once).
 */
export function formDialog({ title, description, body, submitLabel = "Save", danger, onSubmit }) {
  const error = h("div", { class: "form-error", hidden: true });
  const submit = button(submitLabel, { type: "submit", kind: danger ? "danger solid" : "primary" });
  const cancel = button("Cancel", { onclick: () => dialog.close() });
  const content = h("div", { class: "dialog-body" }, error, body);
  const foot = h("div", { class: "dialog-foot" }, cancel, submit);
  const form = h("form", { method: "dialog", onsubmit: async (e) => {
    e.preventDefault();
    error.hidden = true;
    submit.disabled = true;
    try {
      const result = await onSubmit(readForm(form));
      if (result instanceof Node) {
        content.replaceChildren(result);
        foot.replaceChildren(button("Done", { kind: "primary", onclick: () => dialog.close() }));
      } else {
        dialog.close();
      }
    } catch (err) {
      error.textContent = err.message;
      error.hidden = false;
    } finally {
      submit.disabled = false;
    }
  } },
    h("div", { class: "dialog-head" }, h("h2", null, title), description && h("p", null, description)),
    content, foot);
  const dialog = h("dialog", { onclose: () => dialog.remove() }, form);
  document.body.append(dialog);
  dialog.showModal();
  form.querySelector("input:not([type=hidden]), select, textarea")?.focus();
  return dialog;
}

/** Resolves true if the admin confirms. */
export function confirm({ title, message, confirmLabel = "Confirm", danger = true }) {
  return new Promise((resolve) => {
    let confirmed = false;
    const dialog = formDialog({
      title, submitLabel: confirmLabel, danger,
      body: h("p", { style: "margin:0" }, message),
      onSubmit: () => { confirmed = true; },
    });
    dialog.addEventListener("close", () => resolve(confirmed));
  });
}

/** A value (like a token) shown once, with a copy button. */
export function secret(value, note) {
  const copy = button("Copy", { small: true, iconName: "copy", onclick: async () => {
    await navigator.clipboard?.writeText(value);
    copy.lastChild.textContent = "Copied";
  } });
  return h("div", null,
    note && h("div", { class: "callout" }, note),
    h("div", { class: "secret" }, h("code", null, value), copy));
}

// ---------- Toasts ----------

export function toast(message, tone) {
  const el = h("div", { class: `toast ${tone ?? ""}`, role: "status" }, message);
  document.getElementById("toasts").append(el);
  setTimeout(() => el.remove(), tone === "bad" ? 7000 : 3500);
}

/** Runs an action from a button and reports failure as a toast, so handlers don't each need try/catch. */
export async function attempt(action, success) {
  try {
    const result = await action();
    if (success) toast(success);
    return result;
  } catch (err) {
    toast(err.message, "bad");
    return undefined;
  }
}
