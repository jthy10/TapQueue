import { api, enc } from "../api.js";
import { activityList } from "./activity.js";
import { h, pageHead, panel, button, pill, time, table, empty, drawer, props, sectionTitle, field, input, checkbox, checkField,
  formDialog, confirm, attempt, loading } from "../ui.js";

const tones = { ok: "ok", warning: "warn", error: "bad", offline: "bad" };

/** Ready, printing, a warning (toner low), a problem (paper jam, stopped), or unreachable. */
export function printerStatus(p) {
  const health = p.health;
  if (!p.checkedAt || !health) return pill("Checking…");
  if (health.level === "offline") return pill("Unreachable", "bad");
  const label = health.problems[0] ?? (health.state === "printing" ? "Printing" : "Ready");
  const more = health.problems.length > 1 ? ` +${health.problems.length - 1}` : "";
  return pill(label + more, tones[health.level]);
}

const isDark = (hex) => [1, 3, 5].every((i) => parseInt(hex.slice(i, i + 2), 16) < 0x40);

/** A thin bar per supply, e.g. the toner at 60%. */
export function supplyMeters(supplies, { compact } = {}) {
  if (!supplies?.length) return h("span", { class: "muted" }, compact ? "—" : "Not reported");
  return h("div", { class: `supplies${compact ? " compact" : ""}` }, supplies.map((s) => {
    // Black toner would vanish in dark mode, so near-black uses the text colour.
    const fill = s.low ? "var(--warn)" : !s.color || isDark(s.color) ? "var(--text)" : s.color;
    return h("div", { class: "supply", title: `${s.name}: ${s.level == null ? "level unknown" : `${s.level}%`}` },
      compact ? null : h("span", { class: "supply-name" }, s.name),
      h("span", { class: "meter" }, h("span", { style: `width:${s.level ?? 0}%;background:${fill}` })),
      h("span", { class: `supply-level${s.low ? " low" : ""}` }, s.level == null ? "?" : `${s.level}%`));
  }));
}

export async function render(root, ctx) {
  let printers = [], stations = [];
  const list = h("div", null, loading());
  root.append(pageHead("Printers", "The physical printers that held jobs are released to. TapQueue talks to them over IPP.",
    button("Check all", { iconName: "refresh", onclick: () => load(true) }),
    button("Add printer", { kind: "primary", iconName: "plus", onclick: () => edit() })),
  panel({ flush: true, body: list }));

  async function load(refresh) {
    [printers, stations] = await Promise.all([api.get(`printers${refresh ? "?refresh=true" : ""}`), api.get("stations")]);
    if (!ctx.current) return;
    list.replaceChildren(table({
      rows: printers,
      search: (p) => `${p.printer.id} ${p.printer.name} ${p.printer.location} ${p.uri} ${p.printer.makeAndModel ?? ""}`,
      onRowClick: (p) => open(p.printer.id),
      empty: empty("No printers yet", "Add one by its IPP address, e.g. ipp://192.0.2.10/ipp/print.",
        button("Add printer", { kind: "primary", onclick: () => edit() })),
      columns: [
        { label: "Printer", value: (p) => [h("span", { class: "cell-strong" }, p.printer.name), h("span", { class: "sub" }, p.printer.makeAndModel ?? p.printer.id)] },
        { label: "Location", value: (p) => p.printer.location || h("span", { class: "muted" }, "—") },
        { label: "Status", value: printerStatus },
        { label: "Supplies", value: (p) => supplyMeters(p.health?.supplies, { compact: true }) },
        { label: "Stations", value: (p) => usedBy(p).map((s) => s.id).join(", ") || h("span", { class: "muted" }, "None") },
        { label: "Checked", value: (p) => time(p.checkedAt) },
      ],
    }));
  }

  const usedBy = (p) => stations.filter((s) => s.printerId.toLowerCase() === p.printer.id.toLowerCase());

  async function open(id) {
    ctx.setId(id);
    const p = printers.find((x) => x.printer.id.toLowerCase() === String(id).toLowerCase());
    if (!p) return ctx.setId(null);
    const events = await api.get(`events?subject=${enc(`printer:${p.printer.id}`)}&limit=10`);
    const stationsHere = usedBy(p);
    drawer({
      title: p.printer.name,
      subtitle: p.printer.id,
      onClose: () => ctx.setId(null),
      body: [
        props([
          ["Status", printerStatus(p)],
          p.health?.since && ["Since", time(p.health.since)],
          p.health?.problems.length > 0 && ["Problems", h("ul", { class: "plain-list" }, p.health.problems.map((x) => h("li", null, x)))],
          p.health && !p.health.canPrint && ["Releases", pill("Held back until it's fixed", "bad")],
          ["Model", p.printer.makeAndModel ?? h("span", { class: "muted" }, "Unknown until it's reachable")],
          ["Location", p.printer.location || "—"],
          ["Address", h("code", null, p.uri)],
          ["Certificate", p.tlsSkipVerify ? pill("Not verified", "warn") : "Verified (ipps/https only)"],
          ["Last checked", time(p.checkedAt)],
        ]),
        sectionTitle("Supplies"),
        supplyMeters(p.health?.supplies),
        sectionTitle("Release stations"),
        stationsHere.length
          ? props(stationsHere.map((s) => [h("a", { href: `stations/${enc(s.id)}` }, s.id), `Last seen ${time(s.lastSeenAt).textContent}`]))
          : h("p", { class: "muted", style: "margin:0" }, "No station releases to this printer. Jobs can still be released to it from the client or this console."),
        sectionTitle("History"),
        activityList(events, { emptyText: "Nothing recorded yet." }),
      ],
      footer: [
        button("Remove", { kind: "danger", onclick: () => remove(p), title: stationsHere.length ? "Move its stations to another printer first" : null }),
        button("Check now", { iconName: "refresh", onclick: async () => {
          await attempt(() => api.get("printers?refresh=true"));
          await load();
          open(p.printer.id);
        } }),
        button("Edit", { kind: "primary", onclick: () => edit(p) }),
      ],
    });
  }

  function edit(p) {
    const adding = !p;
    formDialog({
      title: adding ? "Add printer" : `Edit ${p.printer.name}`,
      description: adding ? "TapQueue checks it can reach the printer as soon as it's added." : null,
      submitLabel: adding ? "Add printer" : "Save",
      body: [
        adding && field("ID", input("id", { required: true, placeholder: "e.g. office-m404" }), "Short and permanent: letters, numbers and dashes. Stations refer to the printer by it."),
        field("Name", input("name", { value: p?.printer.name ?? "", placeholder: "e.g. HP LaserJet, 2nd floor" }), "What people see in the client and at stations."),
        field("Location", input("location", { value: p?.printer.location ?? "", placeholder: "e.g. Office, by the kitchen" })),
        field("Address", input("uri", { required: true, value: p?.uri ?? "", placeholder: "ipp://192.0.2.10/ipp/print" }),
          "The printer's IPP address. Most network printers use ipp://<ip>/ipp/print, or ipps:// for encrypted."),
        checkField("Don't verify its certificate", checkbox("tlsSkipVerify", p?.tlsSkipVerify ?? false),
          "For ipps:// printers with the self-signed certificate most printers ship with."),
      ],
      onSubmit: async (values) => {
        const saved = adding ? await api.post("printers", values) : await api.patch(`printers/${enc(p.printer.id)}`, values);
        await load();
        open(saved.printer.id);
      },
    });
  }

  async function remove(p) {
    if (!await confirm({ title: `Remove ${p.printer.name}?`, message: "Held jobs stay held and can be released to another printer.", confirmLabel: "Remove printer" })) return;
    if (await attempt(() => api.del(`printers/${enc(p.printer.id)}`), "Printer removed") !== undefined) {
      ctx.setId(null);
      await load();
    }
  }

  await load();
  if (ctx.id) open(ctx.id);
  ctx.every(30000, () => { if (!document.querySelector(".drawer, dialog[open]")) load(); });
}
