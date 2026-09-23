import { api, enc } from "../api.js";
import { activityList } from "./activity.js";
import { h, pageHead, panel, button, pill, time, dateTime, duration, table, empty, drawer, props, sectionTitle, field, input, select,
  formDialog, confirm, attempt, loading, secret } from "../ui.js";

/** Online, out of service, reader trouble, or offline. */
export function stationStatus(s) {
  if (!s.online) return pill(s.lastHeartbeatAt ? "Offline" : "Never reported in", "bad");
  if (!s.settings.enabled) return pill("Out of service", "warn");
  if (s.readerStatus && s.readerStatus !== "ok") return pill("Reader problem", "warn");
  return pill("Online", "ok");
}

const title = (s) => s.name || s.id;

export async function render(root, ctx) {
  let stations = [], printers = [];
  const list = h("div", null, loading());
  root.append(pageHead("Stations", "Badge readers next to printers. Tapping a card at one releases its owner's jobs to that station's printer.",
    button("Add station", { kind: "primary", iconName: "plus", onclick: add })),
  panel({ flush: true, body: list }));

  const printerName = (id) => printers.find((p) => p.printer.id.toLowerCase() === id.toLowerCase())?.printer.name;
  const printerOptions = () => printers.map((p) => [p.printer.id, `${p.printer.name} (${p.printer.id})`]);

  async function load() {
    [stations, printers] = await Promise.all([api.get("stations"), api.get("printers")]);
    if (!ctx.current) return;
    list.replaceChildren(table({
      rows: stations,
      search: (s) => `${s.id} ${s.name} ${s.location} ${s.printerId} ${s.lastIp ?? ""}`,
      onRowClick: (s) => open(s.id),
      empty: empty("No stations yet", "Add one, then put its token in /etc/tapqueue/station.toml on the reader box.",
        button("Add station", { kind: "primary", onclick: add })),
      columns: [
        { label: "Station", value: (s) => [h("span", { class: "cell-strong" }, title(s)), h("span", { class: "sub" }, s.location || s.id)] },
        { label: "Status", value: stationStatus },
        { label: "Releases to", value: (s) => printerName(s.printerId) ?? h("span", { style: "color:var(--bad)" }, `${s.printerId} (missing)`) },
        { label: "Version", value: (s) => s.version ? h("span", { class: "mono" }, s.version) : h("span", { class: "muted" }, "—") },
        { label: "Last heard from", value: (s) => time(s.lastHeartbeatAt ?? s.lastSeenAt) },
      ],
    }));
  }

  async function open(id) {
    ctx.setId(id);
    const s = stations.find((x) => x.id.toLowerCase() === String(id).toLowerCase());
    if (!s) return ctx.setId(null);
    const events = await api.get(`events?subject=${enc(`station:${s.id}`)}&limit=10`);
    const set = s.settings;
    const fromToml = () => h("span", { class: "muted" }, "station.toml");
    drawer({
      title: title(s),
      subtitle: s.name ? `${s.id} · release station` : "Release station",
      onClose: () => ctx.setId(null),
      body: [
        !set.enabled && h("div", { class: "callout" }, `Out of service. Taps say: "${set.maintenanceMessage || "This station is out of service. Use another printer."}"`),
        props([
          ["Status", stationStatus(s)],
          s.readerStatus && s.readerStatus !== "ok" && ["Reader", h("span", { style: "color:var(--warn)" }, s.readerStatus)],
          ["Releases to", printerName(s.printerId) ? h("a", { href: `printers/${enc(s.printerId)}` }, printerName(s.printerId)) : `${s.printerId} (missing)`],
          s.location && ["Location", s.location],
          ["Version", s.version ? h("span", { class: "mono" }, s.version) : "Unknown (hasn't reported in)"],
          s.startedAt && s.online && ["Running for", duration(s.startedAt)],
          ["Address", s.lastIp ? h("code", null, s.lastIp) : "—"],
          ["Last heard from", s.lastHeartbeatAt || s.lastSeenAt ? [time(s.lastHeartbeatAt ?? s.lastSeenAt), h("span", { class: "sub" }, dateTime(s.lastHeartbeatAt ?? s.lastSeenAt))] : "Never. Check its token and server_url."],
          s.pendingCommand && ["Waiting to", `${s.pendingCommand} (on its next heartbeat)`],
        ]),
        sectionTitle("Reader settings"),
        props([
          ["Reader", set.reader ?? fromToml()],
          ["Device", set.device ? h("code", null, set.device) : set.reader ? "Found automatically" : fromToml()],
          ["Repeat time", set.repeatSeconds != null ? `${set.repeatSeconds} s` : fromToml()],
          ["Min. card length", set.minCardLength ?? fromToml()],
        ]),
        sectionTitle("History"),
        activityList(events, { emptyText: "Nothing recorded yet." }),
      ],
      footer: [
        h("div", { class: "left" }, button("Remove", { kind: "ghost danger", onclick: () => remove(s) })),
        button("New token", { iconName: "key", onclick: () => resetToken(s) }),
        button("Restart", { iconName: "refresh", onclick: () => restart(s) }),
        set.enabled
          ? button("Take out of service", { onclick: () => outOfService(s) })
          : button("Put back in service", { onclick: () => update(s, { enabled: true }, "Back in service") }),
        button("Settings", { kind: "primary", onclick: () => settings(s) }),
      ],
    });
  }

  async function update(s, body, message) {
    if (await attempt(() => api.patch(`stations/${enc(s.id)}`, body), message) === undefined) return;
    await load();
    open(s.id);
  }

  function settings(s) {
    const set = s.settings;
    const local = "Use station.toml";
    formDialog({
      title: `${title(s)} settings`,
      description: "Stations pick these up within 15 seconds. Changing the reader restarts the station.",
      submitLabel: "Save",
      body: [
        h("div", { class: "field-row" },
          field("Name", input("name", { value: s.name, placeholder: s.id })),
          field("Location", input("location", { value: s.location, placeholder: "e.g. 2nd floor, by the kitchen" }))),
        field("Releases to", select("printerId", printerOptions(), s.printerId)),
        sectionTitle("Badge reader"),
        field("Reader", select("reader", [["", local], ["pcprox", "RFIDeas pcProx"], ["keyboard", "Keyboard-style reader"]], set.reader ?? "")),
        field("Device", input("device", { value: set.device ?? "", placeholder: "/dev/input/by-id/…-event-kbd" }),
          "Keyboard-style readers need their /dev/input device. pcProx readers are found automatically; leave it empty."),
        h("div", { class: "field-row" },
          field("Repeat time (seconds)", input("repeatSeconds", { type: "number", min: 0, max: 3600, value: set.repeatSeconds ?? "", placeholder: local }),
            "The same card again within this long counts as one tap."),
          field("Min. card length", input("minCardLength", { type: "number", min: 1, max: 64, value: set.minCardLength ?? "", placeholder: local }),
            "Shorter reads are ignored as noise.")),
      ],
      onSubmit: async (v) => {
        const reset = [];
        const body = { name: v.name, location: v.location, printerId: v.printerId };
        if (v.reader) { body.reader = v.reader; body.device = v.device; } else reset.push("reader", "device");
        if (v.repeatSeconds !== "") body.repeatSeconds = Number(v.repeatSeconds); else reset.push("repeatSeconds");
        if (v.minCardLength !== "") body.minCardLength = Number(v.minCardLength); else reset.push("minCardLength");
        await api.patch(`stations/${enc(s.id)}`, { ...body, reset });
        await load();
        open(s.id);
      },
    });
  }

  function outOfService(s) {
    formDialog({
      title: `Take ${title(s)} out of service?`,
      description: "Taps release nothing and get this message instead. The station keeps reporting in.",
      submitLabel: "Take out of service",
      danger: true,
      body: field("Message", input("maintenanceMessage", { value: s.settings.maintenanceMessage, placeholder: "e.g. Printer being serviced, use the one upstairs." })),
      onSubmit: async ({ maintenanceMessage }) => {
        await api.patch(`stations/${enc(s.id)}`, { enabled: false, maintenanceMessage });
        await load();
        open(s.id);
      },
    });
  }

  async function restart(s) {
    const message = s.online ? "Restart requested; it happens within 15 seconds" : `${title(s)} is offline; it'll restart when it next reports in`;
    if (await attempt(() => api.post(`stations/${enc(s.id)}/restart`), message) === undefined) return;
    await load();
    open(s.id);
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

  function resetToken(s) {
    formDialog({
      title: `New token for ${title(s)}?`,
      description: "The station stops working until its station.toml has the new token.",
      submitLabel: "Make new token",
      danger: true,
      onSubmit: async () => stationConfig(await api.post(`stations/${enc(s.id)}/token`)),
    });
  }

  async function remove(s) {
    if (!await confirm({ title: `Remove station ${title(s)}?`, message: "Taps at it will stop releasing jobs. Its token stops working.", confirmLabel: "Remove station" })) return;
    if (await attempt(() => api.del(`stations/${enc(s.id)}`), "Station removed") !== undefined) {
      ctx.setId(null);
      await load();
    }
  }

  await load();
  if (ctx.id) open(ctx.id);
  ctx.every(10000, () => { if (!document.querySelector(".drawer, dialog[open]")) load(); });
}

function stationConfig({ station, token }) {
  const config = `server_url = "${location.protocol}//${location.host}"\ntoken = "${token}"\nreader = "pcprox"`;
  return h("div", null,
    h("p", { style: "margin:0 0 10px" }, "Put this in ", h("code", null, "/etc/tapqueue/station.toml"), ` on ${station.id}'s reader box, then restart tapqueue-station:`),
    secret(config, "The token won't be shown again."),
    h("p", { class: "muted", style: "margin:10px 0 0; font-size:12.5px" }, "Everything besides the server and token can then be set from this console."));
}
