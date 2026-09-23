import { api, enc } from "../api.js";
import { activityList } from "./activity.js";
import { h, pageHead, panel, button, time, dateTime, table, empty, drawer, props, sectionTitle, field, input, select,
  formDialog, confirm, attempt, loading, secret } from "../ui.js";

export async function render(root, ctx) {
  let stations = [], printers = [];
  const list = h("div", null, loading());
  root.append(pageHead("Stations", "Badge readers next to printers. Tapping a card at one releases its owner's jobs to that station's printer.",
    button("Add station", { kind: "primary", iconName: "plus", onclick: add })),
  h("div", { class: "callout info" }, "Stations report in when they start and whenever a card is tapped, so \"last seen\" is the last of those. Live status and remote control come with station heartbeats."),
  panel({ flush: true, body: list }));

  const printerName = (id) => printers.find((p) => p.printer.id.toLowerCase() === id.toLowerCase())?.printer.name;
  const printerOptions = () => printers.map((p) => [p.printer.id, `${p.printer.name} (${p.printer.id})`]);

  async function load() {
    [stations, printers] = await Promise.all([api.get("stations"), api.get("printers")]);
    if (!ctx.current) return;
    list.replaceChildren(table({
      rows: stations,
      search: (s) => `${s.id} ${s.printerId} ${s.lastIp ?? ""}`,
      onRowClick: (s) => open(s.id),
      empty: empty("No stations yet", "Add one, then put its token in /etc/tapqueue/station.toml on the reader box.",
        button("Add station", { kind: "primary", onclick: add })),
      columns: [
        { label: "Station", value: (s) => h("span", { class: "cell-strong" }, s.id) },
        { label: "Releases to", value: (s) => printerName(s.printerId) ?? h("span", { style: "color:var(--bad)" }, `${s.printerId} (missing)`) },
        { label: "Address", value: (s) => s.lastIp ? h("code", null, s.lastIp) : h("span", { class: "muted" }, "—") },
        { label: "Last seen", value: (s) => time(s.lastSeenAt) },
      ],
    }));
  }

  async function open(id) {
    ctx.setId(id);
    const s = stations.find((x) => x.id.toLowerCase() === String(id).toLowerCase());
    if (!s) return ctx.setId(null);
    const events = await api.get(`events?subject=${enc(`station:${s.id}`)}&limit=10`);
    drawer({
      title: s.id,
      subtitle: "Release station",
      onClose: () => ctx.setId(null),
      body: [props([
        ["Releases to", printerName(s.printerId) ? h("a", { href: `printers/${enc(s.printerId)}` }, printerName(s.printerId)) : `${s.printerId} (missing)`],
        ["Last seen", s.lastSeenAt ? [time(s.lastSeenAt), h("span", { class: "sub" }, dateTime(s.lastSeenAt))] : "Never. Check its token and server_url."],
        ["Address", s.lastIp ? h("code", null, s.lastIp) : "—"],
        ["Added", dateTime(s.createdAt)],
      ]),
      sectionTitle("History"),
      activityList(events, { emptyText: "Nothing recorded yet." })],
      footer: [
        button("Remove", { kind: "danger", onclick: () => remove(s) }),
        button("New token", { iconName: "key", onclick: () => resetToken(s) }),
        button("Change printer", { kind: "primary", onclick: () => move(s) }),
      ],
    });
  }

  function add() {
    if (printers.length === 0) {
      formDialog({ title: "Add station", body: h("p", { style: "margin:0" }, "Add a printer first. Every station releases to one printer."), submitLabel: "OK", onSubmit: () => {} });
      return;
    }
    formDialog({
      title: "Add station",
      description: "You'll get a token for the station's config file.",
      submitLabel: "Add station",
      body: [
        field("ID", input("id", { required: true, placeholder: "e.g. office" }), "Letters, numbers and dashes. Usually named after where it is."),
        field("Releases to", select("printerId", printerOptions())),
      ],
      onSubmit: async (values) => {
        const created = await api.post("stations", values);
        await load();
        return stationConfig(created);
      },
    });
  }

  function move(s) {
    formDialog({
      title: `Change ${s.id}'s printer`,
      description: "Takes effect on the next tap. The station doesn't need restarting.",
      body: field("Releases to", select("printerId", printerOptions(), s.printerId)),
      onSubmit: async (values) => {
        await api.patch(`stations/${enc(s.id)}`, values);
        await load();
        open(s.id);
      },
    });
  }

  function resetToken(s) {
    formDialog({
      title: `New token for ${s.id}?`,
      description: "The station stops working until its station.toml has the new token.",
      submitLabel: "Make new token",
      danger: true,
      onSubmit: async () => stationConfig(await api.post(`stations/${enc(s.id)}/token`)),
    });
  }

  async function remove(s) {
    if (!await confirm({ title: `Remove station ${s.id}?`, message: "Taps at it will stop releasing jobs. Its token stops working.", confirmLabel: "Remove station" })) return;
    if (await attempt(() => api.del(`stations/${enc(s.id)}`), "Station removed") !== undefined) {
      ctx.setId(null);
      await load();
    }
  }

  await load();
  if (ctx.id) open(ctx.id);
  ctx.every(15000, () => { if (!document.querySelector(".drawer, dialog[open]")) load(); });
}

function stationConfig({ station, token }) {
  const config = `server_url = "${location.protocol}//${location.host}"\ntoken = "${token}"\nreader = "pcprox"`;
  return h("div", null,
    h("p", { style: "margin:0 0 10px" }, "Put this in ", h("code", null, "/etc/tapqueue/station.toml"), ` on ${station.id}'s reader box, then restart tapqueue-station:`),
    secret(config, "The token won't be shown again."),
    h("p", { class: "muted", style: "margin:10px 0 0; font-size:12.5px" }, "Use reader = \"keyboard\" and device = \"/dev/input/…\" for readers that type the card number."));
}
