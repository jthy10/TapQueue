import { api, enc } from "../api.js";
import { h, pageHead, panel, table, empty, loading, input, select, plural } from "../ui.js";

const groupings = [["user", "People"], ["group", "Groups"], ["printer", "Printers"], ["queue", "Queues"], ["day", "Days"], ["month", "Months"]];
const headings = { user: "Person", group: "Group", printer: "Printer", queue: "Queue", day: "Day", month: "Month" };
// Where a row's name links to, for the things that have a page.
const links = { user: "users", group: "groups", printer: "printers", queue: "queues" };

const iso = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

/** [from, to] for each choice, as of today. */
function periods() {
  const now = new Date();
  const y = now.getFullYear(), m = now.getMonth(), d = now.getDate();
  return {
    month: [new Date(y, m, 1), now],
    lastMonth: [new Date(y, m - 1, 1), new Date(y, m, 0)],
    week: [new Date(y, m, d - 6), now],
    days30: [new Date(y, m, d - 29), now],
    year: [new Date(y, 0, 1), now],
    lastYear: [new Date(y - 1, 0, 1), new Date(y - 1, 11, 31)],
  };
}

const number = (n) => n.toLocaleString();

export async function render(root, ctx) {
  let by = "user";
  let report = null;
  const period = select("period", [["month", "This month"], ["lastMonth", "Last month"], ["week", "Last 7 days"], ["days30", "Last 30 days"],
    ["year", "This year"], ["lastYear", "Last year"], ["custom", "Other dates"]], "month");
  const from = input("from", { type: "date", "aria-label": "From", required: true });
  const to = input("to", { type: "date", "aria-label": "To", required: true });
  const dates = h("span", { style: "gap:8px;align-items:center" }, from, "to", to);
  const segmented = h("div", { class: "segmented", role: "group" });
  const download = h("a", { class: "btn" }, "Download CSV");
  const summary = h("div", { class: "grid stats" });
  const body = h("div", null, loading());

  function setPeriod() {
    dates.style.display = period.value === "custom" ? "inline-flex" : "none";
    if (period.value !== "custom") [from.value, to.value] = periods()[period.value].map(iso);
  }
  period.onchange = () => { setPeriod(); load(); };
  from.onchange = to.onchange = () => { if (from.value && to.value) load(); };

  root.append(pageHead("Reports", "Pages printed, from the jobs people released.", download),
    summary,
    panel({ flush: true, body: [h("div", { class: "toolbar" }, period, dates, h("div", { class: "grow" }), segmented), body] }));

  async function load() {
    const query = `from=${enc(from.value)}&to=${enc(to.value)}&by=${by}`;
    segmented.replaceChildren(...groupings.map(([value, label]) =>
      h("button", { class: value === by ? "on" : "", onclick: () => { by = value; load(); } }, label)));
    download.href = `/api/v1/admin/reports/usage?${query}&format=csv`;
    try {
      report = await api.get(`reports/usage?${query}`);
    } catch (err) {
      if (ctx.current) body.replaceChildren(empty("Can't make that report", err.message));
      return;
    }
    if (ctx.current) draw();
  }

  function draw() {
    const most = Math.max(1, ...report.rows.map((r) => r.pages));
    const byTime = by === "day" || by === "month";
    const people = new Set(report.rows.filter((r) => r.key).map((r) => r.key)).size;
    summary.replaceChildren(
      stat("Pages", number(report.pages), `${number(report.colorPages)} colour, ${number(report.monoPages)} black and white, ${number(report.duplexPages)} two-sided`),
      stat("Jobs", number(report.jobs), report.jobs ? `${number(Math.round(report.pages / report.jobs))} pages a job on average` : "Nothing released in this period"),
      stat(byTime ? (by === "day" ? "Busiest day" : "Busiest month") : `${groupings.find(([v]) => v === by)[1]} that printed`,
        byTime ? (report.pages ? report.rows.reduce((a, r) => (r.pages > a.pages ? r : a)).key : "None") : number(people),
        `Days are the server's (${report.timeZone})`));
    body.replaceChildren(
      report.uncountedJobs ? h("div", { class: "callout info", style: "margin:12px 16px 0" },
        `${plural(report.uncountedJobs, "job")} couldn't be counted and ${report.uncountedJobs === 1 ? "counts" : "count"} as 1 page a copy.`) : "",
      report.pages > report.colorPages + report.monoPages ? h("div", { class: "callout info", style: "margin:12px 16px 0" },
        `${number(report.pages - report.colorPages - report.monoPages)} pages are neither colour nor black and white here: their jobs left the choice to the printer, or were printed before TapQueue 0.16.`) : "",
      by === "group" ? h("div", { class: "callout info", style: "margin:12px 16px 0" },
        "Groups are the ones people are in today. Someone in two groups counts in both, so the rows can add up to more than the total.") : "",
      table({
        rows: byTime ? report.rows : report.rows.filter((r) => r.jobs > 0),
        empty: empty("Nothing was printed", "No jobs were released in this period."),
        search: byTime ? null : (r) => `${r.key} ${r.name}`,
        columns: [
          { label: headings[by], value: (r) => byTime ? h("span", { class: "mono" }, r.key)
            : [r.key && links[by] ? h("a", { class: "cell-strong", href: `${links[by]}/${enc(r.key)}` }, r.name) : h("span", { class: "cell-strong" }, r.name),
              r.key && r.key !== r.name ? h("span", { class: "sub" }, r.key) : ""] },
          { label: "Jobs", class: "num", value: (r) => number(r.jobs) },
          { label: "Pages", class: "num", value: (r) => number(r.pages) },
          { label: "Colour", class: "num", value: (r) => number(r.colorPages) },
          { label: "Black and white", class: "num", value: (r) => number(r.monoPages) },
          { label: "Two-sided", class: "num", value: (r) => number(r.duplexPages) },
          { label: "", value: (r) => h("div", { class: "report-bar", title: report.pages ? `${Math.round(100 * r.pages / report.pages)}% of all pages` : "" },
            h("span", { style: `width:${(100 * r.pages / most).toFixed(1)}%` })) },
        ],
      }));
  }

  setPeriod();
  await load();
}

function stat(label, value, note) {
  return h("div", { class: "panel stat" },
    h("div", { class: "stat-label" }, label),
    h("div", { class: "stat-value" }, value),
    h("div", { class: "stat-note" }, note));
}
