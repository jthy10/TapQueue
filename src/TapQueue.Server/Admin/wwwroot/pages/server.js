import { api } from "../api.js";
import { h, pageHead, panel, button, pill, props, time, dateTime, duration, plural, field, input, select, formDialog, confirm, attempt, toast } from "../ui.js";

const MAX_LINES = 2000;

export async function render(root, ctx) {
  let s = await api.get("server");
  // Null for admins without a role in the Server area.
  let u = await api.maybe("server/update", null);
  if (!ctx.current) return;

  const status = h("div");
  const settings = h("div");
  const updates = h("div");
  const checkButton = button("Check for updates", { small: true, iconName: "refresh", onclick: () => checkForUpdates(true) });
  const restartButton = button("Restart", { iconName: "refresh", onclick: restart });
  root.append(pageHead("Server", "The TapQueue server this console is running on.",
    h("a", { class: "btn", href: "/healthz", target: "_blank" }, "Health check"), restartButton),
  h("div", { class: "grid two" },
    panel({ title: "Status", body: status }),
    panel({ title: "Settings", actions: button("Edit", { small: true, onclick: edit }), body: settings })),
  u && panel({ title: "Updates", description: "New TapQueue server releases on GitHub.", actions: checkButton, body: updates }),
  liveLog(ctx));

  function draw() {
    restartButton.disabled = !s.canRestart;
    restartButton.title = s.canRestart ? "" : "Only when the server runs as the tapqueue-server service; nothing else would start it again.";
    status.replaceChildren(props([
      ["Version", h("span", { class: "mono" }, s.version)],
      ["Running since", [dateTime(s.startedAt), h("span", { class: "sub" }, `Up ${duration(s.startedAt)}`)]],
      ["Listening on", h("code", null, s.listen)],
      ["Data", h("code", null, s.dataDir)],
      ["Database schema", `Version ${s.schemaVersion}`],
      ["Held jobs", s.heldJobs],
    ]));
    const source = (key) => h("span", { class: "sub" }, s.changedSettings?.includes(key) ? "Set here." : "From server.toml.");
    settings.replaceChildren(props([
      ["Jobs kept for", [plural(s.holdHours, "hour"), h("span", { class: "sub" }, "Held jobs nobody releases are deleted after this."), source("holdHours")]],
      ["Session timeout", [plural(s.sessionTimeoutMinutes, "minute"), h("span", { class: "sub" }, "A client that stops checking in is signed out after this."), source("sessionTimeoutMinutes")]],
      ["Over a page limit", [s.quotaOverrun === "deny" ? "Only jobs that fit" : "Finish the job",
        h("span", { class: "sub" }, s.quotaOverrun === "deny"
          ? "A job prints only if it fits in the pages someone has left."
          : "A job prints in full if someone is under their limit when it starts, even if it takes them over.")]],
      ["Jobs without a PC key", [s.addressMatching === "off" ? "Refused" : "Matched by address",
        h("span", { class: "sub" }, s.addressMatching === "off"
          ? "Only jobs from PCs whose TapQueue client has a key (0.8 on) go to someone."
          : "Jobs from clients older than 0.8 go to whoever is signed in at that address. Turn this off once every PC under Workstations has a key.")]],
      ["Client sign-in", s.authMode === "dev"
        ? [pill("Dev", "bad"), h("span", { class: "sub" }, "Anyone can sign in as any username, and this console is open. Set auth.mode in server.toml and restart to change it.")]
        : [pill("Tokens", "ok"), h("span", { class: "sub" }, "Clients need the token from Users.")]],
    ]));
  }

  function drawUpdates() {
    const latest = u.latest;
    const run = u.lastRun;
    const runTone = { done: "ok", failed: "bad" }[run?.state] ?? "warn";
    const runLabel = { requested: "Starting", running: "Upgrading", done: "Done", failed: "Failed" }[run?.state];
    updates.replaceChildren(...[
      u.checkError ? h("div", { class: "callout bad" }, u.checkError) : null,
      u.updateAvailable && !u.canApply ? h("div", { class: "callout" }, u.cannotApplyReason) : null,
      props([
        ["Running", h("span", { class: "mono" }, u.current)],
        ["Newest release", latest
          ? [h("a", { href: latest.url, target: "_blank", rel: "noopener" }, latest.version), " ",
            u.updateAvailable ? pill("Update available", "accent") : pill("Up to date", "ok"),
            latest.publishedAt && h("span", { class: "sub" }, `Released ${dateTime(latest.publishedAt)}`)]
          : h("span", { class: "muted" }, u.checkedAt ? "Unknown" : "Not checked yet")],
        ["Last checked", u.checkedAt ? time(u.checkedAt) : "Not since the server started"],
        u.scheduled && ["Scheduled", [`${u.scheduled.version} at ${dateTime(u.scheduled.at)}`,
          u.scheduled.by && h("span", { class: "sub" }, `By ${u.scheduled.by}`), " ",
          button("Call off", { small: true, onclick: cancelScheduled })]],
        run && ["Last upgrade", [pill(runLabel, runTone), ` ${run.version} `,
          run.at && h("span", { class: "sub" }, `${run.message} (${dateTime(run.at)})`)]],
      ]),
      u.updateAvailable && u.canApply && !u.scheduled && !["requested", "running"].includes(run?.state)
        ? button(`Update to ${latest.version}…`, { kind: "primary", onclick: offerUpdate })
        : null,
    ].filter(Boolean));
  }

  async function checkForUpdates(offer) {
    checkButton.disabled = true;
    const checked = await attempt(() => api.get("server/update?refresh=true"));
    checkButton.disabled = false;
    if (!checked || !ctx.current) return;
    u = checked;
    drawUpdates();
    if (u.checkError) return;
    if (!u.updateAvailable) return toast(`This server runs the newest release (${u.current})`);
    if (offer && u.canApply && !u.scheduled) offerUpdate();
  }

  function offerUpdate() {
    const version = u.latest.version;
    // datetime-local wants local time without a zone; default to tonight at 2:00.
    const tonight = new Date();
    tonight.setDate(tonight.getDate() + (tonight.getHours() >= 2 ? 1 : 0));
    tonight.setHours(2, 0, 0, 0);
    const local = (d) => new Date(d - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
    const when = select("when", [["now", "Now"], ["later", "Later, at a time I pick"]], "now");
    const at = input("at", { type: "datetime-local", value: local(tonight), min: local(new Date()) });
    const atField = field("Upgrade at", at, "This browser's local time. The server starts it within a minute of then.");
    atField.hidden = true;
    when.onchange = () => { atField.hidden = when.value !== "later"; };
    formDialog({
      title: `Update to TapQueue server ${version}?`,
      description: `This server runs ${u.current}.`,
      submitLabel: "Update",
      body: [
        h("p", { style: "margin-top:0" }, "The server downloads the release from GitHub, checks it against its SHA256SUMS and installs it. ",
          "Printing, releasing and this console stop for up to a minute while it restarts. Held jobs, settings and server.toml are kept. ",
          h("a", { href: u.latest.url, target: "_blank", rel: "noopener" }, "Read the release notes"),
          " first: before 1.0, a new minor version can need manual steps."),
        s.heldJobs ? h("div", { class: "callout info" }, `${plural(s.heldJobs, "job")} held now; they stay held.`) : null,
        field("When", when),
        atField,
      ],
      onSubmit: async (v) => {
        const later = v.when === "later";
        if (later && !v.at) throw new Error("Pick a time to upgrade at.");
        const body = { version, at: later ? new Date(v.at).toISOString() : null };
        u = await api.post("server/update", body);
        drawUpdates();
        if (later) toast(`Upgrade to ${version} scheduled for ${dateTime(body.at)}`);
        else followUpgrade(version);
      },
    });
  }

  async function cancelScheduled() {
    const updated = await attempt(() => api.del("server/update"), "Scheduled upgrade called off");
    if (updated && ctx.current) { u = updated; drawUpdates(); }
  }

  /** Waits for the server to come back on the new version, or for the updater to say it failed. */
  async function followUpgrade(version) {
    toast(`Upgrading to ${version}…`);
    restartButton.disabled = true;
    for (let i = 0; i < 300 && ctx.current; i++) {
      await new Promise((r) => setTimeout(r, 2000));
      try {
        const now = await api.get("server/update");
        u = now;
        drawUpdates();
        if (now.current === version) {
          s = await api.get("server");
          draw();
          return toast(`Updated to TapQueue server ${version}`);
        }
        if (now.lastRun?.state === "failed") {
          draw();
          return toast(`The upgrade didn't work: ${now.lastRun.message}`, "bad");
        }
      } catch { /* restarting */ }
    }
    if (ctx.current) toast("The server hasn't come back on the new version after 10 minutes. Check it with: journalctl -u tapqueue-update -n 100", "bad");
  }

  function edit() {
    const fromFile = "Use server.toml";
    const changed = (key) => s.changedSettings?.includes(key);
    formDialog({
      title: "Server settings",
      description: "These apply straight away, without a restart.",
      body: [
        field("Keep held jobs for (hours)", h("div", { class: "field-row" },
          input("holdHours", { type: "number", min: 1, max: 720, required: true, value: s.holdHours }),
          select("holdHoursSource", [["here", "Set here"], ["file", fromFile]], changed("holdHours") ? "here" : "file")),
          "1 to 720. Jobs already held keep the time they were given."),
        field("Session timeout (minutes)", h("div", { class: "field-row" },
          input("sessionTimeoutMinutes", { type: "number", min: 2, max: 1440, required: true, value: s.sessionTimeoutMinutes }),
          select("sessionTimeoutSource", [["here", "Set here"], ["file", fromFile]], changed("sessionTimeoutMinutes") ? "here" : "file")),
          "2 to 1440. Clients check in every minute."),
        field("When a job would go over someone's page limit", select("quotaOverrun", [
          ["allow", "Print it if they're under the limit when it starts"],
          ["deny", "Only print jobs that fit in what's left"],
        ], s.quotaOverrun)),
        field("Jobs that arrive without a PC key", select("addressMatching", [
          ["on", "Match them to whoever is signed in at that address"],
          ["off", "Don't match them to anyone"],
        ], s.addressMatching),
          "Clients from 0.8 on give each PC a key, so its jobs are matched by PC and PC user, not address."),
      ],
      onSubmit: async (v) => {
        const body = { reset: [] };
        if (v.holdHoursSource === "file") body.reset.push("holdHours"); else body.holdHours = Number(v.holdHours);
        if (v.sessionTimeoutSource === "file") body.reset.push("sessionTimeoutMinutes"); else body.sessionTimeoutMinutes = Number(v.sessionTimeoutMinutes);
        if (v.quotaOverrun !== s.quotaOverrun) body.quotaOverrun = v.quotaOverrun;
        if (v.addressMatching !== s.addressMatching) body.addressMatching = v.addressMatching;
        s = await api.patch("server/settings", body);
        draw();
        toast("Settings saved");
      },
    });
  }

  async function restart() {
    if (!await confirm({
      title: "Restart the server?",
      message: `Printing, releasing and this console stop for a few seconds. Held jobs are kept.${s.heldJobs ? ` (${plural(s.heldJobs, "job")} held now.)` : ""}`,
      confirmLabel: "Restart",
    })) return;
    if (await attempt(() => api.post("server/restart")) === undefined) return;
    const before = s.startedAt;
    restartButton.disabled = true;
    toast("Restarting…");
    for (let i = 0; i < 60 && ctx.current; i++) {
      await new Promise((r) => setTimeout(r, 1000));
      try {
        const now = await api.get("server");
        if (now.startedAt !== before) {
          s = now;
          draw();
          toast(`Server is back (${s.version})`);
          return;
        }
      } catch { /* still down */ }
    }
    if (ctx.current) toast("The server hasn't come back after a minute. Check it with: systemctl status tapqueue-server", "bad");
  }

  draw();
  if (u) drawUpdates();
  ctx.every(30000, async () => {
    s = await api.get("server");
    if (u) u = await api.get("server/update");
    if (ctx.current) { draw(); if (u) drawUpdates(); }
  });
}

/** The server's log as it happens, from a server-sent event stream that picks up where it left off after a reconnect. */
function liveLog(ctx) {
  let paused = false, atBottom = true, lastId = 0, filter = "", level = "all", source = null;
  const lines = [];
  const view = h("div", { class: "log", role: "log", "aria-live": "off", onscroll: () => {
    atBottom = view.scrollTop + view.clientHeight >= view.scrollHeight - 24;
  } });
  const state = h("span", { class: "log-state" }, "Connecting…");
  const pauseButton = button("Pause", { small: true, onclick: () => {
    paused = !paused;
    pauseButton.lastChild.textContent = paused ? "Resume" : "Pause";
    if (!paused) redraw();
  } });

  const matches = (l) => (level === "all" || l.level === "warning" || l.level === "error")
    && (!filter || `${l.category} ${l.message}`.toLowerCase().includes(filter));

  const row = (l) => l.mark
    ? h("div", { class: "log-mark" }, l.mark)
    : h("div", { class: `log-line ${l.level}` },
      h("span", { class: "log-time", title: dateTime(l.at) }, new Date(l.at).toLocaleTimeString()),
      h("span", { class: "log-level" }, l.level),
      h("span", { class: "log-cat" }, l.category),
      h("span", null, l.message));

  function redraw() {
    view.replaceChildren(...lines.filter((l) => l.mark || matches(l)).map(row));
    view.scrollTop = view.scrollHeight;
    atBottom = true;
  }

  function add(l) {
    if (l.id <= lastId) lines.push({ mark: `Server restarted at ${new Date(l.at).toLocaleTimeString()}` });
    lastId = l.id;
    lines.push(l);
    if (lines.length > MAX_LINES) lines.splice(0, lines.length - MAX_LINES);
    if (paused) return;
    if (lines.at(-2)?.mark) view.append(row(lines.at(-2)));
    if (!matches(l)) return;
    view.append(row(l));
    while (view.childElementCount > MAX_LINES) view.firstChild.remove();
    if (atBottom) view.scrollTop = view.scrollHeight;
  }

  function connect() {
    source = new EventSource("/api/v1/admin/server/log/stream");
    source.addEventListener("log", (e) => {
      if (!ctx.current) return source.close();
      add(JSON.parse(e.data));
    });
    source.onopen = () => { state.textContent = "Live"; };
    source.onerror = () => { state.textContent = "Reconnecting…"; };
  }

  const levelSelect = select("level", [["all", "Everything"], ["problems", "Warnings and errors"]], "all");
  levelSelect.onchange = () => { level = levelSelect.value; redraw(); };

  // Pages have no unmount hook; stop streaming once the admin has moved on.
  const watch = setInterval(() => { if (!ctx.current) { source?.close(); clearInterval(watch); } }, 2000);
  connect();

  return panel({
    title: "Live log",
    description: "The last 2000 lines since the server started. The full log is in journalctl -u tapqueue-server.",
    flush: true,
    body: h("div", null,
      h("div", { class: "log-toolbar" },
        h("input", { type: "search", placeholder: "Filter", "aria-label": "Filter log", oninput: (e) => { filter = e.target.value.trim().toLowerCase(); redraw(); } }),
        levelSelect,
        pauseButton,
        button("Clear", { small: true, onclick: () => { lines.length = 0; view.replaceChildren(); } }),
        state),
      view),
  });
}
