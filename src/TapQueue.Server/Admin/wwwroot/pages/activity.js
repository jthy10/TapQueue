import { api, enc } from "../api.js";
import { h, icon, pageHead, panel, button, time, empty, loading } from "../ui.js";

const filters = [["", "Everything"], ["job", "Jobs"], ["tap", "Taps"], ["signin", "Sign-ins"], ["printer", "Printers"], ["crash", "Crashes"], ["admin", "Admin changes"]];
const icons = { admin: "admins", job: "jobs", tap: "cards", signin: "workstations", crash: "alert", printer: "printers" };
const pages = { user: "users", group: "groups", printer: "printers", station: "stations", queue: "queues", job: "jobs" };

/** "user:alice" -> "users/alice", for linking an event to what it's about. */
export function subjectHref(subject) {
  const [kind, ...rest] = (subject ?? "").split(":");
  return pages[kind] && rest.length ? `${pages[kind]}/${enc(rest.join(":"))}` : null;
}

/** A compact list of events, newest first. Used here and in detail drawers. */
export function activityList(events, { emptyText = "Nothing has happened yet." } = {}) {
  if (events.length === 0) return h("p", { class: "muted", style: "margin:0" }, emptyText);
  return h("ul", { class: "feed" }, events.map((e) => {
    const href = subjectHref(e.subject);
    return h("li", null,
      h("span", { class: `feed-icon ${e.category}` }, icon(icons[e.category] ?? "activity", 14)),
      h("div", { class: "feed-body" },
        href ? h("a", { href, class: "feed-text" }, e.message) : h("span", { class: "feed-text" }, e.message),
        h("span", { class: "feed-meta" }, time(e.at), ` · ${actorName(e.actor)}`)));
  }));
}

function actorName(actor) {
  if (actor.startsWith("station:")) return `station ${actor.slice(8)}`;
  if (actor === "system") return "TapQueue";
  return actor;
}

export async function render(root, ctx) {
  let category = "";
  let events = [];
  const segmented = h("div", { class: "segmented", role: "group" });
  const list = h("div", { class: "panel-body" }, loading());
  const more = button("Load older", { onclick: () => load({ older: true }) });
  const foot = h("div", { class: "toolbar", style: "border-top:1px solid var(--border);border-bottom:none;justify-content:center" }, more);

  root.append(pageHead("Activity", "Everything that happened: prints and releases, badge taps, sign-ins, printer problems and admin changes. Kept for 90 days."),
    panel({ flush: true, body: [h("div", { class: "toolbar" }, segmented), list, foot] }));

  async function load({ older } = {}) {
    const query = new URLSearchParams({ limit: "50" });
    if (category) query.set("category", category);
    if (older && events.length) query.set("before", events.at(-1).id);
    const page = await api.get(`events?${query}`);
    if (!ctx.current) return;
    events = older ? [...events, ...page] : page;
    segmented.replaceChildren(...filters.map(([value, label]) =>
      h("button", { class: value === category ? "on" : "", onclick: () => { category = value; load(); } }, label)));
    list.replaceChildren(events.length ? activityList(events) : empty("Nothing yet", "Activity shows up here as people print, tap and sign in."));
    foot.hidden = page.length < 50;
  }

  await load();
  // Keep the newest page current, unless the admin has paged back through history.
  ctx.every(10000, () => { if (events.length <= 50) load(); });
}
