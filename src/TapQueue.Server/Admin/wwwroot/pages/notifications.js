import { api } from "../api.js";
import { h, pageHead, panel, button, pill, props, table, empty, time, dateTime, plural, field, input, select, checkbox, checkField,
  formDialog, attempt, toast } from "../ui.js";

const securityLabel = { starttls: "STARTTLS", tls: "TLS", none: "No encryption" };
// What each kind of encryption usually listens on, to fill in when the kind is changed.
const usualPort = { starttls: 587, tls: 465, none: 25 };

const kinds = [
  ["printerProblems", "Printer problems", "A printer is unreachable or has stopped: paper jam, out of paper, door open."],
  ["printerWarnings", "Printer warnings", "A printer still prints but wants attention soon: toner low, paper low."],
  ["stationsOffline", "Stations going offline", "A release station stops checking in, so cards tapped there do nothing."],
  ["crashReports", "Crash reports", "The tray app or TapQueue service on a PC crashed. At most one email an hour for each PC."],
];

export async function render(root, ctx) {
  let s = await api.get("server/notifications");
  if (!ctx.current) return;

  const canEdit = ctx.can("server", "admin");
  const problem = h("div");
  const server = h("div");
  const alerts = h("div");
  const open = h("div");
  const testButton = button("Send test email", { iconName: "send", onclick: test });
  const toggleButton = button("Turn on", { kind: "primary", onclick: toggle });

  root.append(
    pageHead("Notifications", "Emails when a printer has a problem, a station goes offline or a PC's client crashes. They're sent through your own mail server or provider.",
      canEdit && testButton, canEdit && toggleButton),
    problem,
    h("div", { class: "grid two" },
      panel({ title: "Mail server", description: "The SMTP server emails go out through.", actions: canEdit && button("Edit", { small: true, onclick: editServer }), body: server }),
      panel({ title: "Alerts", description: "Who gets an email, and what for.", actions: canEdit && button("Edit", { small: true, onclick: editAlerts }), body: alerts })),
    panel({ title: "Wrong right now", description: "What an email went out about and hasn't recovered. Another email follows when it does.", flush: true, body: open }));

  const ready = () => s.config.host && s.config.from && s.config.recipients.length > 0;

  function draw() {
    const c = s.config;
    testButton.disabled = !ready();
    testButton.title = ready() ? "" : "Set the mail server and at least one recipient first.";
    toggleButton.lastChild.textContent = c.enabled ? "Turn off" : "Turn on";
    toggleButton.className = c.enabled ? "btn" : "btn primary";
    toggleButton.disabled = !c.enabled && !ready();
    toggleButton.title = testButton.title;

    problem.replaceChildren(...[
      s.lastError && h("div", { class: "callout bad" }, h("b", null, "The last email couldn't be sent. "), s.lastError,
        s.waiting > 0 && ` ${plural(s.waiting, "email")} waiting; they're tried again for a day.`),
      !c.enabled && ready() && h("div", { class: "callout info" }, "Notifications are off. Send a test email, then turn them on."),
    ].filter(Boolean));

    server.replaceChildren(c.host
      ? props([
        ["Server", [h("code", null, `${c.host}:${c.port}`), h("span", { class: "sub" }, securityLabel[c.security] ?? c.security,
          c.tlsSkipVerify && c.security !== "none" && ", any certificate accepted")]],
        ["Signs in as", c.username
          ? [h("code", null, c.username), !c.hasPassword && h("span", { class: "sub" }, pill("No password saved", "warn"))]
          : "Nobody (a relay that doesn't ask)"],
        ["Sent from", c.from ? [c.fromName && `${c.fromName} `, h("code", null, c.from)] : pill("Not set", "warn")],
        ["Last email", s.lastSentAt ? time(s.lastSentAt) : h("span", { class: "muted" }, "None since the server started")],
      ])
      : empty("Not set up", "Enter the SMTP server of your mail provider, or a relay of your own.",
        canEdit && button("Set up", { kind: "primary", onclick: editServer })));

    alerts.replaceChildren(props([
      ["Notifications", c.enabled ? pill("On", "ok") : pill("Off", "plain")],
      ["Sent to", c.recipients.length ? c.recipients.map((r) => h("div", null, r)) : pill("Nobody yet", "warn")],
      ["About", kinds.filter(([key]) => c[key]).map(([, label]) => h("div", null, label))],
      ["After", c.delayMinutes
        ? [plural(c.delayMinutes, "minute"), h("span", { class: "sub" }, "A printer or station that recovers sooner sends nothing.")]
        : "Straight away"],
    ]));

    open.replaceChildren(table({
      rows: s.alerts,
      empty: empty("Nothing", c.enabled ? "No printer or station is in a state an email went out about." : "Notifications are off."),
      columns: [
        { label: "What", value: (a) => h("span", { class: "cell-strong" }, a.summary) },
        { label: "Since", value: (a) => dateTime(a.since) },
        { label: "", class: "num", value: (a) => {
          const [kind, id] = a.subject.split(":");
          return h("a", { href: `${kind}s/${encodeURIComponent(id)}` }, kind === "printer" ? "Printer" : "Station");
        } },
      ],
    }));
  }

  async function reload() {
    s = await api.get("server/notifications");
    if (ctx.current) draw();
  }

  function editServer() {
    const c = s.config;
    const port = input("port", { type: "number", min: 1, max: 65535, required: true, value: c.port });
    const security = select("security", [["starttls", "STARTTLS (usually port 587)"], ["tls", "TLS (usually port 465)"], ["none", "None (a local relay, port 25)"]], c.security);
    // Whoever picks another kind most likely wants its port too, unless they typed one of their own.
    security.addEventListener("change", () => {
      if (Object.values(usualPort).includes(Number(port.value))) port.value = usualPort[security.value];
    });
    formDialog({
      title: "Mail server",
      description: "The SMTP settings from your mail provider (Gmail, Fastmail, Amazon SES, Mailgun…) or a relay on your network. TapQueue only sends; it never reads mail.",
      wide: true,
      body: [
        h("div", { class: "field-row" },
          field("Server", input("host", { required: true, value: c.host, placeholder: "smtp.example.org" })),
          field("Port", port)),
        field("Encryption", security, "With STARTTLS or TLS the password never crosses the network readable."),
        checkField("Accept any certificate", checkbox("tlsSkipVerify", c.tlsSkipVerify), "Only for a relay of your own with a self-signed certificate."),
        field("Username", input("username", { value: c.username, placeholder: "alerts@example.org" }), "Leave empty for a relay that doesn't ask anyone to sign in."),
        field("Password", input("password", { type: "password", autocomplete: "new-password", placeholder: c.hasPassword ? "Saved; leave empty to keep it" : "" }),
          c.hasPassword
            ? "Enter it again if you change the server, port or encryption."
            : "Many providers want an app password made for TapQueue here, not the account's own. It's kept on the server and never shown again."),
        h("div", { class: "field-row" },
          field("Send from", input("from", { type: "email", required: true, value: c.from, placeholder: "alerts@example.org" }), "Most providers only take an address that belongs to the account."),
          field("Shown as", input("fromName", { value: c.fromName, placeholder: "TapQueue" }))),
      ],
      onSubmit: async (v) => {
        s = await api.patch("server/notifications", { ...v, port: Number(v.port) });
        draw();
        toast(s.config.recipients.length ? "Saved. Send a test email to check it." : "Saved. Add who gets the emails under Alerts.");
      },
    });
  }

  function editAlerts() {
    const c = s.config;
    formDialog({
      title: "Alerts",
      body: [
        field("Send to", h("textarea", { name: "recipients", rows: 3, placeholder: "it@example.org\nfrontdesk@example.org" }, c.recipients.join("\n")),
          "One address on each line. A mailing list or a shared inbox works well."),
        ...kinds.map(([key, label, hint]) => checkField(label, checkbox(key, c[key]), hint)),
        field("Wait (minutes)", input("delayMinutes", { type: "number", min: 0, max: 1440, required: true, value: c.delayMinutes }),
          "How long a printer or station must stay that way before an email goes out. 0 sends straight away."),
      ],
      onSubmit: async (v) => {
        s = await api.patch("server/notifications", {
          ...v,
          recipients: v.recipients.split(/[\s,;]+/).filter(Boolean),
          delayMinutes: Number(v.delayMinutes),
        });
        draw();
        toast("Saved");
      },
    });
  }

  async function toggle() {
    const on = !s.config.enabled;
    const result = await attempt(() => api.patch("server/notifications", { enabled: on }), on ? "Notifications are on" : "Notifications are off");
    if (result) { s = result; draw(); }
  }

  async function test() {
    testButton.disabled = true;
    toast("Sending…");
    const result = await attempt(() => api.post("server/notifications/test"));
    if (result) toast(result.message, result.success ? undefined : "bad");
    await reload();
  }

  draw();
  ctx.every(15000, reload);
}
