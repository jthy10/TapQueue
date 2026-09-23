import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, time, table, empty, loading } from "../ui.js";

export async function render(root, ctx) {
  const list = h("div", null, loading());
  root.append(pageHead("Workstations", "PCs with the TapQueue client signed in. Jobs printed from one are matched to whoever is signed in there.",
    button("Refresh", { iconName: "refresh", onclick: () => load() })),
  panel({ flush: true, body: list }));

  async function load() {
    const [clients, builds, server] = await Promise.all([api.get("clients"), api.get("client-builds"), api.get("server")]);
    if (!ctx.current) return;
    const latest = builds[0]?.version;
    list.replaceChildren(table({
      rows: clients,
      search: (c) => `${c.hostname ?? ""} ${c.username} ${c.windowsUser ?? ""} ${c.remoteIp} ${c.clientVersion ?? ""}`,
      empty: empty("No workstations signed in", `Clients that haven't checked in for ${server.sessionTimeoutMinutes} minutes drop off this list.`),
      columns: [
        { label: "PC", value: (c) => [h("span", { class: "cell-strong" }, c.hostname ?? "Unknown PC"), h("span", { class: "sub" }, c.remoteIp)] },
        { label: "Person", value: (c) => [h("a", { href: `users/${enc(c.username)}` }, c.username), h("span", { class: "sub" }, `Windows: ${c.windowsUser ?? "?"}`)] },
        { label: "Client", value: (c) => [h("span", { class: "mono" }, c.clientVersion ?? "unknown"), " ",
          latest && (c.clientVersion === latest ? pill("Current", "ok") : pill("Updating", "warn"))] },
        { label: "Last seen", value: (c) => time(c.lastSeenAt) },
      ],
    }));
  }

  await load();
  ctx.every(15000, load);
}
