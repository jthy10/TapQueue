import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, time, dateTime, table, empty, drawer, props, sectionTitle, field, input,
  select, checkList, formDialog, confirm, secret, attempt, toast, loading, plural } from "../ui.js";
import { enrollCard, editCard, removeCard } from "./cards.js";
import { jobStatus } from "./jobs.js";
import { activityList } from "./activity.js";
import { quotaText, quotaField, saveQuota, usageMeter } from "./quota.js";

export async function render(root, ctx) {
  let data;
  const list = h("div", null, loading());
  let selection = new Set();
  const bulkBar = h("div", { class: "bulk-bar", hidden: true });
  root.append(pageHead("Users", "People who print. Each one signs in on their PC with the TapQueue client and releases jobs with a card.",
    button("Import CSV", { onclick: importCsv }),
    h("a", { class: "btn", href: "/api/v1/admin/users/export", download: "" }, "Export CSV"),
    button("Add user", { kind: "primary", iconName: "plus", onclick: addUser })),
  panel({ flush: true, body: [bulkBar, list] }));

  function drawBulkBar() {
    bulkBar.hidden = selection.size === 0;
    const run = (action, extra) => attempt(() => bulk(action, extra));
    bulkBar.replaceChildren(
      h("b", null, `${selection.size} selected`),
      data.groups.length > 0 && button("Add to group…", { small: true, onclick: () => pickGroup("add-to-group") }),
      data.groups.length > 0 && button("Remove from group…", { small: true, onclick: () => pickGroup("remove-from-group") }),
      button("Disable", { small: true, onclick: () => run("disable") }),
      button("Enable", { small: true, onclick: () => run("enable") }),
      button("Delete", { small: true, kind: "danger", onclick: () => run("delete") }));
  }

  function pickGroup(action) {
    formDialog({
      title: action === "add-to-group" ? `Add ${plural(selection.size, "user")} to a group` : `Remove ${plural(selection.size, "user")} from a group`,
      submitLabel: action === "add-to-group" ? "Add" : "Remove",
      body: field("Group", select("groupId", data.groups.map((g) => [g.id, g.name]))),
      onSubmit: ({ groupId }) => bulk(action, { groupId }, true),
    });
  }

  async function bulk(action, extra = {}, confirmed = false) {
    const who = plural(selection.size, "user");
    if (!confirmed && action !== "enable" && !await confirm({
      title: action === "delete" ? `Delete ${who}?` : `Disable ${who}?`,
      message: action === "delete"
        ? "Their held jobs are canceled and their cards unlinked. Job history keeps their names."
        : "They're signed out and can't sign in, print or release until re-enabled.",
      confirmLabel: action === "delete" ? "Delete" : "Disable",
    })) return;
    const result = await api.post("users/bulk", { usernames: [...selection], action, ...extra });
    toast(result.errors.length ? `${plural(result.changed, "user")} changed. ${result.errors.join(" ")}` : `${plural(result.changed, "user")} changed`, result.errors.length ? "bad" : undefined);
    selection = new Set();
    await load();
  }

  function importCsv() {
    formDialog({
      title: "Import users from CSV",
      description: "You'll see what would change before anything does.",
      submitLabel: "Preview",
      body: [
        field("CSV file", input("file", { type: "file", accept: ".csv,text/csv", required: true })),
        h("div", { class: "callout info" }, "Columns: username (required), display_name, groups (ids separated by ;), card, disabled. ",
          "The groups column replaces each person's groups. Export first to get a file in the right shape."),
      ],
      onSubmit: async ({ file }) => {
        if (!file) throw new Error("Choose a CSV file.");
        const preview = await api.upload("users/import", file);
        reviewImport(file, preview);
      },
    });
  }

  function reviewImport(file, preview) {
    const changing = preview.creates + preview.updates;
    formDialog({
      title: "Review import",
      wide: true,
      submitLabel: changing ? `Apply ${plural(changing, "change")}` : "Nothing to apply",
      body: importResult(preview),
      onSubmit: async () => {
        if (!changing) return;
        const result = await api.upload("users/import?apply=true", file);
        await load();
        return importResult(result);
      },
    });
  }

  function importResult(r) {
    const tone = { create: "ok", update: "accent", error: "bad" };
    return h("div", null,
      h("div", { class: "import-summary" },
        pill(`${r.creates} to create`, "ok"), pill(`${r.updates} to update`, "accent"), r.errors > 0 && pill(`${r.errors} with problems`, "bad"),
        r.applied && pill("Applied", "ok")),
      h("div", { class: "import-rows" }, h("table", null, h("tbody", null, r.rows.map((row) => h("tr", null,
        h("td", { class: "muted nowrap" }, `Row ${row.row}`),
        h("td", { class: "cell-strong" }, row.username || "—"),
        h("td", null, pill(row.action, tone[row.action])),
        h("td", null, row.error ?? (row.changes.join(", ") || "no changes"))))))));
  }

  async function load() {
    const [users, badges, held, clients, server, groups, quotas] = await Promise.all([
      api.get("users"), api.get("badges"), api.get("jobs?status=held"), api.get("clients"), api.get("server"), api.get("groups"), api.get("quotas"),
    ]);
    if (!ctx.current) return;
    const by = (rows, key) => rows.reduce((m, r) => m.set(r[key], [...(m.get(r[key]) ?? []), r]), new Map());
    data = { users, server, groups, badges: by(badges, "username"), held: by(held, "owner"), clients: by(clients, "username"),
      quotas: new Map(quotas.map((q) => [q.username, q.applies])) };

    drawBulkBar();
    list.replaceChildren(table({
      rows: users,
      selectable: { key: (u) => u.username, onChange: (picked) => { selection = picked; drawBulkBar(); } },
      search: (u) => `${u.username} ${u.displayName}`,
      onRowClick: (u) => open(u.username),
      empty: empty("No users yet", server.authMode === "dev"
        ? "In dev mode, anyone who signs in with the client is added automatically."
        : "Add people here, then give them their client token.", button("Add user", { kind: "primary", onclick: addUser })),
      columns: [
        { label: "Name", value: (u) => [h("span", { class: "cell-strong" }, u.displayName, " ", u.disabledAt && pill("Disabled", "bad"), " ", u.source === "ad" && pill("AD", "plain")), h("span", { class: "sub" }, u.username)] },
        { label: "Groups", value: (u) => u.groups.length ? u.groups.map(groupName).join(", ") : h("span", { class: "muted" }, "None (can use everything)") },
        { label: "Cards", value: (u) => count(data.badges.get(u.username), "card", "No card") },
        { label: "Pages", value: (u) => pagesLeft(data.quotas.get(u.username)) },
        { label: "Held jobs", class: "num", value: (u) => data.held.get(u.username)?.length ?? 0 },
        { label: "Signed in", value: (u) => {
          const sessions = data.clients.get(u.username);
          return sessions ? pill(sessions.map((s) => s.hostname ?? s.remoteIp).join(", "), "ok") : h("span", { class: "muted" }, "No");
        } },
      ],
    }));
  }

  function disabledWhy(user) {
    if (user.disabledBy !== "directory") return "";
    return { disabled: " because they're disabled in Active Directory", expired: " because their account expired in Active Directory",
      missing: " because they're no longer in the Active Directory sync's scope" }[user.directoryState] ?? " by Active Directory";
  }

  const groupName = (id) => data.groups.find((g) => g.id.toLowerCase() === id.toLowerCase())?.name ?? id;

  /** The most generous limit that applies, since that's the one that decides. */
  function pagesLeft(applies) {
    if (!applies?.length) return h("span", { class: "muted" }, "No limit");
    const best = applies.reduce((a, b) => (b.remaining > a.remaining ? b : a));
    return [h("span", { class: "nowrap" }, `${best.used} of ${best.pages}`), h("span", { class: "sub" }, best.remaining ? `${best.remaining} left` : "None left")];
  }

  function count(rows, noun, none) {
    return rows?.length ? plural(rows.length, noun) : h("span", { class: "muted" }, none);
  }

  async function open(username) {
    ctx.setId(username);
    const user = data.users.find((u) => u.username.toLowerCase() === username.toLowerCase());
    if (!user) return ctx.setId(null);
    const [badges, jobs, events, quota] = await Promise.all([
      api.get(`badges?username=${enc(user.username)}`), api.get("jobs"), api.get(`events?subject=${enc(`user:${user.username}`)}&limit=15`),
      api.get(`users/${enc(user.username)}/quota`),
    ]);
    const theirJobs = jobs.filter((j) => j.owner === user.username).slice(0, 10);
    const sessions = data.clients.get(user.username) ?? [];
    const refresh = async () => { await load(); open(user.username); };
    const fromAd = user.source === "ad";
    // AD disabled them, so only AD can enable them; an AD user comes back at the next sync unless they left the scope.
    const adDisabled = user.disabledBy === "directory";
    const canDelete = !fromAd || user.directoryState === "missing";

    drawer({
      title: user.displayName,
      subtitle: `${user.username} · user #${user.id}`,
      onClose: () => ctx.setId(null),
      body: [
        user.disabledAt && h("div", { class: "callout" }, `Disabled since ${dateTime(user.disabledAt)}${disabledWhy(user)}. They can't sign in, print or release; their held jobs are kept until they expire.`),
        fromAd && h("div", { class: "callout info" }, "Synced from Active Directory, which decides their name, whether they're enabled and their AD groups. ",
          h("a", { href: "directory" }, "Sync settings")),
        props([
          ["Status", user.disabledAt ? pill("Disabled", "bad") : pill("Active", "ok")],
          ["Added", dateTime(user.createdAt)],
          ["Comes from", fromAd ? "Active Directory" : "TapQueue"],
          ["Groups", h("div", null,
            user.groups.length
              ? h("div", { class: "chips" }, user.groups.map((g) => h("a", { class: "pill plain", href: `groups/${enc(g)}` }, groupName(g))))
              : h("span", { class: "muted" }, "None, so they can print everywhere."),
            h("div", { style: "margin-top:8px" }, button("Change groups", { small: true, onclick: () => changeGroups(user) })))],
        ]),
        sectionTitle("Pages"),
        quota.applies.length
          ? [quota.applies.map((u) => usageMeter(u, groupName)),
            quota.applies.length > 1 && h("p", { class: "muted", style: "margin:10px 0 0" }, "A job prints if any of these limits allows it.")]
          : h("p", { class: "muted", style: "margin:0" }, "No page limit."),
        h("div", { style: "margin-top:10px" }, button("Page limit", { small: true, onclick: () => changeQuota(user) })),

        sectionTitle("Cards"),
        badges.length
          ? h("div", { class: "panel" }, table({ rows: badges, columns: [
            { label: "Card", value: (b) => [h("code", null, b.cardHint), b.label && h("span", { class: "sub" }, b.label)] },
            { label: "Last used", value: (b) => time(b.lastUsedAt) },
            { label: "", class: "num nowrap", value: (b) => [
              button("Edit", { small: true, kind: "ghost", onclick: () => editCard(b, refresh) }),
              button("Remove", { small: true, kind: "ghost danger", onclick: () => removeCard(b, refresh) }),
            ] },
          ] }))
          : h("p", { class: "muted", style: "margin:0" }, "No card yet, so they can't release jobs at a station."),
        h("div", { style: "margin-top:10px" }, button("Enroll card", { small: true, iconName: "plus", onclick: () => enrollCard({ username: user.username, onDone: refresh }) })),

        sectionTitle("Signed in on"),
        sessions.length
          ? props(sessions.map((s) => [s.hostname ?? s.remoteIp, h("span", null, `${s.windowsUser ?? "?"} · client ${s.clientVersion ?? "unknown"}`, h("span", { class: "sub" }, `${s.remoteIp}, seen ${time(s.lastSeenAt).textContent}`))]))
          : h("p", { class: "muted", style: "margin:0" }, "Not signed in anywhere. Jobs they print can't be matched to them."),

        sectionTitle("Recent jobs"),
        theirJobs.length
          ? h("div", { class: "panel" }, table({ rows: theirJobs, onRowClick: (j) => ctx.navigate(`jobs/${j.id}`), columns: [
            { label: "Document", value: (j) => j.name },
            { label: "Status", value: jobStatus },
            { label: "When", value: (j) => time(j.submittedAt) },
          ] }))
          : h("p", { class: "muted", style: "margin:0" }, "Nothing printed yet."),

        sectionTitle("History"),
        activityList(events, { emptyText: "No activity yet." }),
      ],
      footer: [
        h("div", { class: "left" }, canDelete && button("Delete", { kind: "ghost danger", onclick: () => remove(user) })),
        user.disabledAt
          ? !adDisabled && button("Enable", { onclick: () => setDisabled(user, false) })
          : button("Disable", { kind: "danger", onclick: () => setDisabled(user, true) }),
        button("Reset token", { iconName: "key", onclick: () => resetToken(user) }),
        !fromAd && button("Rename", { kind: "primary", onclick: () => rename(user) }),
      ],
    });
  }

  function changeGroups(user) {
    // Active Directory sets who is in its groups, so only TapQueue's own groups can be changed here.
    const local = data.groups.filter((g) => g.source !== "ad");
    const isLocal = (id) => local.some((g) => g.id.toLowerCase() === id.toLowerCase());
    if (local.length === 0) {
      formDialog({ title: "Change groups", body: h("p", { style: "margin:0" }, data.groups.length
        ? "All groups come from Active Directory; change their members there, or add a TapQueue group on the Groups page."
        : "There are no groups yet. Add one on the Groups page."), submitLabel: "OK", onSubmit: () => {} });
      return;
    }
    formDialog({
      title: `${user.displayName}'s groups`,
      description: "They may use whatever any of their groups allows. With no groups, they can use everything." +
        (local.length < data.groups.length ? " Active Directory groups aren't listed: AD decides who is in them." : ""),
      body: checkList("groups", local.map((g) => [g.id, g.name, g.description]), user.groups.filter(isLocal)),
      onSubmit: async ({ groups = [] }) => {
        const now = new Set(groups.map((g) => g.toLowerCase()));
        const before = new Set(user.groups.filter(isLocal).map((g) => g.toLowerCase()));
        for (const g of now) if (!before.has(g)) await api.put(`groups/${enc(g)}/members/${enc(user.username)}`);
        for (const g of before) if (!now.has(g)) await api.del(`groups/${enc(g)}/members/${enc(user.username)}`);
        await load();
        open(user.username);
      },
    });
  }

  function changeQuota(user) {
    const fromGroups = user.groups.map((id) => data.groups.find((g) => g.id.toLowerCase() === id.toLowerCase())).filter((g) => g?.quota);
    formDialog({
      title: `${user.displayName}'s page limit`,
      description: fromGroups.length
        ? `Their groups allow ${fromGroups.map((g) => `${quotaText(g.quota)} (${g.name})`).join(", ")}. A limit set here replaces those.`
        : "None of their groups has a limit. Set one here for just this person.",
      body: quotaField(user.quota, fromGroups.length ? "Use their groups' limits" : "No limit", "Pages are counted when jobs are released: pages times copies."),
      onSubmit: async (values) => {
        await saveQuota(`users/${enc(user.username)}/quota`, values, user.quota);
        await load();
        open(user.username);
      },
    });
  }

  function rename(user) {
    formDialog({
      title: `Rename ${user.username}`,
      description: "The display name shows at stations and in the client. The username can't change.",
      body: field("Display name", input("displayName", { required: true, value: user.displayName })),
      onSubmit: async (values) => {
        await api.patch(`users/${enc(user.username)}`, values);
        await load();
        open(user.username);
      },
    });
  }

  async function setDisabled(user, disabled) {
    if (disabled && !await confirm({
      title: `Disable ${user.displayName}?`,
      message: "They're signed out everywhere and can't sign in, print or release until re-enabled. Held jobs are kept until they expire.",
      confirmLabel: "Disable",
    })) return;
    if (await attempt(() => api.patch(`users/${enc(user.username)}`, { disabled }), disabled ? "User disabled" : "User enabled") === undefined) return;
    await load();
    open(user.username);
  }

  async function remove(user) {
    const held = data.held.get(user.username)?.length ?? 0;
    if (!await confirm({
      title: `Delete ${user.displayName}?`,
      message: `Their cards are unlinked${held ? ` and their ${plural(held, "held job")} canceled` : ""}. Past jobs stay in the history under their name. To keep the account for later, disable it instead.`,
      confirmLabel: "Delete user",
    })) return;
    if (await attempt(() => api.del(`users/${enc(user.username)}`), "User deleted") === undefined) return;
    ctx.setId(null);
    await load();
  }

  function addUser() {
    formDialog({
      title: "Add user",
      description: "They'll get a client token to put in their TapQueue client's config.",
      submitLabel: "Add user",
      body: [
        field("Username", input("username", { required: true, placeholder: "e.g. asmith" }), "What they sign in with. Usually their Windows or directory username."),
        field("Display name", input("displayName", { placeholder: "e.g. Alex Smith" }), "Shown at the station when they tap. Defaults to the username."),
      ],
      onSubmit: async (values) => {
        const created = await api.post("users", values);
        await load();
        return tokenResult(created, "Put this in their client.toml as token = \"…\". It won't be shown again.");
      },
    });
  }

  function resetToken(user) {
    formDialog({
      title: `Reset ${user.username}'s client token?`,
      description: "Their current token stops working. Their client will need the new one to sign in.",
      submitLabel: "Reset token",
      danger: true,
      body: data.server.authMode === "dev" ? h("div", { class: "callout info" }, "Dev mode ignores tokens, so this only matters once sign-in is turned on.") : null,
      onSubmit: async () => tokenResult(await api.post(`users/${enc(user.username)}/token`), "Copy it now. It won't be shown again."),
    });
  }

  await load();
  if (ctx.id) open(ctx.id);
}

function tokenResult({ user, token }, note) {
  return h("div", null, h("p", { style: "margin:0 0 10px" }, "Client token for ", h("b", null, user.username), ":"), secret(token, note));
}
