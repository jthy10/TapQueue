import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, table, empty, drawer, props, sectionTitle, field, input, select, checkbox, checkField,
  formDialog, confirm, attempt, loading, secret } from "../ui.js";

const media = [["na_letter_8.5x11in", "Letter"], ["na_legal_8.5x14in", "Legal"], ["iso_a4_210x297mm", "A4"]];
const mediaName = (id) => media.find(([v]) => v === id)?.[1] ?? id;

export async function render(root, ctx) {
  let queues = [], held = [];
  const list = h("div", null, loading());
  root.append(pageHead("Queues", "The printers people see in Windows. Jobs sent to a queue are held until their owner releases them.",
    button("Add queue", { kind: "primary", iconName: "plus", onclick: () => edit() })),
  panel({ flush: true, body: list }));

  async function load() {
    [queues, held] = await Promise.all([api.get("queues"), api.maybe("jobs?status=held", [])]);
    if (!ctx.current) return;
    list.replaceChildren(table({
      rows: queues,
      search: (q) => `${q.id} ${q.name} ${q.location}`,
      onRowClick: (q) => open(q.id),
      empty: empty("No queues yet", "Add one, then add it as a printer on each PC.", button("Add queue", { kind: "primary", onclick: () => edit() })),
      columns: [
        { label: "Queue", value: (q) => [h("span", { class: "cell-strong" }, q.name), h("span", { class: "sub" }, q.id)] },
        { label: "Location", value: (q) => q.location || h("span", { class: "muted" }, "—") },
        { label: "Defaults", value: (q) => `${mediaName(q.defaultMedia)} · ${q.color ? "Color" : "Black & white"} · ${q.duplex ? "Two-sided" : "One-sided"}` },
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
          ["Color", q.color ? "Offered" : "Black & white only"],
          ["Two-sided", q.duplex ? "Offered" : "One-sided only"],
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
        checkField("Offer color", checkbox("color", q?.color ?? false), "Only if every printer it's released to prints color."),
        checkField("Offer two-sided", checkbox("duplex", q?.duplex ?? false), "Only if every printer it's released to can print both sides."),
      ],
      onSubmit: async (values) => {
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
