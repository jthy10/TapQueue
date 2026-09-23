// Page limits, shared by Users and Groups.
import { api } from "../api.js";
import { h, field, input, select, dateTime, plural } from "../ui.js";

const per = { day: "a day", week: "a week", month: "a month" };
const current = { day: "today", week: "this week", month: "this month" };

/** "100 pages a month", or null for no limit. */
export const quotaText = (q) => q ? `${plural(q.pages, "page")} ${per[q.period] ?? q.period}` : null;

/** Form controls for a limit; read them back with {@link saveQuota}. */
export function quotaField(quota, noneLabel, hint) {
  const mode = select("quotaMode", [["none", noneLabel], ["limit", "Limit to"]], quota ? "limit" : "none");
  const amount = h("div", { class: "field-row" },
    input("quotaPages", { type: "number", min: 0, max: 1000000, value: quota?.pages ?? 100 }),
    select("quotaPeriod", Object.entries(per).map(([v, label]) => [v, `pages ${label}`]), quota?.period ?? "month"));
  const sync = () => { amount.hidden = mode.value !== "limit"; };
  mode.addEventListener("change", sync);
  sync();
  return field("Page limit", h("div", { class: "quota-field" }, mode, amount), hint);
}

/** Puts or removes the limit at path (users/x/quota, groups/x/quota) if the form changed it. */
export async function saveQuota(path, values, existing) {
  const next = values.quotaMode === "limit" ? { pages: Number(values.quotaPages), period: values.quotaPeriod } : null;
  if (next?.pages === existing?.pages && next?.period === existing?.period) return;
  if (next) await api.put(path, next);
  else if (existing) await api.del(path);
}

/** One limit that applies to a user: used / limit, with a bar. */
export function usageMeter(u, groupName) {
  const share = u.pages ? Math.min(1, u.used / u.pages) : 1;
  const tone = u.remaining === 0 ? "bad" : share >= 0.8 ? "warn" : "ok";
  const from = u.source === "user" ? "Their own limit" : `From ${groupName(u.source.slice("group:".length))}`;
  return h("div", { class: "quota" },
    h("div", { class: "quota-head" },
      h("span", null, h("b", null, `${u.used} of ${plural(u.pages, "page")}`), ` used ${current[u.period] ?? ""}`),
      h("span", { class: "muted" }, `${u.remaining} left`)),
    h("div", { class: `meter ${tone}` }, h("span", { style: `width:${Math.round(share * 100)}%` })),
    h("span", { class: "sub" }, `${from}, ${per[u.period]}. Starts over ${dateTime(u.resetsAt)}.`));
}
