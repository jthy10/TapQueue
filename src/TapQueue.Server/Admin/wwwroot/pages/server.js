import { api } from "../api.js";
import { h, pageHead, panel, button, pill, props, dateTime, duration, plural, field, input, select, formDialog, confirm, attempt, toast } from "../ui.js";

const MAX_LINES = 2000;

export async function render(root, ctx) {
  let s = await api.get("server");
  if (!ctx.current) return;

  const status = h("div");
  const settings = h("div");
  const restartButton = button("Restart", { iconName: "refresh", onclick: restart });
  root.append(pageHead("Server", "The TapQueue server this console is running on.",
    h("a", { class: "btn", href: "/healthz", target: "_blank" }, "Health check"), restartButton),
  h("div", { class: "grid two" },
    panel({ title: "Status", body: status }),
    panel({ title: "Settings", actions: button("Edit", { small: true, onclick: edit }), body: settings })),
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
      ["Client sign-in", s.authMode === "dev"
        ? [pill("Dev", "bad"), h("span", { class: "sub" }, "Anyone can sign in as any username, and this console is open. Set auth.mode in server.toml and restart to change it.")]
        : [pill("Tokens", "ok"), h("span", { class: "sub" }, "Clients need the token from Users.")]],
    ]));
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
      ],
      onSubmit: async (v) => {
        const body = { reset: [] };
        if (v.holdHoursSource === "file") body.reset.push("holdHours"); else body.holdHours = Number(v.holdHours);
        if (v.sessionTimeoutSource === "file") body.reset.push("sessionTimeoutMinutes"); else body.sessionTimeoutMinutes = Number(v.sessionTimeoutMinutes);
        if (v.quotaOverrun !== s.quotaOverrun) body.quotaOverrun = v.quotaOverrun;
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
  ctx.every(30000, async () => { s = await api.get("server"); if (ctx.current) draw(); });
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
