import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, table, empty, drawer, props, sectionTitle, field, input, checkbox, checkField,
  checkList, formDialog, confirm, attempt, loading, plural } from "../ui.js";
import { activityList } from "./activity.js";
import { quotaText, quotaField, saveQuota } from "./quota.js";

export async function render(root, ctx) {
  let groups = [], queues = [], printers = [], users = [];
  const list = h("div", null, loading());
  root.append(pageHead("Groups", "Which queues people may print to, which printers they may release at, and how many pages they get. Someone in several groups gets everything any of them allows.",
    button("Add group", { kind: "primary", iconName: "plus", onclick: () => edit() })),
  h("div", { class: "callout info" }, "People who aren't in any group can print to every queue and release at every printer. Put someone in a group to limit them."),
  panel({ flush: true, body: list }));

  const queueName = (id) => queues.find((q) => q.id.toLowerCase() === id.toLowerCase())?.name ?? id;
  const printerName = (id) => printers.find((p) => p.printer.id.toLowerCase() === id.toLowerCase())?.printer.name ?? id;
  const limit = (g) => quotaText(g.quota) ?? h("span", { class: "muted" }, "No limit");
  const allows = (all, ids, name, noun) => all ? pill(`Every ${noun}`, "ok") : ids.length ? ids.map(name).join(", ") : pill(`No ${noun}s`, "bad");

  async function load() {
    [groups, queues, printers, users] = await Promise.all([api.get("groups"), api.get("queues"), api.get("printers"), api.get("users")]);
    if (!ctx.current) return;
    list.replaceChildren(table({
      rows: groups,
      search: (g) => `${g.id} ${g.name} ${g.description}`,
      onRowClick: (g) => open(g.id),
      empty: empty("No groups yet", "Until there are, everyone can print everywhere.", button("Add group", { kind: "primary", onclick: () => edit() })),
      columns: [
        { label: "Group", value: (g) => [h("span", { class: "cell-strong" }, g.name), h("span", { class: "sub" }, g.description || g.id)] },
        { label: "Members", class: "num", value: (g) => g.memberCount },
        { label: "Prints to", value: (g) => allows(g.allQueues, g.queueIds, queueName, "queue") },
        { label: "Releases at", value: (g) => allows(g.allPrinters, g.printerIds, printerName, "printer") },
        { label: "Page limit", value: limit },
      ],
    }));
  }

  async function open(id) {
    ctx.setId(id);
    const g = groups.find((x) => x.id.toLowerCase() === String(id).toLowerCase());
    if (!g) return ctx.setId(null);
    const [members, events] = await Promise.all([api.get(`groups/${enc(g.id)}/members`), api.get(`events?subject=${enc(`group:${g.id}`)}&limit=10`)]);
    const refresh = async () => { await load(); open(g.id); };
    const others = users.filter((u) => !members.includes(u.username));

    drawer({
      title: g.name,
      subtitle: g.id,
      onClose: () => ctx.setId(null),
      body: [
        props([
          g.description && ["Description", g.description],
          ["Prints to", allows(g.allQueues, g.queueIds, queueName, "queue")],
          ["Releases at", allows(g.allPrinters, g.printerIds, printerName, "printer")],
          ["Page limit", limit(g)],
        ]),
        sectionTitle(`Members (${members.length})`),
        members.length
          ? h("div", { class: "panel" }, table({ rows: members, columns: [
            { label: "User", value: (m) => h("a", { href: `users/${enc(m)}` }, m) },
            { label: "", class: "num", value: (m) => button("Remove", { small: true, kind: "ghost danger", onclick: async () => {
              if (await attempt(() => api.del(`groups/${enc(g.id)}/members/${enc(m)}`), `Removed ${m}`) !== undefined) refresh();
            } }) },
          ] }))
          : h("p", { class: "muted", style: "margin:0" }, "Nobody yet."),
        others.length > 0 && h("div", { style: "margin-top:10px" }, button("Add members", { small: true, iconName: "plus", onclick: () => addMembers(g, others, refresh) })),
        sectionTitle("Changes"),
        activityList(events, { emptyText: "No changes recorded." }),
      ],
      footer: [
        button("Remove", { kind: "danger", onclick: () => remove(g) }),
        button("Edit", { kind: "primary", onclick: () => edit(g) }),
      ],
    });
  }

  function addMembers(g, others, onDone) {
    formDialog({
      title: `Add to ${g.name}`,
      submitLabel: "Add",
      body: field("People", checkList("usernames", others.map((u) => [u.username, u.displayName, u.username]))),
      onSubmit: async ({ usernames = [] }) => {
        for (const username of usernames) await api.put(`groups/${enc(g.id)}/members/${enc(username)}`);
        await onDone();
      },
    });
  }

  function edit(g) {
    const adding = !g;
    const allQueues = checkbox("allQueues", g?.allQueues ?? true);
    const allPrinters = checkbox("allPrinters", g?.allPrinters ?? true);
    const queueList = checkList("queueIds", queues.map((q) => [q.id, q.name, q.id]), g?.queueIds);
    const printerList = checkList("printerIds", printers.map((p) => [p.printer.id, p.printer.name, p.printer.location || p.printer.id]), g?.printerIds);
    const sync = () => { queueList.hidden = allQueues.checked; printerList.hidden = allPrinters.checked; };
    allQueues.addEventListener("change", sync);
    allPrinters.addEventListener("change", sync);
    sync();

    formDialog({
      title: adding ? "Add group" : `Edit ${g.name}`,
      submitLabel: adding ? "Add group" : "Save",
      body: [
        adding && field("ID", input("id", { required: true, placeholder: "e.g. staff" }), "Letters, numbers and dashes. Can't change later."),
        field("Name", input("name", { required: true, value: g?.name ?? "", placeholder: "e.g. Staff" })),
        field("Description", input("description", { value: g?.description ?? "", placeholder: "Optional" })),
        sectionTitle("Printing"),
        checkField("Every queue", allQueues, "Including queues added later."),
        queueList,
        sectionTitle("Releasing"),
        checkField("Every printer", allPrinters, "Including printers added later."),
        printerList,
        sectionTitle("Pages"),
        quotaField(g?.quota, "No limit", "Counted when jobs are released. If someone is in several groups with limits, the most generous one counts; a limit set on the person overrides them all."),
      ],
      onSubmit: async (values) => {
        const { quotaMode, quotaPages, quotaPeriod, ...rest } = values;
        const body = { ...rest, queueIds: values.queueIds ?? [], printerIds: values.printerIds ?? [] };
        const saved = adding ? await api.post("groups", body) : await api.patch(`groups/${enc(g.id)}`, body);
        await saveQuota(`groups/${enc(saved.id)}/quota`, values, g?.quota);
        await load();
        open(saved.id);
      },
    });
  }

  async function remove(g) {
    if (!await confirm({
      title: `Remove ${g.name}?`,
      message: `Its ${plural(g.memberCount, "member")} lose what it allowed. Anyone left in no group can use everything.`,
      confirmLabel: "Remove group",
    })) return;
    if (await attempt(() => api.del(`groups/${enc(g.id)}`), "Group removed") !== undefined) {
      ctx.setId(null);
      await load();
    }
  }

  await load();
  if (ctx.id) open(ctx.id);
}
