// The page at /my: someone's own print jobs, balance and cards, from any browser.
// Plain modules, no build step. It talks to /api/v1/portal, which only ever answers about the person signed in.

const page = document.getElementById("page");
const signInBox = document.getElementById("sign-in");
const app = document.getElementById("app");

/** h("div", { class: "row", onclick: fn }, "text", child) */
function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs ?? {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === "class") el.className = value;
    else if (key.startsWith("on")) el.addEventListener(key.slice(2), value);
    else if (key in el && typeof value !== "string") el[key] = value;
    else el.setAttribute(key, value === true ? "" : value);
  }
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

async function api(method, path, body) {
  // The server refuses changes without this header, so another site can't make them with our cookie.
  const init = { method, headers: { "X-TapQueue-Portal": "1" } };
  if (body !== undefined) {
    init.body = JSON.stringify(body);
    init.headers["Content-Type"] = "application/json";
  }
  let response;
  try {
    response = await fetch(`/api/v1/portal/${path}`, init);
  } catch {
    throw new ApiError("Can't reach TapQueue. Check your connection.", 0);
  }
  if (response.status === 204) return null;
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* a proxy's error page */ }
  if (response.status === 401 && me) signedOut("Your sign-in has ended. Sign in again.");
  if (!response.ok) throw new ApiError(data?.error ?? `${response.status} ${response.statusText}`, response.status);
  return data;
}

function toast(message, bad) {
  const el = h("div", { class: `toast${bad ? " bad" : ""}`, role: "status" }, message);
  document.getElementById("toasts").append(el);
  setTimeout(() => el.remove(), bad ? 7000 : 3500);
}

/** Runs something a button asked for; a failure becomes a toast. */
async function attempt(action) {
  try {
    return await action();
  } catch (err) {
    if (err.status !== 401) toast(err.message, true);
    return undefined;
  }
}

const plural = (n, noun) => `${n} ${noun}${n === 1 ? "" : "s"}`;

function ago(value) {
  const seconds = Math.round((Date.now() - new Date(value)) / 1000);
  if (seconds < 45) return "just now";
  if (seconds < 3600) return `${Math.max(1, Math.round(seconds / 60))} min ago`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)} h ago`;
  return new Date(value).toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

/** Money is kept in thousandths of the currency's unit: 12500 is "$12.50", 35 is "€0.035". */
function money(mills, currency = "") {
  const text = (Math.abs(mills) / 1000).toFixed(Math.abs(mills) % 10 ? 3 : 2);
  const sign = mills < 0 ? "-" : "";
  if (!currency) return sign + text;
  return /^\p{L}+$/u.test(currency) ? `${sign}${text} ${currency}` : `${sign}${currency}${text}`;
}

// ---------- State ----------

let me = null;
let tab = "jobs";
let timer = null;
let printers = [];
/** The printer last released at, so the next visit starts there. */
const lastPrinter = {
  get: () => { try { return localStorage.getItem("tapqueue.printer"); } catch { return null; } },
  set: (id) => { try { localStorage.setItem("tapqueue.printer", id); } catch { /* private browsing */ } },
};

function signedOut(message) {
  me = null;
  clearInterval(timer);
  app.hidden = true;
  document.querySelector(".release")?.remove();
  showSignIn(message);
}

// ---------- Sign-in ----------

function showSignIn(message) {
  const error = h("div", { class: "error", role: "alert", hidden: !message }, message ?? "");
  const submit = h("button", { type: "submit", class: "btn primary" }, "Sign in");
  const username = h("input", { name: "username", type: "text", required: true, autocomplete: "username", autocapitalize: "none", spellcheck: false });
  const password = h("input", { name: "password", type: "password", required: true, autocomplete: "current-password" });
  const form = h("form", { onsubmit: async (e) => {
    e.preventDefault();
    error.hidden = true;
    submit.disabled = true;
    try {
      await api("POST", "sign-in", { username: username.value.trim(), password: password.value });
      await start();
    } catch (err) {
      error.textContent = err.message;
      error.hidden = false;
      password.value = "";
      password.focus();
    } finally {
      submit.disabled = false;
    }
  } },
  h("div", { class: "brand" }, h("img", { src: "/icons/128.png", alt: "", width: 44, height: 44 }),
    h("div", null, h("b", null, "TapQueue"), h("span", null, "Your print jobs"))),
  error,
  h("label", { class: "field" }, "Username", username),
  h("label", { class: "field" }, "Password", password, h("small", null, "The one you sign in to your computer with, or your TapQueue password.")),
  submit);
  signInBox.replaceChildren(h("div", { class: "card" }, form));
  signInBox.hidden = false;
  username.focus();
}

// ---------- Jobs ----------

const held = (j) => j.status === "held";
const pagesOf = (j) => j.pages == null ? "pages not counted" : plural(j.pages * j.copies, "page");

function describe(j) {
  return [pagesOf(j), j.copies > 1 && `${j.copies} copies`, j.color === true && "colour", j.duplex === true && "two-sided", ago(j.submittedAt)]
    .filter(Boolean).join(" · ");
}

function printedState(j) {
  if (j.status === "canceled") return h("span", { class: "pill" }, "Deleted");
  if (j.status === "expired") return h("span", { class: "pill" }, "Expired");
  if (j.status === "releasing") return h("span", { class: "pill ok" }, "Sending");
  if (j.printState === "printing") return h("span", { class: "pill ok" }, "Printing");
  if (j.printState === "stopped") return h("span", { class: "pill warn" }, "Stopped");
  return h("span", { class: "pill ok" }, "Printed");
}

async function showJobs() {
  const picked = new Set();
  let jobs = [], account = null, first = true;
  const waiting = h("div", { class: "list" });
  const recent = h("div", { class: "list" });
  const recentTitle = h("h2", null, "Lately");
  const select = h("select", { "aria-label": "Printer to release at" });
  const release = h("button", { type: "button", class: "btn primary", onclick: () => releaseJobs() }, "Release");
  const bar = h("div", { class: "release" }, select, release);
  page.replaceChildren(h("h2", null, "Waiting to print"), waiting, recentTitle, recent);
  document.querySelector(".release")?.remove();
  document.body.append(bar);

  function draw() {
    const waitingJobs = jobs.filter(held);
    // Anything that stopped waiting is no longer picked.
    for (const id of [...picked]) if (!waitingJobs.some((j) => j.id === id)) picked.delete(id);
    waiting.replaceChildren(...(waitingJobs.length ? waitingJobs.map((j) => h("label", { class: "row" },
      h("input", { type: "checkbox", checked: picked.has(j.id), "aria-label": `Release ${j.name}`, onchange: (e) => {
        e.target.checked ? picked.add(j.id) : picked.delete(j.id);
        label();
      } }),
      h("div", { class: "grow" }, h("div", { class: "title" }, j.name), h("span", { class: "sub" }, describe(j)),
        j.error && h("span", { class: "job-error" }, j.error)),
      h("button", { type: "button", class: "btn ghost small danger", "aria-label": `Delete ${j.name}`, onclick: (e) => { e.preventDefault(); remove(j); } }, "Delete")))
      : [h("div", { class: "empty" }, h("b", null, "Nothing waiting"), "Print to a TapQueue printer from your computer and it shows up here.")]));

    const done = jobs.filter((j) => !held(j) && j.status !== "receiving").slice(0, 15);
    recentTitle.hidden = recent.hidden = done.length === 0;
    recent.replaceChildren(...done.map((j) => h("div", { class: "row" },
      h("div", { class: "grow" }, h("div", { class: "title" }, j.name),
        h("span", { class: "sub" }, [pagesOf(j), j.releasedAt ? `released ${ago(j.releasedAt)}` : ago(j.submittedAt),
          j.cost > 0 && account && money(j.cost - j.refundedCost, account.currency)].filter(Boolean).join(" · "))),
      printedState(j),
      j.printState === "printing" && h("button", { type: "button", class: "btn ghost small danger", onclick: () => remove(j) }, "Stop"))));
    bar.hidden = waitingJobs.length === 0 || printers.length === 0;
    label();
  }

  function label() {
    const count = picked.size || jobs.filter(held).length;
    release.textContent = picked.size ? `Release ${picked.size}` : count > 1 ? `Release all ${count}` : "Release";
  }

  async function load() {
    const loaded = await attempt(() => Promise.all([api("GET", "me/jobs?all=true"), api("GET", "printers"), api("GET", "me/account")]));
    if (!loaded || tab !== "jobs") return;
    [jobs, printers, account] = loaded;
    const chosen = first ? lastPrinter.get() : select.value;
    select.replaceChildren(...printers.map((p) => h("option", { value: p.id, selected: p.id === chosen }, p.online ? p.name : `${p.name} (not answering)`)));
    first = false;
    draw();
  }

  async function releaseJobs() {
    if (!select.value) return;
    release.disabled = true;
    lastPrinter.set(select.value);
    const result = await attempt(() => api("POST", "me/release", { printerId: select.value, jobIds: picked.size ? [...picked] : null }));
    release.disabled = false;
    if (result) {
      const sent = result.results.filter((r) => r.success);
      const failed = result.results.filter((r) => !r.success);
      const printer = printers.find((p) => p.id === select.value)?.name ?? select.value;
      const cost = sent.reduce((sum, r) => sum + (r.cost ?? 0), 0);
      if (failed.length) toast(`${failed[0].jobName ? `${failed[0].jobName}: ` : ""}${failed[0].error}`, true);
      if (sent.length) toast(`${plural(sent.length, "job")} sent to ${printer}${cost > 0 && account ? `, for ${money(cost, account.currency)}` : ""}.`);
      for (const note of new Set(sent.map((r) => r.note).filter(Boolean))) toast(note);
      picked.clear();
    }
    await load();
  }

  async function remove(j) {
    const result = await attempt(() => api("DELETE", `me/jobs/${j.id}`));
    if (result !== undefined) toast(result?.message ?? `Deleted "${j.name}".`);
    await load();
  }

  await load();
  clearInterval(timer);
  timer = setInterval(() => { if (!document.hidden && tab === "jobs") load(); }, 8000);
}

// ---------- Account ----------

const per = { day: "a day", week: "a week", month: "a month" };
const current = { day: "today", week: "this week", month: "this month" };
const kinds = { charge: "Printed", refund: "Given back", adjust: "Adjusted" };

async function showAccount() {
  page.replaceChildren(h("div", { class: "empty" }, "Loading…"));
  const loaded = await attempt(() => Promise.all([api("GET", "me/account"), api("GET", "me/quota")]));
  if (!loaded || tab !== "account") return;
  const [account, quotas] = loaded;
  const m = (mills) => money(mills, account.currency);
  const parts = [];

  if (account.mode === "enforce") {
    parts.push(h("h2", null, "Balance"), h("div", { class: "stat" },
      h("div", { class: `big${account.balance < 0 ? " bad" : ""}` }, m(account.balance)),
      h("span", { class: "sub" }, account.unrestricted ? "You can keep printing when it runs out." : "Printing comes off this. Ask your IT team or front desk to top it up.")));
  }
  if (quotas.length) {
    parts.push(h("h2", null, "Page limit"), h("div", { class: "stack" }, quotas.map((q) => {
      const share = q.pages ? Math.min(1, q.used / q.pages) : 1;
      const bar = h("span");
      bar.style.width = `${Math.round(share * 100)}%`;
      return h("div", { class: "stat" },
        h("div", null, h("b", null, `${q.used} of ${plural(q.pages, "page")}`), ` used ${current[q.period] ?? ""}`),
        h("div", { class: `meter${q.remaining === 0 ? " bad" : share >= 0.8 ? " warn" : ""}` }, bar),
        h("span", { class: "sub" }, `${q.remaining} left. ${plural(q.pages, "page")} ${per[q.period] ?? q.period}; starts over ${new Date(q.resetsAt).toLocaleDateString(undefined, { weekday: "short", month: "short", day: "numeric" })}.`));
    })));
  }
  if (account.prices.length) {
    parts.push(h("h2", null, "What a page costs"), h("div", { class: "list" }, account.prices.map((p) => h("div", { class: "row" },
      h("div", { class: "grow" }, h("div", { class: "title" }, p.printerName)),
      h("div", { class: "amount" }, `${m(p.priceMono)} black and white`, h("span", { class: "sub" }, `${m(p.priceColor)} colour`))))),
    account.duplexDiscountPercent > 0 && h("p", { class: "sub" }, `Pages printed on both sides of the paper cost ${account.duplexDiscountPercent}% less.`));
  }
  if (account.transactions.length) {
    parts.push(h("h2", null, "What changed your balance"), h("div", { class: "list" }, account.transactions.map((t) => h("div", { class: "row" },
      h("div", { class: "grow" }, h("div", { class: "title" }, kinds[t.kind] ?? t.kind), h("span", { class: "sub" }, [t.note, ago(t.at)].filter(Boolean).join(" · "))),
      h("div", { class: "amount" }, `${t.amount > 0 ? "+" : ""}${m(t.amount)}`, h("span", { class: "sub" }, m(t.balanceAfter)))))));
  }
  if (!parts.length)
    parts.push(h("div", { class: "empty" }, h("b", null, "Nothing to keep track of"), "Printing isn't limited or charged for here."));
  if (me.canChangePassword) parts.push(h("h2", null, "Password"), passwordForm());
  page.replaceChildren(...parts);
}

function passwordForm() {
  const error = h("div", { class: "error", role: "alert", hidden: true });
  const now = h("input", { type: "password", required: true, autocomplete: "current-password" });
  const next = h("input", { type: "password", required: true, minLength: 10, autocomplete: "new-password" });
  const submit = h("button", { type: "submit", class: "btn" }, "Change password");
  const form = h("form", { class: "stat", onsubmit: async (e) => {
    e.preventDefault();
    error.hidden = true;
    submit.disabled = true;
    try {
      await api("POST", "password", { currentPassword: now.value, newPassword: next.value });
      toast("Password changed. Other browsers are signed out.");
      form.reset();
    } catch (err) {
      error.textContent = err.message;
      error.hidden = false;
    } finally {
      submit.disabled = false;
    }
  } },
  error,
  h("label", { class: "field" }, "Current password", now),
  h("label", { class: "field" }, "New password", next, h("small", null, "At least 10 characters.")),
  submit);
  return form;
}

// ---------- Badges ----------

async function showBadges() {
  page.replaceChildren(h("div", { class: "empty" }, "Loading…"));
  const loaded = await attempt(() => Promise.all([api("GET", "me/badges"), api("GET", "printers"), api("GET", "me/badge-enrollment")]));
  if (!loaded || tab !== "badges") return;
  const [badges, places, enrollment] = loaded;
  const linking = h("div");
  page.replaceChildren(
    h("h2", null, "Your badges"),
    h("div", { class: "list" }, badges.length ? badges.map((b) => h("div", { class: "row" },
      h("div", { class: "grow" }, h("div", { class: "title" }, `Card ${b.cardHint}`, b.label && ` (${b.label})`),
        h("span", { class: "sub" }, b.lastUsedAt ? `Last tapped ${ago(b.lastUsedAt)}` : "Not tapped yet")),
      b.canRemove && h("button", { type: "button", class: "btn ghost small danger", onclick: async () => {
        if (!window.confirm(`Remove card ${b.cardHint}? Tapping it won't print your jobs any more.`)) return;
        if (await attempt(() => api("DELETE", `me/badges/${b.id}`)) !== undefined) showBadges();
      } }, "Remove")))
      : h("div", { class: "empty" }, h("b", null, "No badge yet"), "Link one to print by tapping it at a printer.")),
    me.canLinkBadge && h("h2", null, "Link a badge"),
    me.canLinkBadge && linking);
  if (!me.canLinkBadge) return;

  function offer() {
    if (!places.length) return linking.replaceChildren(h("div", { class: "empty" }, "There's no printer you can release at."));
    const select = h("select", { "aria-label": "Printer you're standing at" }, places.map((p) => h("option", { value: p.id }, p.name)));
    linking.replaceChildren(h("div", { class: "stat stack" },
      h("p", null, "Stand at a printer with a badge reader, choose it here, then tap your badge on the reader."),
      select,
      h("button", { type: "button", class: "btn primary", onclick: async () => {
        const started = await attempt(() => api("POST", "me/badge-enrollment", { printerId: select.value }));
        if (started) wait(started);
      } }, "I'm here: link my badge")));
  }

  /** Counts down while the server waits for the tap, then says how it went. */
  function wait(status) {
    let left = status.secondsLeft;
    const count = h("div", { class: "countdown", "aria-live": "off" }, left);
    linking.replaceChildren(h("div", { class: "callout stack" }, h("b", null, status.message), count,
      h("button", { type: "button", class: "btn small", onclick: async () => {
        clearInterval(ticking);
        await attempt(() => api("DELETE", "me/badge-enrollment"));
        offer();
      } }, "Cancel")));
    const ticking = setInterval(async () => {
      if (tab !== "badges" || !count.isConnected) return clearInterval(ticking);
      count.textContent = Math.max(0, --left);
      if (left % 2 !== 0 && left > 0) return;
      const now = await api("GET", "me/badge-enrollment").catch(() => null);
      if (!now || now.state === "waiting") return;
      clearInterval(ticking);
      if (now.state === "linked") {
        toast(now.message);
        showBadges();
      } else {
        linking.replaceChildren(h("div", { class: "callout warn stack" }, h("p", null, now.message || "No card was linked."),
          h("button", { type: "button", class: "btn small", onclick: offer }, "Try again")));
      }
    }, 1000);
  }

  if (enrollment.state === "waiting") wait(enrollment);
  else offer();
}

// ---------- Shell ----------

const pages = { jobs: showJobs, account: showAccount, badges: showBadges };

function show(next) {
  tab = next;
  clearInterval(timer);
  if (next !== "jobs") document.querySelector(".release")?.remove();
  for (const button of document.querySelectorAll(".tabs button"))
    button.setAttribute("aria-selected", String(button.dataset.tab === next));
  pages[next]();
}

for (const button of document.querySelectorAll(".tabs button"))
  button.addEventListener("click", () => show(button.dataset.tab));

document.getElementById("sign-out").addEventListener("click", async () => {
  await api("POST", "sign-out", {}).catch(() => {});
  signedOut();
});

async function start() {
  try {
    me = await api("GET", "me");
  } catch (err) {
    me = null;
    app.hidden = true;
    // 404: an admin turned the page off. Anything but "not signed in" is worth showing.
    showSignIn(err.status === 401 ? null : err.message);
    return;
  }
  signInBox.hidden = true;
  app.hidden = false;
  document.getElementById("who-name").textContent = me.displayName || me.username;
  document.getElementById("who-username").textContent = me.username;
  show("jobs");
}

start();
