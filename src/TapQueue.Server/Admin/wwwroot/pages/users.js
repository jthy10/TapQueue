import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, time, dateTime, table, empty, drawer, props, sectionTitle, field, input,
  checkList, formDialog, confirm, secret, attempt, loading, plural } from "../ui.js";
import { enrollCard, editCard, removeCard } from "./cards.js";
import { jobStatus } from "./jobs.js";
import { activityList } from "./activity.js";

export async function render(root, ctx) {
  let data;
  const list = h("div", null, loading());
  root.append(pageHead("Users", "People who print. Each one signs in on their PC with the TapQueue client and releases jobs with a card.",
    button("Add user", { kind: "primary", iconName: "plus", onclick: addUser })),
  panel({ flush: true, body: list }));

  async function load() {
    const [users, badges, held, clients, server, groups] = await Promise.all([
      api.get("users"), api.get("badges"), api.get("jobs?status=held"), api.get("clients"), api.get("server"), api.get("groups"),
    ]);
    if (!ctx.current) return;
    const by = (rows, key) => rows.reduce((m, r) => m.set(r[key], [...(m.get(r[key]) ?? []), r]), new Map());
    data = { users, server, groups, badges: by(badges, "username"), held: by(held, "owner"), clients: by(clients, "username") };

    list.replaceChildren(table({
      rows: users,
      search: (u) => `${u.username} ${u.displayName}`,
      onRowClick: (u) => open(u.username),
      empty: empty("No users yet", server.authMode === "dev"
        ? "In dev mode, anyone who signs in with the client is added automatically."
        : "Add people here, then give them their client token.", button("Add user", { kind: "primary", onclick: addUser })),
      columns: [
        { label: "Name", value: (u) => [h("span", { class: "cell-strong" }, u.displayName, " ", u.disabledAt && pill("Disabled", "bad")), h("span", { class: "sub" }, u.username)] },
        { label: "Groups", value: (u) => u.groups.length ? u.groups.map(groupName).join(", ") : h("span", { class: "muted" }, "None (can use everything)") },
        { label: "Cards", value: (u) => count(data.badges.get(u.username), "card", "No card") },
        { label: "Held jobs", class: "num", value: (u) => data.held.get(u.username)?.length ?? 0 },
        { label: "Signed in", value: (u) => {
          const sessions = data.clients.get(u.username);
          return sessions ? pill(sessions.map((s) => s.hostname ?? s.remoteIp).join(", "), "ok") : h("span", { class: "muted" }, "No");
        } },
      ],
    }));
  }

  const groupName = (id) => data.groups.find((g) => g.id.toLowerCase() === id.toLowerCase())?.name ?? id;

  function count(rows, noun, none) {
    return rows?.length ? plural(rows.length, noun) : h("span", { class: "muted" }, none);
  }

  async function open(username) {
    ctx.setId(username);
    const user = data.users.find((u) => u.username.toLowerCase() === username.toLowerCase());
    if (!user) return ctx.setId(null);
    const [badges, jobs, events] = await Promise.all([
      api.get(`badges?username=${enc(user.username)}`), api.get("jobs"), api.get(`events?subject=${enc(`user:${user.username}`)}&limit=15`),
    ]);
    const theirJobs = jobs.filter((j) => j.owner === user.username).slice(0, 10);
    const sessions = data.clients.get(user.username) ?? [];
    const refresh = async () => { await load(); open(user.username); };

    drawer({
      title: user.displayName,
      subtitle: `${user.username} · user #${user.id}`,
      onClose: () => ctx.setId(null),
      body: [
        user.disabledAt && h("div", { class: "callout" }, `Disabled since ${dateTime(user.disabledAt)}. They can't sign in, print or release; their held jobs are kept until they expire.`),
        props([
          ["Status", user.disabledAt ? pill("Disabled", "bad") : pill("Active", "ok")],
          ["Added", dateTime(user.createdAt)],
          ["Groups", h("div", null,
            user.groups.length
              ? h("div", { class: "chips" }, user.groups.map((g) => h("a", { class: "pill plain", href: `groups/${enc(g)}` }, groupName(g))))
              : h("span", { class: "muted" }, "None, so they can print everywhere."),
            h("div", { style: "margin-top:8px" }, button("Change groups", { small: true, onclick: () => changeGroups(user) })))],
        ]),
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
        h("div", { class: "left" }, button("Delete", { kind: "ghost danger", onclick: () => remove(user) })),
        user.disabledAt
          ? button("Enable", { onclick: () => setDisabled(user, false) })
          : button("Disable", { kind: "danger", onclick: () => setDisabled(user, true) }),
        button("Reset token", { iconName: "key", onclick: () => resetToken(user) }),
        button("Rename", { kind: "primary", onclick: () => rename(user) }),
      ],
    });
  }

  function changeGroups(user) {
    if (data.groups.length === 0) {
      formDialog({ title: "Change groups", body: h("p", { style: "margin:0" }, "There are no groups yet. Add one on the Groups page."), submitLabel: "OK", onSubmit: () => {} });
      return;
    }
    formDialog({
      title: `${user.displayName}'s groups`,
      description: "They may use whatever any of their groups allows. With no groups, they can use everything.",
      body: checkList("groups", data.groups.map((g) => [g.id, g.name, g.description]), user.groups),
      onSubmit: async ({ groups = [] }) => {
        const now = new Set(groups.map((g) => g.toLowerCase()));
        const before = new Set(user.groups.map((g) => g.toLowerCase()));
        for (const g of now) if (!before.has(g)) await api.put(`groups/${enc(g)}/members/${enc(user.username)}`);
        for (const g of before) if (!now.has(g)) await api.del(`groups/${enc(g)}/members/${enc(user.username)}`);
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
