import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, time, table, empty, drawer, props, sectionTitle, field, input,
  formDialog, secret, attempt, loading, plural } from "../ui.js";
import { enrollCard, removeCard } from "./cards.js";
import { jobStatus } from "./jobs.js";

export async function render(root, ctx) {
  let data;
  const list = h("div", null, loading());
  root.append(pageHead("Users", "People who print. Each one signs in on their PC with the TapQueue client and releases jobs with a card.",
    button("Add user", { kind: "primary", iconName: "plus", onclick: addUser })),
  panel({ flush: true, body: list }));

  async function load() {
    const [users, badges, held, clients, server] = await Promise.all([
      api.get("users"), api.get("badges"), api.get("jobs?status=held"), api.get("clients"), api.get("server"),
    ]);
    if (!ctx.current) return;
    const by = (rows, key) => rows.reduce((m, r) => m.set(r[key], [...(m.get(r[key]) ?? []), r]), new Map());
    data = { users, server, badges: by(badges, "username"), held: by(held, "owner"), clients: by(clients, "username") };

    list.replaceChildren(table({
      rows: users,
      search: (u) => `${u.username} ${u.displayName}`,
      onRowClick: (u) => open(u.username),
      empty: empty("No users yet", server.authMode === "dev"
        ? "In dev mode, anyone who signs in with the client is added automatically."
        : "Add people here, then give them their client token.", button("Add user", { kind: "primary", onclick: addUser })),
      columns: [
        { label: "Name", value: (u) => [h("span", { class: "cell-strong" }, u.displayName), h("span", { class: "sub" }, u.username)] },
        { label: "Cards", value: (u) => count(data.badges.get(u.username), "card", "No card") },
        { label: "Held jobs", class: "num", value: (u) => data.held.get(u.username)?.length ?? 0 },
        { label: "Signed in", value: (u) => {
          const sessions = data.clients.get(u.username);
          return sessions ? pill(sessions.map((s) => s.hostname ?? s.remoteIp).join(", "), "ok") : h("span", { class: "muted" }, "No");
        } },
      ],
    }));
  }

  function count(rows, noun, none) {
    return rows?.length ? plural(rows.length, noun) : h("span", { class: "muted" }, none);
  }

  async function open(username) {
    ctx.setId(username);
    const user = data.users.find((u) => u.username.toLowerCase() === username.toLowerCase());
    if (!user) return ctx.setId(null);
    const [badges, jobs] = await Promise.all([api.get(`badges?username=${enc(user.username)}`), api.get("jobs")]);
    const theirJobs = jobs.filter((j) => j.owner === user.username).slice(0, 10);
    const sessions = data.clients.get(user.username) ?? [];
    const refresh = async () => { await load(); open(user.username); };

    drawer({
      title: user.displayName,
      subtitle: `${user.username} · user #${user.id}`,
      onClose: () => ctx.setId(null),
      body: [
        sectionTitle("Cards"),
        badges.length
          ? h("div", { class: "panel" }, table({ rows: badges, columns: [
            { label: "Card", value: (b) => h("code", null, b.cardHint) },
            { label: "Last used", value: (b) => time(b.lastUsedAt) },
            { label: "", class: "num", value: (b) => button("Remove", { small: true, kind: "ghost danger", onclick: () => removeCard(b, refresh) }) },
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
      ],
      footer: [
        button("Reset client token", { iconName: "key", onclick: () => resetToken(user) }),
      ],
    });
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
