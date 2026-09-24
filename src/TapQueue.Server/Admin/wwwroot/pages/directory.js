import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, props, table, empty, time, dateTime, plural, field, input, select, checkbox, checkField,
  formDialog, confirm, attempt, toast, loading } from "../ui.js";

const kinds = {
  ou: { label: "OU", adds: "Every user anywhere under it." },
  group: { label: "Group", adds: "Every member, through nested groups too. It also becomes a TapQueue group." },
  user: { label: "User", adds: "Just this person." },
};

const outcomeTone = { succeeded: "ok", running: "accent", failed: "bad", stopped: "warn" };

const actionLabel = {
  "create-user": ["Add user", "ok"],
  "link-user": ["Link user", "accent"],
  "update-user": ["Update user", "accent"],
  "disable-user": ["Disable", "bad"],
  "enable-user": ["Re-enable", "ok"],
  "create-group": ["Add group", "ok"],
  "update-group": ["Update group", "accent"],
  "delete-group": ["Remove group", "bad"],
  members: ["Members", "accent"],
  badge: ["Card", "accent"],
};

export async function render(root, ctx) {
  let s = await api.get("directory");
  if (!ctx.current) return;

  const connection = h("div");
  const schedule = h("div");
  const signIn = h("div");
  const scope = h("div");
  const runs = h("div");
  const previewButton = button("Preview", { iconName: "search", onclick: () => run(true) });
  const syncButton = button("Sync now", { kind: "primary", iconName: "refresh", onclick: () => run(false) });

  root.append(
    pageHead("Active Directory", "Users and groups synced from AD. AD decides their names, whether they're enabled and who is in AD groups; what groups may use and page limits are set here.",
      previewButton, syncButton),
    h("div", { class: "grid two" },
      panel({ title: "Connection", actions: [button("Test", { small: true, onclick: test }), button("Edit", { small: true, onclick: editConnection })], body: connection }),
      panel({ title: "Daily sync", actions: button("Edit", { small: true, onclick: editSchedule }), body: schedule })),
    panel({ title: "Tray sign-in", description: "Who the TapQueue tray app on each PC signs in as.",
      actions: button("Edit", { small: true, onclick: editSignIn }), body: signIn }),
    panel({
      title: "Scope",
      description: "Who is synced. Users in none of these aren't in TapQueue, or are disabled if they were.",
      actions: [
        button("Add OU", { small: true, iconName: "plus", onclick: () => addScope("ou") }),
        button("Add group", { small: true, iconName: "plus", onclick: () => addScope("group") }),
        button("Add user", { small: true, iconName: "plus", onclick: () => addScope("user") }),
      ],
      flush: true,
      body: scope,
    }),
    panel({ title: "History", description: "Syncs and previews from the last 90 days.", flush: true, body: runs }));

  const configured = () => s.config.host && s.config.bindDn && s.config.hasPassword;

  function draw() {
    const c = s.config;
    previewButton.disabled = syncButton.disabled = s.running || !configured() || s.scope.length === 0;
    syncButton.title = previewButton.title = !configured() ? "Set up the connection first." : s.scope.length === 0 ? "Add something to the scope first." : "";

    connection.replaceChildren(c.host
      ? props([
        ["Domain controller", [h("code", null, `${c.host}:${c.port}`), h("span", { class: "sub" }, "LDAPS")]],
        ["Bind account", [h("code", null, c.bindDn || "—"), !c.hasPassword && h("span", { class: "sub" }, pill("No password saved", "warn"))]],
        ["Trusts", c.caCertificate ? "Its own CA certificate" : "The server's trusted CAs"],
        ["Cards", c.badgeAttribute ? [h("code", null, c.badgeAttribute), h("span", { class: "sub" }, "AD decides the card of anyone with a value in it.")] : "Managed in TapQueue"],
      ])
      : empty("Not set up", "Connect to a domain controller with a read-only account.", button("Set up", { kind: "primary", onclick: editConnection })));

    const last = s.runs.find((r) => !r.dryRun);
    schedule.replaceChildren(props([
      ["Daily sync", c.enabled ? pill(`On, at ${c.syncTime}`, "ok") : pill("Off", "plain")],
      ["Next", s.nextRunAt ? [dateTime(s.nextRunAt), !last && h("span", { class: "sub" }, "Only after the first sync is run here.")] : "—"],
      ["Last sync", last ? [pill(last.outcome, outcomeTone[last.outcome]), " ", time(last.startedAt), h("span", { class: "sub" }, last.error ?? last.summary)] : "Never"],
      ["Safety limit", `Stops before disabling more than ${c.maxDisablePercent}% of AD users`],
    ]));

    signIn.replaceChildren(props([
      ["Signs in as", c.clientSignIn === "domain"
        ? [pill("Domain account", "accent"), h("span", { class: "sub" }, "People choose Sign in as… in the tray and enter their AD name and password. It's remembered on that PC until they sign out.")]
        : [pill("The PC's user", "plain"), h("span", { class: "sub" }, "The person signed in to the PC, without asking. Sign in as… is greyed out.")]],
    ]));

    scope.replaceChildren(table({
      rows: s.scope,
      empty: empty("Nothing in the scope", "Add the OUs, groups or single users whose people should be in TapQueue."),
      columns: [
        { label: "Kind", value: (i) => pill(kinds[i.kind].label, "plain") },
        { label: "Name", value: (i) => [h("span", { class: "cell-strong" }, i.name), h("span", { class: "sub mono" }, i.dn)] },
        { label: "Brings in", value: (i) => h("span", { class: "muted" }, kinds[i.kind].adds) },
        { label: "", class: "num", value: (i) => button("Remove", { small: true, kind: "ghost danger", onclick: () => removeScope(i) }) },
      ],
    }));

    runs.replaceChildren(table({
      rows: s.runs,
      empty: empty("No syncs yet", "Preview a sync to see what it would do."),
      columns: [
        { label: "When", value: (r) => time(r.startedAt) },
        { label: "", value: (r) => [pill(r.outcome, outcomeTone[r.outcome]), r.dryRun && [" ", pill("Preview", "plain")]] },
        { label: "By", value: (r) => r.trigger === "schedule" ? "Schedule" : "Admin" },
        { label: "Result", value: (r) => [r.summary, r.error && h("span", { class: "sub" }, r.error)] },
      ],
    }));
  }

  async function reload() {
    s = await api.get("directory");
    if (ctx.current) draw();
  }

  function editConnection() {
    const c = s.config;
    formDialog({
      title: "Connection",
      description: "TapQueue connects over LDAPS (port 636) and only reads. Use an account that can read users and groups, nothing more.",
      wide: true,
      body: [
        h("div", { class: "field-row" },
          field("Domain controller", input("host", { required: true, value: c.host, placeholder: "dc01.example.org" }), "Its certificate must be issued for this name."),
          field("Port", input("port", { type: "number", min: 1, max: 65535, required: true, value: c.port }))),
        field("Bind account", input("bindDn", { required: true, value: c.bindDn, placeholder: "svc-tapqueue@example.org or CN=svc-tapqueue,OU=…" })),
        field("Password", input("password", { type: "password", required: !c.hasPassword, placeholder: c.hasPassword ? "Saved; leave empty to keep it" : "" })),
        field("CA certificate", h("textarea", { name: "caCertificate", rows: 5, class: "mono", placeholder: "-----BEGIN CERTIFICATE-----\n…\n-----END CERTIFICATE-----" }, c.caCertificate),
          "The CA that issued the domain controllers' certificates, as PEM. Leave empty if the server already trusts it."),
        field("Card number attribute", input("badgeAttribute", { value: c.badgeAttribute, placeholder: "e.g. employeeNumber" }),
          "Optional. If set, AD decides the card of everyone with a value in it; their other cards are removed."),
      ],
      onSubmit: async (v) => {
        s = await api.patch("directory/config", { ...v, port: Number(v.port) });
        draw();
        const result = await api.post("directory/test");
        toast(result.message, result.success ? undefined : "bad");
      },
    });
  }

  function editSchedule() {
    const c = s.config;
    formDialog({
      title: "Daily sync",
      body: [
        checkField("Sync every day", checkbox("enabled", c.enabled), "Runs once an admin has run the first sync here."),
        field("At", input("syncTime", { type: "time", required: true, value: c.syncTime }), "In the server's time zone."),
        field("Stop before disabling more than (%)", input("maxDisablePercent", { type: "number", min: 1, max: 100, required: true, value: c.maxDisablePercent }),
          "Of the AD users who are enabled now, in case AD or the scope is wrong. Syncs that disable fewer than 5 people always go ahead."),
      ],
      onSubmit: async (v) => {
        s = await api.patch("directory/config", { enabled: v.enabled, syncTime: v.syncTime, maxDisablePercent: Number(v.maxDisablePercent) });
        draw();
        toast("Saved");
      },
    });
  }

  function editSignIn() {
    formDialog({
      title: "Tray sign-in",
      body: [
        field("Sign in as", select("clientSignIn", [["pc", "The person signed in to the PC"], ["domain", "A domain account, entered in the tray"]], s.config.clientSignIn),
          "With a domain account, only users synced from AD can sign in, and jobs printed before someone signs in on a PC aren't anyone's. Passwords are checked against AD and never stored."),
      ],
      onSubmit: async (v) => {
        s = await api.patch("directory/config", { clientSignIn: v.clientSignIn });
        draw();
        toast("Saved");
      },
    });
  }

  async function test() {
    const result = await attempt(() => api.post("directory/test"));
    if (result) toast(result.message, result.success ? undefined : "bad");
  }

  function addScope(kind) {
    const results = h("div", { class: "picker" }, h("p", { class: "muted" }, "Type part of a name and search."));
    const query = input("q", { type: "search", placeholder: kind === "ou" ? "e.g. Staff (empty lists them all)" : "Name or username" });
    let dialog;
    async function search() {
      results.replaceChildren(loading());
      try {
        const found = await api.get(`directory/search?kind=${kind}&q=${enc(query.value.trim())}`);
        results.replaceChildren(found.length
          ? h("div", { class: "panel" }, table({ rows: found, columns: [
            { label: "Name", value: (o) => [h("span", { class: "cell-strong" }, o.displayName || o.name, o.username && o.username !== o.name && h("span", { class: "muted" }, ` (${o.username})`)),
              h("span", { class: "sub mono" }, o.dn)] },
            { label: "", class: "num", value: (o) => o.inScope ? pill("In scope", "plain") : button("Add", { small: true, kind: "primary", onclick: () => add({ guid: o.guid }) }) },
          ] }))
          : h("p", { class: "muted" }, "Nothing found."));
      } catch (err) {
        results.replaceChildren(h("div", { class: "form-error" }, err.message));
      }
    }
    async function add(body) {
      const item = await attempt(() => api.post("directory/scope", body), "Added to the scope");
      if (!item) return;
      dialog.close();
      await reload();
    }
    query.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); search(); } });
    dialog = formDialog({
      title: `Add ${kinds[kind].label === "OU" ? "an OU" : `a ${kinds[kind].label.toLowerCase()}`}`,
      description: kinds[kind].adds,
      wide: true,
      submitLabel: "Add by DN",
      body: [
        h("div", { class: "field-row" }, field("Search Active Directory", query), h("div", { class: "field" }, h("span", null, " "), button("Search", { onclick: search }))),
        results,
        field("Or its distinguished name", input("dn", { placeholder: "OU=Staff,DC=example,DC=org" })),
      ],
      onSubmit: async (v) => {
        if (!v.dn) throw new Error("Search and pick one, or type its DN.");
        await api.post("directory/scope", { dn: v.dn });
        toast("Added to the scope");
        await reload();
      },
    });
    if (kind === "ou") search();
  }

  async function removeScope(item) {
    if (!await confirm({
      title: `Remove ${item.name} from the scope?`,
      message: item.kind === "group"
        ? "At the next sync the TapQueue group goes, with what it allowed, and anyone only it brought in is disabled."
        : "At the next sync, anyone only it brought in is disabled. Nobody is deleted.",
      confirmLabel: "Remove",
    })) return;
    if (await attempt(() => api.del(`directory/scope/${item.id}`), "Removed from the scope") !== undefined) await reload();
  }

  async function run(dryRun, force = false) {
    previewButton.disabled = syncButton.disabled = true;
    toast(dryRun ? "Working out what would change…" : "Syncing…");
    const result = await attempt(() => api.post("directory/sync", { dryRun, force }));
    await reload();
    if (result) showResult(result, dryRun);
  }

  function showResult(r, dryRun) {
    const title = r.outcome === "failed" ? "Sync failed"
      : r.outcome === "stopped" ? "Sync stopped"
      : dryRun ? "What a sync would do" : "Sync done";
    const canApply = r.outcome === "stopped" || (dryRun && r.outcome === "succeeded" && r.changes.length > 0);
    formDialog({
      title,
      description: r.outcome === "failed" ? "Nothing was changed." : `${plural(r.users, "user")} and ${plural(r.groups, "group")} in the scope. ${r.summary}`,
      wide: true,
      submitLabel: r.outcome === "stopped" ? "Sync anyway" : canApply ? "Sync now" : "Close",
      danger: r.outcome === "stopped",
      body: [
        r.error && h("div", { class: r.outcome === "stopped" ? "callout" : "form-error" }, r.error),
        r.warnings.length > 0 && h("div", { class: "callout" }, h("b", null, plural(r.warnings.length, "warning")), h("ul", { style: "margin:4px 0 0; padding-left:18px" }, r.warnings.map((w) => h("li", null, w)))),
        r.changes.length > 0 && h("div", { class: "panel" }, table({
          rows: r.changes,
          search: (c) => `${c.subject} ${c.description}`,
          columns: [
            { label: "", value: (c) => pill(...(actionLabel[c.action] ?? [c.action, "plain"])) },
            { label: "Change", value: (c) => c.description },
          ],
        })),
        r.outcome === "succeeded" && r.changes.length === 0 && h("p", { class: "muted", style: "margin:0" }, "Everything is already as it is in Active Directory."),
      ],
      onSubmit: async () => {
        if (canApply) setTimeout(() => run(false, r.outcome === "stopped"));
      },
    });
  }

  draw();
  ctx.every(15000, reload);
}
