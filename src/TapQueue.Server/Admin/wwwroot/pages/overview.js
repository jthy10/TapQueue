import { api } from "../api.js";
import { h, icon, pageHead, panel, button, pill, time, plural, duration } from "../ui.js";
import { activityList } from "./activity.js";

export async function render(root, ctx) {
  const body = h("div", { class: "stack" });
  root.append(pageHead("Overview", "How printing is going right now.",
    button("Refresh", { iconName: "refresh", onclick: () => load(true) })), body);

  async function load(refresh) {
    const [server, printers, stations, clients, held, unknown, queues, builds, events] = await Promise.all([
      api.get("server"), api.get(`printers${refresh ? "?refresh=true" : ""}`), api.get("stations"), api.get("clients"),
      api.get("jobs?status=held"), api.get("badges/unknown"), api.get("queues"), api.get("client-builds"), api.get("events?limit=10"),
    ]);
    const crashes = await api.get("crashes?limit=200");
    if (!ctx.current) return;
    const online = printers.filter((p) => p.printer.online).length;
    const ready = printers.filter((p) => p.health?.canPrint ?? p.printer.online).length;
    const latestBuild = builds[0];
    const published = new Set(builds.filter((b) => b === builds.find((x) => x.platform === b.platform)).map((b) => b.version));
    const outdated = latestBuild ? clients.filter((c) => !published.has(c.clientVersion)) : [];

    body.replaceChildren(
      h("div", { class: "grid stats" },
        stat("Held jobs", held.length, "jobs", waiting(held)),
        stat("Printers ready", [ready, h("small", null, ` / ${printers.length}`)], "printers", printerNote(printers, online, ready)),
        stat("Stations online", [stations.filter((s) => s.online).length, h("small", null, ` / ${stations.length}`)], "stations",
          stations.length === 0 ? "None set up" : stations.every((s) => s.online) ? "All reporting in" : `${stations.filter((s) => !s.online).length} offline`),
        stat("Workstations", clients.length, "workstations", "Signed in now"),
      ),
      h("div", { class: "grid two" },
        panel({ title: "Needs attention", flush: true, body: attention({ printers, stations, unknown, queues, outdated, latestBuild, server, crashes }) }),
        panel({ title: "Server", flush: true, body: h("div", { class: "panel-body" }, h("dl", { class: "props" },
          h("dt", null, "Version"), h("dd", null, server.version),
          h("dt", null, "Up for"), h("dd", null, duration(server.startedAt)),
          h("dt", null, "Sign-in"), h("dd", null, server.authMode === "dev" ? pill("Dev: no tokens", "bad") : pill("Tokens", "ok")),
          h("dt", null, "Jobs held for"), h("dd", null, plural(server.holdHours, "hour")),
        )) }),
      ),
      panel({ title: "Recent activity", actions: h("a", { class: "btn small", href: "activity" }, "All activity"),
        body: activityList(events, { emptyText: "Nothing yet. Prints, taps and changes show up here." }) }),
    );
  }

  body.append(h("div", { class: "loading" }, "Loading…"));
  await load(false);
  ctx.every(15000, () => load(false));
}

function printerNote(printers, online, ready) {
  if (ready === printers.length) return "All ready to print";
  const stopped = online - ready;
  return [printers.length - online && `${printers.length - online} unreachable`, stopped && `${stopped} stopped`].filter(Boolean).join(", ");
}

function waiting(held) {
  const people = new Set(held.map((j) => j.owner ?? "?")).size;
  return people === 0 ? "Nothing waiting" : `${people === 1 ? "1 person" : `${people} people`} waiting`;
}

function stat(label, value, href, note) {
  return h("a", { class: "panel stat", href }, h("div", { class: "stat-label" }, label), h("div", { class: "stat-value" }, value), h("div", { class: "stat-note" }, note));
}

function attention({ printers, stations, unknown, queues, outdated, latestBuild, server, crashes }) {
  const items = [];
  const add = (tone, title, text, href) => items.push(h("li", null,
    h("span", { class: `icon ${tone}` }, icon(tone === "ok" ? "check" : "alert")),
    h("div", null, href ? h("a", { href }, title) : h("b", null, title), text && h("p", null, text))));

  if (queues.length === 0) add("bad", "No queues", "Nobody can print until there's a queue to print to.", "queues");
  if (printers.length === 0) add("bad", "No printers", "Held jobs can't be released until a printer is added.", "printers");
  for (const p of printers) {
    const health = p.health;
    const since = health?.since ? ` For ${duration(health.since)}.` : "";
    if (!p.printer.online) add("bad", `${p.printer.name} is unreachable`, `${p.printer.stateMessage.replace(/\.$/, "")}.${since}`, `printers/${p.printer.id}`);
    else if (health?.level === "error") add("bad", `${p.printer.name}: ${health.problems.join(", ")}`,
      (health.canPrint ? "Still printing." : "Jobs stay held until it's fixed.") + since, `printers/${p.printer.id}`);
    else if (health?.level === "warning") add("warn", `${p.printer.name}: ${health.problems.join(", ")}`, null, `printers/${p.printer.id}`);
  }
  const printerIds = new Set(printers.map((p) => p.printer.id.toLowerCase()));
  for (const s of stations) {
    const name = s.name || s.id;
    if (!printerIds.has(s.printerId.toLowerCase())) add("bad", `Station ${name} has no printer`, `It's assigned to "${s.printerId}", which doesn't exist.`, `stations/${s.id}`);
    else if (!s.online) add("bad", `Station ${name} is offline`, s.lastHeartbeatAt ? `Last heard from ${time(s.lastHeartbeatAt).textContent}.` : "It has never reported in. Check its token and server_url.", `stations/${s.id}`);
    else if (s.readerStatus && s.readerStatus !== "ok") add("warn", `Station ${name}: ${s.readerStatus}`, "Taps there won't be read until it's fixed.", `stations/${s.id}`);
    else if (!s.settings.enabled) add("warn", `Station ${name} is out of service`, s.settings.maintenanceMessage || null, `stations/${s.id}`);
  }
  if (unknown.length) add("warn", `${plural(unknown.length, "unknown card")} tapped recently`, "Link them to people on the Cards page.", "cards");
  const crashedPcs = [...new Set(crashes.filter((c) => Date.now() - new Date(c.occurredAt) < 24 * 60 * 60 * 1000).map((c) => c.computer))];
  if (crashedPcs.length) add("bad", `TapQueue crashed on ${crashedPcs.length === 1 ? crashedPcs[0] : plural(crashedPcs.length, "workstation")} in the last day`,
    "Open the workstation to read the crash report.", crashedPcs.length === 1 ? `workstations/${encodeURIComponent(crashedPcs[0])}` : "workstations");
  if (outdated.length) add("warn", `${plural(outdated.length, "workstation")} not on the published client`, "They update within a minute of their next heartbeat.", "workstations");
  if (server.authMode === "dev") add("warn", "Dev sign-in is on", "Clients can sign in as anyone without a token. Fine for testing only.", "server");
  if (items.length === 0) add("ok", "All clear", "Nothing needs you right now.");
  return h("ul", { class: "attention" }, items);
}
