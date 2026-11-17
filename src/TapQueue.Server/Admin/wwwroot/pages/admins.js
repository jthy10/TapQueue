import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, table, empty, field, input, select, checkbox, checkField, checkList, formDialog, confirm, attempt,
  loading, time, sectionTitle, props, secret, toast } from "../ui.js";

export const areas = [
  ["jobs", "Jobs", "Held jobs and history: release, cancel."],
  ["people", "People", "Users, groups, cards and page limits."],
  ["directory", "Directory", "Active Directory or Entra ID: connection, scope and sync."],
  ["fleet", "Fleet", "Printers, queues, stations and workstations."],
  ["updates", "Updates", "Client and station builds."],
  ["server", "Server", "Settings, live log, restart, crash reports."],
];
const areaName = (id) => id === "*" ? "Every area" : areas.find(([a]) => a === id)?.[1] ?? id;

const roles = [
  ["viewer", "Viewer", "Sees everything in the area, changes nothing."],
  ["operator", "Operator", "Day-to-day work: release and cancel jobs, link cards, restart stations, sign PCs out, run a sync."],
  ["admin", "Admin", "Everything in the area, including settings, adding and removing."],
];
const roleName = (id) => roles.find(([r]) => r === id)?.[1] ?? id;

export async function render(root, ctx) {
  async function testSso() {
    const result = await attempt(() => api.post("sso/test"));
    if (result) toast(result.message, result.success ? undefined : "bad");
  }

  function editSso() {
    formDialog({
      title: "Single sign-on",
      description: "Register TapQueue with your provider as a web application, with the redirect URI below, and enter what it gives you.",
      wide: true,
      body: [
        secret(sso.redirectUri, "Redirect URI to register with the provider:"),
        checkField("Offer single sign-on", checkbox("enabled", sso.enabled), "Adds \"Sign in with…\" to this console's sign-in and the web page's. Passwords keep working."),
        field("Provider address", input("authority", { value: sso.authority, placeholder: "https://login.microsoftonline.com/<tenant-id>/v2.0" }),
          "The issuer: the address that has /.well-known/openid-configuration under it. Entra ID: https://login.microsoftonline.com/<tenant-id>/v2.0. Google: https://accounts.google.com."),
        field("Client ID", input("clientId", { value: sso.clientId })),
        field("Client secret", input("clientSecret", { type: "password", placeholder: sso.hasSecret ? "Saved; leave empty to keep it" : "" }),
          sso.hasSecret ? "Enter it again if you change the provider address." : undefined),
        field("Your domain", input("domain", { value: sso.domain, placeholder: "example.org" }),
          "Someone the provider calls jake@example.org is then the TapQueue user jake. Leave it empty and names have to match usernames exactly; never put a public domain like gmail.com here."),
        field("Username claim", input("usernameClaim", { required: true, value: sso.usernameClaim }), "Which claim in the ID token names the person. preferred_username for Entra ID; email for Google."),
        field("Button says: Sign in with", input("label", { required: true, maxlength: 40, value: sso.label })),
      ],
      onSubmit: async (v) => {
        sso = await api.patch("sso", v);
        await load();
        if (sso.enabled) testSso();
      },
    });
  }

  let data, users = [], groups = [], sso = null;
  const ssoBody = h("div", null, loading());
  const warning = h("div");
  const people = h("div", null, loading());
  const grants = h("div", null, loading());
  root.append(
    pageHead("Admins", "Who can use this console, and what they may do. Give roles to people or to groups; someone in several gets the highest role any of them gives, per area.",
      button("Add admin", { kind: "primary", iconName: "plus", onclick: () => add() })),
    warning,
    panel({ title: "People", description: "Everyone with a role, directly or through a group.", flush: true, body: people }),
    panel({ title: "Roles given", description: "Remove one to take that role away. Only full admins (admin in every area) see this page.", flush: true, body: grants }),
    panel({ title: "Single sign-on", description: "People sign in to this console and to the web page at /my through your organisation's sign-in (Entra ID, Google, Okta, any OpenID Connect provider). What they may do is still decided by the roles here.",
      actions: [button("Test", { small: true, onclick: testSso }), button("Edit", { small: true, onclick: editSso })], body: ssoBody }),
    panel({ title: "What the roles mean", body: h("div", { class: "stack" },
      h("dl", { class: "props" }, roles.flatMap(([, name, text]) => [h("dt", null, name), h("dd", null, text)])),
      sectionTitle("Areas"),
      h("dl", { class: "props" }, areas.flatMap(([, name, text]) => [h("dt", null, name), h("dd", null, text)])),
      h("p", { class: "muted", style: "margin:0" },
        "Local users sign in with their TapQueue password, set on their user page. Active Directory users sign in with their domain password, and anyone can use single sign-on once it's set up. ",
        "Only full admins can change admins, passwords, or groups that give admin rights.")) }),
  );

  async function load() {
    [data, users, groups, sso] = await Promise.all([api.get("admins"), api.get("users"), api.get("groups"), api.get("sso")]);
    if (!ctx.current) return;
    ssoBody.replaceChildren(props([
      ["Single sign-on", sso.enabled ? pill("On", "ok") : pill("Off", "plain")],
      sso.authority && ["Provider", h("code", null, sso.authority)],
      sso.clientId && ["Client ID", [h("code", null, sso.clientId), !sso.hasSecret && h("span", { class: "sub" }, pill("No client secret saved", "warn"))]],
      ["People are", sso.domain
        ? [`name@${sso.domain} is the TapQueue user "name"`, h("span", { class: "sub" }, `From the ${sso.usernameClaim} claim. Anyone outside ${sso.domain} has to be a TapQueue username exactly.`)]
        : ["The TapQueue user whose username is exactly what the provider calls them", h("span", { class: "sub" }, `From the ${sso.usernameClaim} claim. Set your domain to match jake@example.org to the user jake.`)]],
      ["Redirect URI", [h("code", null, sso.redirectUri), h("span", { class: "sub" }, "Register this with the provider. It's this server's address as you opened it here.")]],
    ]));

    const dev = ctx.session.authMode === "dev";
    warning.replaceChildren(data.fullAdmins > 0 ? "" : h("div", { class: `callout ${dev ? "info" : "bad"}` }, dev
      ? "Nobody can sign in as a full admin yet. Before you switch sign-in to tokens (Server, Startup settings), make someone an admin in every area and give them a way to sign in (a password, or an Active Directory account). Otherwise only admin.token can manage the server."
      : "Nobody can sign in as a full admin. Make someone an admin in every area, and check they can sign in."));

    people.replaceChildren(table({
      rows: data.people,
      search: (p) => `${p.username} ${p.displayName} ${p.via.join(" ")}`,
      empty: empty("No admins yet", "Add someone to let them sign in to this console."),
      columns: [
        { label: "Person", value: (p) => [h("a", { class: "cell-strong", href: `users/${enc(p.username)}` }, p.displayName, " ", p.source === "ad" && pill("AD", "plain")),
          h("span", { class: "sub" }, p.username)] },
        { label: "Roles", value: (p) => p.fullAdmin ? pill("Full admin", "ok") : roleList(p.roles) },
        { label: "Through", value: (p) => p.via.map((v) => v === "direct" ? "Directly" : v).join(", ") },
        { label: "Sign-in", value: (p) => p.signInProblem ? h("span", { class: "text-bad" }, p.signInProblem) : pill(p.source === "ad" ? "Domain password" : "Password", "plain") },
      ],
    }));

    grants.replaceChildren(table({
      rows: data.grants,
      empty: empty("No roles given yet"),
      columns: [
        { label: "Given to", value: (g) => g.username
          ? h("a", { href: `users/${enc(g.username)}` }, g.username)
          : [h("a", { href: `groups/${enc(g.groupId)}` }, g.groupName ?? g.groupId), " ", pill("Group", "plain")] },
        { label: "Area", value: (g) => areaName(g.area) },
        { label: "Role", value: (g) => roleName(g.role) },
        { label: "Since", value: (g) => time(g.createdAt) },
        { label: "", class: "num", value: (g) => button("Remove", { small: true, kind: "ghost danger", onclick: () => revoke(g) }) },
      ],
    }));
  }

  function add() {
    const kind = select("kind", [["user", "A person"], ["group", "Everyone in a group"]], "user");
    const userField = field("Person", select("username", users.map((u) => [u.username, `${u.displayName} (${u.username})${u.source === "ad" ? " · AD" : ""}`])));
    const groupField = field("Group", select("groupId", groups.map((g) => [g.id, `${g.name}${g.source === "ad" ? " · AD" : ""}`])),
      "Membership counts as it changes: people who join get the role, people who leave lose it.");
    const every = checkbox("every", true);
    const areaList = checkList("areas", areas.map(([id, name, text]) => [id, name, text]));
    const sync = () => {
      userField.hidden = kind.value !== "user";
      groupField.hidden = kind.value !== "group";
      areaList.hidden = every.checked;
    };
    kind.addEventListener("change", sync);
    every.addEventListener("change", sync);
    sync();

    formDialog({
      title: "Add admin",
      description: "Gives a role in some or all areas, replacing the role they had there.",
      submitLabel: "Give role",
      body: [
        field("Give to", kind),
        userField,
        groupField,
        field("Role", select("role", roles.map(([id, name, text]) => [id, `${name}: ${text}`]), "viewer")),
        sectionTitle("Where"),
        checkField("Every area", every, "Admin in every area makes them a full admin, who can also manage admins."),
        areaList,
      ],
      onSubmit: async (values) => {
        const chosen = values.every ? [] : values.areas ?? [];
        if (!values.every && chosen.length === 0) throw new Error("Pick at least one area, or Every area.");
        await api.post("admins/grants", {
          username: values.kind === "user" ? values.username : null,
          groupId: values.kind === "group" ? values.groupId : null,
          role: values.role,
          areas: chosen,
        });
        await load();
      },
    });
  }

  async function revoke(g) {
    const who = g.username ?? `everyone in ${g.groupName ?? g.groupId}`;
    const self = g.username && g.username === ctx.session.username;
    if (!await confirm({
      title: "Remove this role?",
      message: `${who} will no longer be ${g.role === "viewer" ? "a" : "an"} ${roleName(g.role).toLowerCase()} in ${areaName(g.area).toLowerCase()}.` +
        (self ? " This is your own role: you may lose access to this page." : ""),
      confirmLabel: "Remove role",
    })) return;
    if (await attempt(() => api.del(`admins/grants/${g.id}`), "Role removed") !== undefined) await load();
  }

  await load();
}

/** "Jobs: Operator, Fleet: Viewer" */
export function roleList(roleMap) {
  const entries = areas.filter(([id]) => roleMap[id]).map(([id, name]) => `${name}: ${roleName(roleMap[id])}`);
  return entries.length ? entries.join(", ") : h("span", { class: "muted" }, "None");
}
