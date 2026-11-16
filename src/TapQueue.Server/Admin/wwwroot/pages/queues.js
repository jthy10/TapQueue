import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, table, empty, drawer, props, sectionTitle, field, input, select, checkbox, checkField,
  formDialog, confirm, attempt, loading, secret } from "../ui.js";

const media = [["na_letter_8.5x11in", "Letter"], ["na_legal_8.5x14in", "Legal"], ["iso_a4_210x297mm", "A4"]];
const mediaName = (id) => media.find(([v]) => v === id)?.[1] ?? id;
const sidesPolicies = [
  ["any", "One side unless they choose two"],
  ["default-two-sided", "Two sides unless they choose one"],
  ["force-two-sided", "Always two sides"],
];
const sidesText = (q) => !q.duplex ? "One-sided only" : sidesPolicies.find(([v]) => v === q.sidesPolicy)?.[1] ?? "Offered";

export async function render(root, ctx) {
  let queues = [], held = [], printers = [];
  const printerName = (id) => printers.find((p) => p.printer.id.toLowerCase() === id.toLowerCase())?.printer.name ?? id;
  const list = h("div", null, loading());
  root.append(pageHead("Queues", "The printers people see in Windows. Jobs sent to a queue are held until their owner releases them, unless the queue prints straight to a printer.",
    button("Add queue", { kind: "primary", iconName: "plus", onclick: () => edit() })),
  panel({ flush: true, body: list }));

  async function load() {
    [queues, held, printers] = await Promise.all([api.get("queues"), api.maybe("jobs?status=held", []), api.get("printers")]);
    if (!ctx.current) return;
    list.replaceChildren(table({
      rows: queues,
      search: (q) => `${q.id} ${q.name} ${q.location}`,
      onRowClick: (q) => open(q.id),
      empty: empty("No queues yet", "Add one, then add it as a printer on each PC.", button("Add queue", { kind: "primary", onclick: () => edit() })),
      columns: [
        { label: "Queue", value: (q) => [h("span", { class: "cell-strong" }, q.name), h("span", { class: "sub" }, q.id)] },
        { label: "Jobs", value: (q) => q.directPrinterId ? [pill("Print straight away", "accent"), h("span", { class: "sub" }, `at ${printerName(q.directPrinterId)}`)] : pill("Held for release", "plain") },
        { label: "Location", value: (q) => q.location || h("span", { class: "muted" }, "—") },
        { label: "Rules", value: (q) => [`${mediaName(q.defaultMedia)} · ${q.color ? "Color" : "Black & white"} · ${q.duplex ? (q.sidesPolicy === "force-two-sided" ? "Always two-sided" : "Two-sided") : "One-sided"}`,
          q.maxPages > 0 && h("span", { class: "sub" }, `At most ${q.maxPages} pages a job`)] },
        { label: "Held jobs", class: "num", value: (q) => held.filter((j) => j.queueId.toLowerCase() === q.id.toLowerCase()).length },
      ],
    }));
  }

  function open(id) {
    ctx.setId(id);
    const q = queues.find((x) => x.id.toLowerCase() === String(id).toLowerCase());
    if (!q) return ctx.setId(null);
    // Same form the TapQueue client installs: IPP over http:// on the server's port.
    const url = `${location.protocol}//${location.host}${q.ippPath}`;
    drawer({
      title: q.name,
      subtitle: q.id,
      onClose: () => ctx.setId(null),
      body: [
        props([
          ["Description", q.description],
          ["Location", q.location || "—"],
          ["Paper", mediaName(q.defaultMedia)],
          ["Jobs", q.directPrinterId
            ? [`Print straight away at ${printerName(q.directPrinterId)}`, h("span", { class: "sub" }, "Not held: still matched to a person, counted and charged. One that can't print there waits to be released somewhere else.")]
            : ["Held until released", h("span", { class: "sub" }, "At any printer, by a badge tap or from the tray.")]],
          ["Color", q.color ? "Offered" : ["Black & white only", h("span", { class: "sub" }, "Whatever the job asks for.")]],
          ["Two-sided", sidesText(q)],
          ["Longest job", q.maxPages > 0 ? `${q.maxPages} pages, copies included` : "No limit"],
        ]),
        sectionTitle("Adding it on a PC"),
        h("p", { class: "muted", style: "margin:0 0 8px" }, "The TapQueue client adds it automatically. To add it by hand, use this address:"),
        secret(url),
        h("p", { class: "muted", style: "margin:12px 0 8px" }, "or in PowerShell:"),
        secret(`Add-Printer -Name "${q.name}" -IppURL "${url}"`),
      ],
      footer: [
        button("Remove", { kind: "danger", onclick: () => remove(q) }),
        button("Edit", { kind: "primary", onclick: () => edit(q) }),
      ],
    });
  }

  function edit(q) {
    const adding = !q;
    formDialog({
      title: adding ? "Add queue" : `Edit ${q.name}`,
      description: adding ? null : "Changes show up on PCs the next time Windows asks the queue what it can do.",
      submitLabel: adding ? "Add queue" : "Save",
      body: [
        adding && field("ID", input("id", { required: true, placeholder: "e.g. secure" }), "Part of the address PCs print to, so it can't change later."),
        field("Name", input("name", { required: true, value: q?.name ?? "", placeholder: "e.g. TapQueue Secure Print" }), "The printer name people see in Windows."),
        field("Description", input("description", { value: q?.description ?? "", placeholder: "Tap your badge at any TapQueue printer to release your print." })),
        field("Location", input("location", { value: q?.location ?? "" })),
        field("Default paper", select("defaultMedia", media, q?.defaultMedia ?? "na_letter_8.5x11in")),
        checkField("Offer color", checkbox("color", q?.color ?? false), "Off: everything printed here comes out in black and white, whatever the job asks for."),
        checkField("Offer two-sided", checkbox("duplex", q?.duplex ?? false), "Only if every printer it's released to can print both sides."),
        field("Sides of the paper", select("sidesPolicy", sidesPolicies, q?.sidesPolicy ?? "any"), "Only when two-sided is offered. \"Always\" overrides a job that asks for one side."),
        field("Longest job (pages)", input("maxPages", { type: "number", min: 0, max: 1000000, required: true, value: q?.maxPages ?? 0 }),
          "Pages times copies. A longer job is refused, and the person is told why. 0 for no limit."),
        field("Jobs", select("directPrinterId", [["", "Hold them until their owner releases them"],
          ...printers.map((p) => [p.printer.id, `Print straight away at ${p.printer.name}`])], q?.directPrinterId ?? ""),
          "Printing straight away skips the tap, but jobs are still matched to a person, counted against page limits and charged. To give different people different rules, make a queue for each and let groups choose who prints to which."),
      ],
      onSubmit: async (values) => {
        values.maxPages = Number(values.maxPages);
        if (adding && !values.directPrinterId) delete values.directPrinterId;
        const saved = adding ? await api.post("queues", values) : await api.patch(`queues/${enc(q.id)}`, values);
        await load();
        open(saved.id);
      },
    });
  }

  async function remove(q) {
    if (!await confirm({
      title: `Remove ${q.name}?`,
      message: "PCs that have it installed won't be able to print to it anymore. Jobs already held stay held.",
      confirmLabel: "Remove queue",
    })) return;
    if (await attempt(() => api.del(`queues/${enc(q.id)}`), "Queue removed") !== undefined) {
      ctx.setId(null);
      await load();
    }
  }

  await load();
  if (ctx.id) open(ctx.id);
}
