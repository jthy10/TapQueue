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
    if (!ctx.current) return;
    const online = printers.filter((p) => p.printer.online).length;
    const latestBuild = builds[0];
    const outdated = latestBuild ? clients.filter((c) => c.clientVersion !== latestBuild.version) : [];

    body.replaceChildren(
      h("div", { class: "grid stats" },
        stat("Held jobs", held.length, "jobs", waiting(held)),
        stat("Printers online", [online, h("small", null, ` / ${printers.length}`)], "printers",
          online === printers.length ? "All reachable" : `${printers.length - online} unreachable`),
        stat("Stations", stations.length, "stations", stations.length ? `Last tap-in ${lastSeen(stations)}` : "None set up"),
        stat("Workstations", clients.length, "workstations", "Signed in now"),
      ),
      h("div", { class: "grid two" },
        panel({ title: "Needs attention", flush: true, body: attention({ printers, stations, unknown, queues, outdated, latestBuild, server }) }),
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

function waiting(held) {
  const people = new Set(held.map((j) => j.owner ?? "?")).size;
  return people === 0 ? "Nothing waiting" : `${people === 1 ? "1 person" : `${people} people`} waiting`;
}

function stat(label, value, href, note) {
  return h("a", { class: "panel stat", href }, h("div", { class: "stat-label" }, label), h("div", { class: "stat-value" }, value), h("div", { class: "stat-note" }, note));
}

function lastSeen(stations) {
  const seen = stations.map((s) => s.lastSeenAt).filter(Boolean).sort().at(-1);
  return seen ? time(seen).textContent : "never";
}

function attention({ printers, stations, unknown, queues, outdated, latestBuild, server }) {
  const items = [];
  const add = (tone, title, text, href) => items.push(h("li", null,
    h("span", { class: `icon ${tone}` }, icon(tone === "ok" ? "check" : "alert")),
    h("div", null, href ? h("a", { href }, title) : h("b", null, title), text && h("p", null, text))));

  if (queues.length === 0) add("bad", "No queues", "Nobody can print until there's a queue to print to.", "queues");
  if (printers.length === 0) add("bad", "No printers", "Held jobs can't be released until a printer is added.", "printers");
  for (const p of printers) {
    if (!p.printer.online) add("bad", `${p.printer.name} is unreachable`, p.printer.stateMessage, `printers/${p.printer.id}`);
    else if (p.printer.stateMessage && p.printer.stateMessage !== "ready") add("warn", `${p.printer.name}: ${p.printer.stateMessage}`, null, `printers/${p.printer.id}`);
  }
  const printerIds = new Set(printers.map((p) => p.printer.id.toLowerCase()));
  for (const s of stations)
    if (!printerIds.has(s.printerId.toLowerCase())) add("bad", `Station ${s.id} has no printer`, `It's assigned to "${s.printerId}", which doesn't exist.`, `stations/${s.id}`);
  if (unknown.length) add("warn", `${plural(unknown.length, "unknown card")} tapped recently`, "Link them to people on the Cards page.", "cards");
  if (outdated.length) add("warn", `${plural(outdated.length, "workstation")} not on client ${latestBuild.version}`, "They update within a minute of their next heartbeat.", "workstations");
  if (server.authMode === "dev") add("warn", "Dev sign-in is on", "Clients can sign in as anyone without a token. Fine for testing only.", "server");
  if (items.length === 0) add("ok", "All clear", "Nothing needs you right now.");
  return h("ul", { class: "attention" }, items);
}
