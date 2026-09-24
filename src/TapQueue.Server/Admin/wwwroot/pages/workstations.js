import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, time, dateTime, table, empty, drawer, props, sectionTitle, confirm, attempt, loading } from "../ui.js";

/**
 * PCs come from two places: the TapQueue service, which checks in once a minute whether or not
 * anyone is signed in, and signed-in tray apps. A PC with only the latter runs an older client or
 * one installed without the setup program, so it can't be updated from here.
 */
function merge(workstations, clients) {
  const byName = new Map(workstations.map((w) => [w.hostname.toLowerCase(), { ...w, service: true }]));
  for (const c of clients) {
    const key = (c.hostname ?? c.remoteIp).toLowerCase();
    if (byName.has(key)) continue;
    const pc = { hostname: c.hostname ?? c.remoteIp, lastIp: c.remoteIp, version: c.clientVersion, sessions: [], online: true, service: false };
    byName.set(key, pc);
  }
  for (const c of clients) {
    const pc = byName.get((c.hostname ?? c.remoteIp).toLowerCase());
    if (!pc.service) {
      pc.sessions.push(c);
      pc.lastSeenAt = pc.lastSeenAt && pc.lastSeenAt > c.lastSeenAt ? pc.lastSeenAt : c.lastSeenAt;
    }
  }
  return [...byName.values()].sort((a, b) => a.hostname.localeCompare(b.hostname));
}

const DAY = 24 * 60 * 60 * 1000;
const recentCrashes = (pc) => (pc.crashes ?? []).filter((c) => Date.now() - new Date(c.occurredAt) < DAY);

function status(pc) {
  if (recentCrashes(pc).length) return pill(pc.online ? "Crashed" : "Crashed, offline", "bad");
  if (!pc.online) return pill("Offline", "bad");
  if (pc.updateError) return pill("Update failed", "warn");
  return pill("Online", "ok");
}

const platformName = (p) => ({ "win-x64": "Windows", "linux-x64": "Linux" })[p] ?? p;

function clientVersion(pc, latestByPlatform) {
  if (!pc.version) return h("span", { class: "muted" }, "—");
  const latest = latestByPlatform[pc.platform ?? "win-x64"];
  const tag = !latest ? null
    : pc.service ? (pc.upToDate ? pill("Current", "ok") : pill(pc.updateError ? "Behind" : "Updating", "warn"))
    : pc.version === latest ? pill("Current", "ok") : pill("Behind", "warn");
  return [h("span", { class: "mono" }, pc.version), " ", tag];
}

export async function render(root, ctx) {
  let pcs = [], latest = {}, timeout = 10;
  const list = h("div", null, loading());
  root.append(pageHead("Workstations", "PCs with the TapQueue client. Jobs printed from one are matched to whoever is signed in there.",
    button("Refresh", { iconName: "refresh", onclick: () => load() })),
  panel({ flush: true, body: list }));

  async function load() {
    const [workstations, clients, builds, server, crashes] = await Promise.all([
      api.get("workstations"), api.get("clients"), api.get("client-builds"), api.get("server"), api.get("crashes?limit=200")]);
    if (!ctx.current) return;
    latest = {};
    for (const b of builds) latest[b.platform] ??= b.version; // newest first
    timeout = server.sessionTimeoutMinutes;
    pcs = merge(workstations, clients);
    for (const pc of pcs) pc.crashes = crashes.filter((c) => c.computer.toLowerCase() === pc.hostname.toLowerCase());
    list.replaceChildren(table({
      rows: pcs,
      search: (pc) => `${pc.hostname} ${pc.lastIp} ${pc.version ?? ""} ${pc.sessions.map((s) => `${s.username} ${s.windowsUser ?? ""}`).join(" ")}`,
      onRowClick: (pc) => open(pc.hostname),
      empty: empty("No workstations yet", "PCs show up here once the TapQueue client is installed and running on them."),
      columns: [
        { label: "PC", value: (pc) => [h("span", { class: "cell-strong" }, pc.hostname),
          h("span", { class: "sub" }, pc.platform ? `${platformName(pc.platform)} · ${pc.lastIp}` : pc.lastIp)] },
        { label: "Status", value: status },
        { label: "Signed in", value: (pc) => pc.sessions.length === 0 ? h("span", { class: "muted" }, "Nobody")
          : pc.sessions.map((s, i) => [i > 0 && ", ", h("a", { href: `users/${enc(s.username)}`, onclick: (e) => e.stopPropagation() }, s.username)]) },
        { label: "Client", value: (pc) => clientVersion(pc, latest) },
        { label: "Last seen", value: (pc) => time(pc.lastSeenAt) },
      ],
    }));
  }

  function open(hostname) {
    ctx.setId(hostname);
    const pc = pcs.find((x) => x.hostname.toLowerCase() === String(hostname).toLowerCase());
    if (!pc) return ctx.setId(null);
    drawer({
      title: pc.hostname,
      subtitle: "Workstation",
      onClose: () => ctx.setId(null),
      body: [
        !pc.service && h("div", { class: "callout" },
          "This PC's TapQueue service isn't checking in (an older client, or one not installed with the setup program), so it can't be updated from here. Install the client from the latest release on it."),
        pc.updateError && h("div", { class: "callout" }, `Its last update failed: ${pc.updateError}`),
        props([
          ["Status", status(pc)],
          pc.platform && ["System", platformName(pc.platform)],
          ["Client", clientVersion(pc, latest)],
          ["Address", h("code", null, pc.lastIp)],
          ["Last seen", pc.lastSeenAt ? [time(pc.lastSeenAt), h("span", { class: "sub" }, dateTime(pc.lastSeenAt))] : "—"],
          pc.firstSeenAt && ["First seen", dateTime(pc.firstSeenAt)],
          pc.pendingCommand && ["Waiting to", `${pc.pendingCommand} (when it next checks in, within a minute)`],
        ]),
        pc.crashes.length > 0 && sectionTitle("Crash reports"),
        pc.crashes.length > 0 && h("div", null,
          pc.crashes.slice(0, 10).map((c) => h("details", { class: "crash" },
            h("summary", null, h("b", null, c.program === "service" ? "TapQueue service" : "Tray app"), ` ${c.version ?? ""} · `,
              time(c.occurredAt), h("div", { class: "sub" }, c.message)),
            h("pre", null, c.details ?? c.message))),
          button("Clear crash reports", { small: true, kind: "ghost", onclick: () => clearCrashes(pc) })),
        sectionTitle("Signed in"),
        pc.sessions.length === 0
          ? h("p", { class: "muted", style: "margin:0" }, `Nobody. Jobs printed from here don't belong to anyone until someone signs in.`)
          : h("div", { class: "row-list" }, pc.sessions.map((s) => h("div", { class: "row-item" },
            h("div", null,
              h("a", { class: "cell-strong", href: `users/${enc(s.username)}` }, s.username),
              h("span", { class: "sub" }, `${pc.platform === "linux-x64" ? "Linux" : "Windows"} user ${s.windowsUser ?? "?"} · client ${s.clientVersion ?? "unknown"} · seen `, time(s.lastSeenAt))),
            button("Sign out", { small: true, onclick: () => signOut(pc, s) })))),
        h("p", { class: "muted", style: "margin:12px 0 0; font-size:12.5px" },
          `A client that stops checking in is signed out after ${timeout} minutes.`),
      ],
      footer: [
        pc.service && h("div", { class: "left" }, button("Forget", { kind: "ghost danger", onclick: () => forget(pc) })),
        pc.service && button("Update now", { iconName: "updates", onclick: () => updateNow(pc), disabled: !latest[pc.platform] }),
      ],
    });
  }

  async function clearCrashes(pc) {
    if (await attempt(() => api.del(`crashes?computer=${enc(pc.hostname)}`), `Cleared crash reports from ${pc.hostname}`) === undefined) return;
    await load();
    open(pc.hostname);
  }

  async function signOut(pc, s) {
    if (!await confirm({
      title: `Sign ${s.username} out on ${pc.hostname}?`,
      message: "Jobs printed from this PC stop going to them, and their TapQueue app says an admin signed them out. They can sign in again from its menu.",
      confirmLabel: "Sign out",
    })) return;
    if (await attempt(() => api.del(`clients/${s.id}`), `Signed ${s.username} out`) === undefined) return;
    await load();
    open(pc.hostname);
  }

  async function updateNow(pc) {
    const message = pc.online ? `${pc.hostname} will install client ${latest[pc.platform]} within a minute` : `${pc.hostname} is offline; it'll update when it's back`;
    if (await attempt(() => api.post(`workstations/${enc(pc.hostname)}/update`), message) === undefined) return;
    await load();
    open(pc.hostname);
  }

  async function forget(pc) {
    if (!await confirm({ title: `Forget ${pc.hostname}?`, message: "For PCs that are gone. If its TapQueue service checks in again, it comes back.", confirmLabel: "Forget" })) return;
    if (await attempt(() => api.del(`workstations/${enc(pc.hostname)}`), `Forgot ${pc.hostname}`) !== undefined) {
      ctx.setId(null);
      await load();
    }
  }

  await load();
  if (ctx.id) open(ctx.id);
  ctx.every(15000, () => { if (!document.querySelector(".drawer, dialog[open]")) load(); });
}
