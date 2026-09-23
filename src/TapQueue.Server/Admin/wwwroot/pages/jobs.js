import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, time, dateTime, bytes, table, empty, drawer, props, sectionTitle,
  field, select, formDialog, confirm, attempt, loading } from "../ui.js";

const filters = [["held", "Held"], ["", "All"], ["released", "Released"], ["canceled", "Canceled"], ["expired", "Expired"]];

/** A status pill for a job, shared with the overview and user details. */
export function jobStatus(job) {
  if (job.error && job.status === "held") return pill("Held, last release failed", "bad");
  const tone = { held: "accent", releasing: "warn", released: "ok" }[job.status];
  return pill(job.status[0].toUpperCase() + job.status.slice(1), tone);
}

export async function render(root, ctx) {
  let status = "held";
  let jobs = [];
  const list = h("div", null, loading());
  const segmented = h("div", { class: "segmented", role: "group" });
  root.append(pageHead("Jobs", "Print jobs waiting at the server, and what happened to the rest.",
    button("Refresh", { iconName: "refresh", onclick: () => load() })),
  panel({ flush: true, body: list }));

  async function load() {
    jobs = await api.get(status ? `jobs?status=${status}` : "jobs");
    if (!ctx.current) return;
    segmented.replaceChildren(...filters.map(([value, label]) =>
      h("button", { class: value === status ? "on" : "", onclick: () => { status = value; load(); } }, label)));
    list.replaceChildren(table({
      rows: jobs,
      search: (j) => `${j.name} ${j.owner ?? ""} ${j.claimedUser ?? ""} ${j.queueId}`,
      toolbar: segmented,
      onRowClick: (j) => open(j.id),
      empty: status === "held"
        ? empty("Nothing is held", "Jobs wait here until their owner taps a badge at a printer.")
        : empty("No jobs", "Nothing matches this filter."),
      columns: [
        { label: "Document", value: (j) => [h("span", { class: "cell-strong" }, j.name), h("span", { class: "sub" }, `#${j.id} · ${j.queueId}`)] },
        { label: "Owner", value: owner },
        { label: "Status", value: jobStatus },
        { label: "Size", class: "num", value: (j) => [bytes(j.sizeBytes), j.copies > 1 ? h("span", { class: "sub" }, `${j.copies} copies`) : null] },
        { label: "Submitted", value: (j) => time(j.submittedAt) },
      ],
    }));
  }

  async function open(id) {
    ctx.setId(id);
    const job = jobs.find((j) => j.id === Number(id)) ?? await attempt(() => api.get(`jobs/${enc(id)}`));
    if (!job) return ctx.setId(null);
    const held = job.status === "held";
    drawer({
      title: job.name,
      subtitle: `Job #${job.id}`,
      onClose: () => ctx.setId(null),
      body: [
        props([
          ["Status", jobStatus(job)],
          job.error && ["Last error", h("span", { style: "color:var(--bad)" }, job.error)],
          ["Owner", owner(job)],
          job.claimedUser && ["Sent as", h("span", null, job.claimedUser, h("span", { class: "sub" }, "The name the print client gave. Not verified."))],
          ["Queue", h("a", { href: `queues/${enc(job.queueId)}` }, job.queueId)],
          ["Format", h("code", null, job.documentFormat)],
          ["Size", `${bytes(job.sizeBytes)}${job.copies > 1 ? `, ${job.copies} copies` : ""}`],
        ]),
        sectionTitle("Timeline"),
        props([
          ["Submitted", dateTime(job.submittedAt)],
          held && ["Expires", [dateTime(job.expiresAt), h("span", { class: "sub" }, "Deleted if nobody releases it by then.")]],
          job.releasedAt && ["Released", `${dateTime(job.releasedAt)} to ${job.releasedPrinterId}`],
        ]),
      ],
      footer: held && [
        button("Cancel job", { kind: "danger", onclick: () => cancel(job) }),
        button("Release…", { kind: "primary", iconName: "send", disabled: !job.owner,
          title: job.owner ? null : "Only jobs matched to a person can be released", onclick: () => release(job) }),
      ],
    });
  }

  async function cancel(job) {
    if (!await confirm({ title: `Cancel "${job.name}"?`, message: "The document is deleted from the server. Its owner will have to print it again.", confirmLabel: "Cancel job" })) return;
    if (await attempt(() => api.del(`jobs/${job.id}`), "Job canceled") !== undefined) {
      ctx.setId(null);
      await load();
    }
  }

  async function release(job) {
    const printers = await api.get("printers");
    formDialog({
      title: "Release to a printer",
      description: `Prints "${job.name}" for ${job.owner} now, as if they'd tapped their badge.`,
      submitLabel: "Release",
      body: printers.length
        ? field("Printer", select("printerId", printers.map((p) => [p.printer.id, `${p.printer.name}${p.printer.online ? "" : " (unreachable)"}`])))
        : h("p", { class: "muted" }, "There are no printers yet."),
      onSubmit: async ({ printerId }) => {
        const { results } = await api.post("release", { username: job.owner, printerId, jobIds: [job.id] });
        const failed = results.find((r) => !r.success);
        if (failed) throw new Error(failed.error);
        ctx.setId(null);
        await load();
      },
    });
  }

  await load();
  if (ctx.id) open(ctx.id);
  ctx.every(10000, () => { if (!document.querySelector(".drawer, dialog[open]")) load(); });
}

function owner(job) {
  if (job.formerOwner) return h("span", { class: "muted" }, `${job.formerOwner} (deleted)`);
  if (job.owner) return h("a", { href: `users/${enc(job.owner)}`, onclick: (e) => e.stopPropagation() }, job.owner);
  return h("span", { class: "muted" }, "Unmatched", job.claimedUser && ` (sent as ${job.claimedUser})`);
}
