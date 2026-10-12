import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, props, time, dateTime, duration, plural, bytes, table, empty, field, checkField, input, checkbox, select, formDialog, confirm, attempt, toast } from "../ui.js";

const MAX_LINES = 2000;

export async function render(root, ctx) {
  let s = await api.get("server");
  // Null for admins without a role in the Server area.
  let u = await api.maybe("server/update", null);
  // Older releases to go back to; asked of GitHub only when someone presses the button.
  let d = u && await api.maybe("server/downgrade", null);
  // Null for everyone but full admins: a backup holds everything.
  let b = await api.maybe("server/backups", null);
  // The server.toml settings a restart applies. Null for everyone but full admins: sign-in mode and the backup passphrase are among them.
  let st = await api.maybe("server/startup", null);
  if (!ctx.current) return;

  const status = h("div");
  const startup = h("div");
  const backups = h("div");
  const settings = h("div");
  const updates = h("div");
  const downgrades = h("div");
  const olderButton = button("Look for older releases", { small: true, iconName: "refresh", onclick: lookForOlder });
  const checkButton = button("Check for updates", { small: true, iconName: "refresh", onclick: () => checkForUpdates(true) });
  const restartButton = button("Restart", { iconName: "refresh", onclick: restart });
  root.append(pageHead("Server", "The TapQueue server this console is running on.",
    h("a", { class: "btn", href: "/healthz", target: "_blank" }, "Health check"), restartButton),
  h("div", { class: "grid two" },
    panel({ title: "Status", body: status }),
    panel({ title: "Settings", actions: button("Edit", { small: true, onclick: edit }), body: settings })),
  st && panel({
    title: "Startup settings",
    description: "What the server reads when it starts: sign-in, HTTPS, discovery, reverse proxies and where backups go. A change here is used from the next restart, in place of server.toml's.",
    actions: button("Edit", { small: true, onclick: editStartup }),
    body: startup,
  }),
  u && panel({ title: "Updates", description: "New TapQueue server releases on GitHub.", actions: checkButton, body: updates }),
  d && panel({ title: "Downgrade", description: "Take the server, and every PC and station, back to an older release when an update turned out badly.",
    actions: olderButton, body: downgrades }),
  b && panel({
    title: "Backups",
    description: "The database, this server's certificate, the badge key and the Active Directory CA. Held documents and published builds aren't in them.",
    actions: [button("Schedule", { small: true, onclick: editBackupSchedule }), button("Back up now", { small: true, onclick: backUpNow })],
    body: backups,
  }),
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
      ["Waiting per person", [s.heldMegabytesPerUser ? `Up to ${s.heldMegabytesPerUser.toLocaleString()} MB` : "No limit",
        h("span", { class: "sub" }, s.heldMegabytesPerUser
          ? "A job that would take someone's held documents past this is refused, so nobody can fill the disk."
          : "Anyone who may print can fill the server's disk with held jobs.")]],
      ["Job history", [s.jobHistoryDays ? `Kept ${plural(s.jobHistoryDays, "day")}` : "Kept for ever",
        h("span", { class: "sub" }, s.jobHistoryDays
          ? "Finished jobs (document names, owners, where they came from) are deleted after this. Reports reach back no further."
          : "Finished jobs' document names, owners and source PCs are never deleted.")]],
      ["Activity log", [s.activityDays ? `Kept ${plural(s.activityDays, "day")}` : "Kept for ever"]],
      ["Time zone", [h("span", { class: "mono" }, s.timeZone),
        h("span", { class: "sub" }, "Where days start: page limits, reports, and the times of the daily backup and directory sync."),
        h("span", { class: "sub" }, s.changedSettings?.includes("timeZone") ? "Set here." : "This machine's.")]],
      ["Taps", [s.tapWaitSeconds ? `Wait up to ${plural(s.tapWaitSeconds, "second")} for the printer` : "Don't wait for the printer",
        h("span", { class: "sub" }, `Then the station reads the next card while the job is still being sent. A busy printer is retried for ${plural(s.printerBusySeconds, "second")}; one job may take ${plural(s.printerSendSeconds, "second")} to send.`)]],
      ["Wrong passwords", [`${s.signInAttempts} tries, then ${plural(s.signInLockoutMinutes, "minute")} locked`,
        h("span", { class: "sub" }, "Counted for each name from each address, so guessing from one machine doesn't lock the person out everywhere.")]],
      ["Reverse proxies", s.trustedProxies?.length
        ? [h("code", null, s.trustedProxies.join(", ")), h("span", { class: "sub" }, "Callers' addresses are taken from X-Forwarded-For when a request comes from these.")]
        : ["None", h("span", { class: "sub" }, "If the server is behind one, name it under Startup settings, or everyone shares the proxy's address.")]],
      ["Jobs without a PC key", [s.addressMatching === "off" ? "Refused" : "Matched by address",
        h("span", { class: "sub" }, s.addressMatching === "off"
          ? "Only jobs from PCs whose TapQueue client has a key (0.8 on) go to someone."
          : "Jobs from clients older than 0.8 go to whoever is signed in at that address. Turn this off once every PC under Workstations has a key.")]],
      ["New PCs", [s.enrollmentCodeRequired ? pill("Need the enrollment code", "ok") : pill("Open", "warn"),
        h("span", { class: "sub" }, s.enrollmentCodeRequired
          ? "A PC's TapQueue service must send the code (enrollment_code in client.toml) the first time it checks in."
          : "Any machine that can reach the server can register as a PC."),
        h("span", { style: "display:flex;gap:8px;margin-top:6px" },
          button(s.enrollmentCodeRequired ? "New code…" : "Require a code…", { small: true, onclick: newEnrollmentCode }),
          s.enrollmentCodeRequired && button("Stop asking", { small: true, kind: "ghost danger", onclick: stopEnrollmentCode }))]],
      ["Client sign-in", s.authMode === "dev"
        ? [pill("Dev", "bad"), h("span", { class: "sub" }, "Anyone can sign in as any username, and this console is open. Change it under Startup settings.")]
        : [pill("Tokens", "ok"), h("span", { class: "sub" }, "Clients need the token from Users.")]],
    ]));
  }

  function drawStartup() {
    const n = st.next, r = st.running;
    const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
    // Where a value comes from, and whether the server is using it yet.
    const notes = (...keys) => [
      keys.some((k) => !same(n[k], r[k])) && pill("After a restart", "warn"),
      h("span", { class: "sub" }, keys.some((k) => st.setHere.includes(k === "backupEncrypted" ? "backupPassphrase" : k)) ? "Set here." : "From server.toml."),
    ];
    const tlsOn = n.tlsListen !== "";
    startup.replaceChildren(...[
      st.restartNeeded ? h("div", { class: "callout", style: "display:flex;align-items:center;gap:12px" },
        h("span", { style: "flex:1" }, st.canRestart
          ? "Saved, but not in use yet: the server reads these when it starts."
          : "Saved, but not in use yet: restart the server where you started it."),
        st.canRestart && button("Restart now", { small: true, iconName: "refresh", onclick: restart })) : null,
      props([
        ["Client sign-in", [n.authMode === "dev" ? pill("Dev", "bad") : pill("Tokens", "ok"),
          h("span", { class: "sub" }, n.authMode === "dev"
            ? "Anyone can sign in as any username, and this console is open to anyone who can reach it. For setting up and testing."
            : "Clients need the token from Users, and this console asks for a sign-in."),
          ...notes("authMode")]],
        ["HTTPS and IPPS", [tlsOn ? h("code", null, n.tlsListen) : pill("Off", "bad"),
          h("span", { class: "sub" }, !tlsOn ? "Print jobs and tokens cross the network unencrypted."
            : n.tlsRequire ? "Plain HTTP is refused from other machines."
            : "Plain HTTP still answers too. Refuse it once every PC and station uses https://."),
          ...notes("tlsListen", "tlsRequire")]],
        tlsOn && ["Certificate", [n.tlsCertFile ? h("code", null, n.tlsCertFile) : "Self-signed, made by this server",
          h("span", { class: "sub" }, n.tlsCertFile ? `Private key: ${n.tlsKeyFile}`
            : n.tlsNames.length ? `Also for the names ${n.tlsNames.join(", ")}.` : "For this machine's own name and addresses."),
          ...notes("tlsCertFile", "tlsKeyFile", "tlsNames")]],
        ["Finding the server", [n.discoveryPort ? `Answers on UDP port ${n.discoveryPort}` : "Off",
          h("span", { class: "sub" }, n.discoveryPort ? "The client installers find this server by themselves." : "People type the server's address when they install a client."),
          ...notes("discoveryPort")]],
        ["Reverse proxies", [n.trustedProxies.length ? h("code", null, n.trustedProxies.join(", ")) : "None",
          h("span", { class: "sub" }, n.trustedProxies.length
            ? "Callers' addresses are taken from X-Forwarded-For when a request comes from these."
            : "Every caller's address is the one it connects from."),
          ...notes("trustedProxies")]],
        ["Backups copied to", [n.backupCopyTo ? h("code", null, n.backupCopyTo) : "Nowhere else", ...notes("backupCopyTo")]],
        ["Backup encryption", [n.backupEncrypted ? pill("With a passphrase", "ok") : pill("Not encrypted", "warn"), ...notes("backupEncrypted")]],
        ["Only in server.toml", [h("span", null, "Plain HTTP on ", h("code", null, st.listen), ", data in ", h("code", null, st.dataDir), ", and the admin token."),
          h("span", { class: "sub" }, "tapqueue-admin and the install scripts find the server by these, so they're changed in the file.")]],
      ]),
      st.setHere.length ? h("div", { style: "margin-top:12px" },
        button("Go back to server.toml's", { small: true, kind: "ghost", onclick: useFile })) : null,
    ].filter(Boolean));
  }

  function editStartup() {
    const n = st.next;
    const list = (text) => text.split(/[\s,]+/).filter(Boolean);
    const encryption = select("encryption", [
      ["keep", n.backupEncrypted ? "Keep the passphrase there is" : "Not encrypted"],
      ["new", n.backupEncrypted ? "Change the passphrase" : "Encrypt with a passphrase"],
      ...(n.backupEncrypted ? [["off", "Stop encrypting"]] : []),
    ], "keep");
    const passphraseField = field("Backup passphrase", input("backupPassphrase", { type: "password", autocomplete: "new-password", minlength: 12 }),
      "At least 12 characters. Restoring a backup needs it, and it can't be read back here: keep a copy that isn't on this server. Backups made before keep the passphrase they were made with.");
    passphraseField.hidden = true;
    encryption.onchange = () => { passphraseField.hidden = encryption.value !== "new"; };
    formDialog({
      title: "Startup settings",
      description: "Saved now, used from the next restart. A value the same as server.toml's follows the file.",
      wide: true,
      body: [
        field("Client sign-in", select("authMode", [
          ["token", "Tokens: clients need their token, the console needs a sign-in"],
          ["dev", "Dev: a username is enough, and the console is open"],
        ], n.authMode),
          n.authMode === "dev"
            ? "Before switching to tokens, make someone a full admin under Admins, or nobody can sign in to this console."
            : "Dev is for setting up and testing: anyone who can reach the server can print as anyone and change anything here."),
        field("HTTPS and IPPS on", input("tlsListen", { value: n.tlsListen, placeholder: "0.0.0.0:8632" }),
          "An address and port, like 0.0.0.0:8632 for every address this machine has. Empty turns HTTPS off: PCs and stations set up with https:// stop working."),
        checkField("Refuse plain HTTP from other machines", checkbox("tlsRequire", n.tlsRequire),
          "Only once every PC and station uses https://, and you've opened this console over https:// yourself."),
        field("Certificate file", input("tlsCertFile", { value: n.tlsCertFile, placeholder: "/etc/tapqueue/server.crt" }),
          "PEM, with any intermediate certificates after the server's. Empty for the self-signed certificate this server makes. The server has to be able to read it."),
        field("Private key file", input("tlsKeyFile", { value: n.tlsKeyFile, placeholder: "/etc/tapqueue/server.key" })),
        field("More names for the self-signed certificate", input("tlsNames", { value: n.tlsNames.join(", "), placeholder: "print.example.com, 10.0.0.5" }),
          "Names or addresses PCs reach this server by, besides this machine's own. A certificate that's already made isn't remade: see docs/tls.md."),
        field("Answer installers looking for a server on UDP port", input("discoveryPort", { type: "number", min: 0, max: 65535, required: true, value: n.discoveryPort }),
          "0 for no answer; people then type the server's address."),
        field("Reverse proxies", input("trustedProxies", { value: n.trustedProxies.join(", "), placeholder: "10.0.0.5, 10.0.1.0/24" }),
          "Addresses or networks of proxies in front of this server. Only from these is X-Forwarded-For believed. Empty if there's none."),
        field("Copy every backup to", input("backupCopyTo", { value: n.backupCopyTo, placeholder: "/mnt/nas/tapqueue" }),
          "A folder on another disk or a mounted share. The service has to be allowed to write there (ReadWritePaths in its systemd unit): see docs/backup.md."),
        field("Backup encryption", encryption),
        passphraseField,
      ],
      onSubmit: async (v) => {
        const body = {
          authMode: v.authMode, tlsListen: v.tlsListen, tlsRequire: v.tlsRequire, tlsCertFile: v.tlsCertFile, tlsKeyFile: v.tlsKeyFile,
          tlsNames: list(v.tlsNames), discoveryPort: Number(v.discoveryPort), trustedProxies: list(v.trustedProxies), backupCopyTo: v.backupCopyTo,
        };
        if (v.encryption === "new") {
          if (!v.backupPassphrase) throw new Error("Type the passphrase to encrypt backups with.");
          body.backupPassphrase = v.backupPassphrase;
        } else if (v.encryption === "off") {
          body.backupPassphrase = "";
        }
        st = await api.patch("server/startup", body);
        drawStartup();
        toast(st.restartNeeded ? "Saved. Restart the server to use them." : "Startup settings saved");
      },
    });
  }

  async function useFile() {
    if (!await confirm({
      title: "Go back to server.toml's settings?",
      message: "Everything changed here is forgotten, and from the next restart the server uses what server.toml says.",
      confirmLabel: "Use server.toml",
    })) return;
    const updated = await attempt(() => api.patch("server/startup", { reset: st.setHere }));
    if (!updated || !ctx.current) return;
    st = updated;
    drawStartup();
    toast(st.restartNeeded ? "Restart the server to use server.toml's settings." : "Using server.toml's settings");
  }

  function drawUpdates() {
    const latest = u.latest;
    const run = u.lastRun;
    const runTone = { done: "ok", failed: "bad" }[run?.state] ?? "warn";
    const runLabel = { requested: "Starting", running: "Working", done: "Done", failed: "Failed" }[run?.state];
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
        run && ["Last change", [pill(runLabel, runTone), ` ${run.version} `,
          run.at && h("span", { class: "sub" }, `${run.message} (${dateTime(run.at)})`)]],
      ]),
      u.updateAvailable && u.canApply && !u.scheduled && !["requested", "running"].includes(run?.state)
        ? button(`Update to ${latest.version}…`, { kind: "primary", onclick: offerUpdate })
        : null,
    ].filter(Boolean));
  }

  function drawBackups() {
    const why = { daily: "Daily", manual: "Made by an admin", upgrade: "Before an upgrade", downgrade: "Before a downgrade" };
    backups.replaceChildren(...[
      b.lastError ? h("div", { class: "callout bad" }, `The last daily backup failed: ${b.lastError}`) : null,
      props([
        ["Daily backup", b.time
          ? [`At ${b.time} (${b.timeZone})`, h("span", { class: "sub" }, `The newest ${b.keep} are kept. Next: ${dateTime(b.nextRunAt)}`)]
          : [pill("Off", "warn"), h("span", { class: "sub" }, "Only backups you make here, and one before each upgrade.")]],
        ["Kept in", [h("code", null, b.directory), h("span", { class: "sub" },
          b.copyTo ? "On this server's own disk. " : "On this server's own disk: download copies, or fetch them from another machine with tapqueue-admin backups download latest. ",
          "To restore one, stop the server and run tapqueue-server --restore.")]],
        ["Copied to", b.copyTo
          ? [h("code", null, b.copyTo), b.copyError ? pill("Not reachable", "bad") : pill("Up to date", "ok"),
            h("span", { class: "sub" }, b.copyError ? `The last copy failed: ${b.copyError}. Backups made meanwhile follow when it works again.`
              : "Every backup is copied here as it's made, and deleted here when it's deleted above.")]
          : [pill("Nowhere else", "warn"), h("span", { class: "sub" },
            "If this disk dies, so do the backups. Name a folder on another disk or a mounted share under Startup settings.")]],
        ["Encryption", b.encrypted
          ? [pill("Encrypted", "ok"), h("span", { class: "sub" }, "With the backup passphrase. Restoring needs it: keep a copy that isn't on this server.")]
          : [pill("Not encrypted", "warn"), h("span", { class: "sub" },
            "A backup holds password hashes, the badge key and this server's private key. Set a passphrase under Startup settings to encrypt them.")]],
      ]),
      table({
        rows: b.backups,
        empty: empty("No backups yet", b.time ? `The first daily one is made at ${b.time}.` : "Make one with Back up now."),
        columns: [
          { label: "Made", value: (x) => h("span", { class: "cell-strong", title: x.name }, dateTime(x.createdAt)) },
          { label: "Why", value: (x) => [why[x.kind] ?? x.kind,
            h("span", { class: "sub" }, [x.encrypted ? "Encrypted" : "Not encrypted", x.copied === false ? "not copied yet" : x.copied ? "copied" : null].filter(Boolean).join(", "))] },
          { label: "Size", class: "num", value: (x) => bytes(x.sizeBytes) },
          { label: "", class: "num", value: (x) => h("span", { style: "display:inline-flex;gap:8px" },
            h("a", { class: "btn small", href: `/api/v1/admin/server/backups/${enc(x.name)}`, download: x.name }, "Download"),
            button("Delete", { small: true, kind: "ghost danger", onclick: () => deleteBackup(x) })) },
        ],
      }),
    ].filter(Boolean));
  }

  async function backUpNow() {
    const made = await attempt(() => api.post("server/backups"));
    if (!made || !ctx.current) return;
    b = await api.get("server/backups");
    drawBackups();
    toast(`Backup made (${bytes(made.sizeBytes)})`);
  }

  async function deleteBackup(backup) {
    if (!await confirm({
      title: "Delete this backup?",
      message: `The backup made ${dateTime(backup.createdAt)} is deleted from the server. Copies you downloaded aren't touched.`,
      confirmLabel: "Delete",
    })) return;
    if (await attempt(() => api.del(`server/backups/${enc(backup.name)}`), "Backup deleted") === undefined) return;
    b = await api.get("server/backups");
    drawBackups();
  }

  function editBackupSchedule() {
    const on = select("on", [["on", "Every day"], ["off", "Off"]], b.time ? "on" : "off");
    const at = input("time", { type: "time", required: true, value: b.time ?? "02:00" });
    const atField = field("At", at, `In the server's time zone (${b.timeZone}). If the server is off then, it's made when it next starts.`);
    atField.hidden = !b.time;
    on.onchange = () => { atField.hidden = on.value === "off"; };
    formDialog({
      title: "Backup schedule",
      body: [
        field("Back up", on),
        atField,
        field("Daily backups to keep", input("keep", { type: "number", min: 1, max: 365, required: true, value: b.keep }),
          "Older daily ones are deleted. Backups you make yourself stay until you delete them."),
      ],
      onSubmit: async (v) => {
        b = await api.patch("server/backups/schedule", { time: v.on === "off" ? "off" : v.time, keep: Number(v.keep) });
        drawBackups();
        toast("Backup schedule saved");
      },
    });
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

  function drawDowngrades() {
    downgrades.replaceChildren(...[
      d.checkError ? h("div", { class: "callout bad" }, d.checkError) : null,
      d.versions.length > 0 && !d.canApply ? h("div", { class: "callout" }, d.cannotApplyReason) : null,
      !d.checkedAt ? h("p", { class: "muted", style: "margin:0" }, `Releases from ${d.oldest} on can be gone back to. Nothing is asked of GitHub until you look.`)
        : d.versions.length === 0 && !d.checkError ? h("p", { class: "muted", style: "margin:0" }, `There's no release older than ${d.current} to go back to (the oldest that can be is ${d.oldest}).`)
        : null,
      ...d.versions.map((v) => h("div", { style: "display:flex; align-items:center; gap:10px; margin:6px 0" },
        h("a", { class: "mono", href: v.url, target: "_blank", rel: "noopener" }, v.version),
        v.publishedAt && h("span", { class: "sub" }, `Released ${dateTime(v.publishedAt)}`),
        d.canApply && button(`Go back to ${v.version}…`, { small: true, kind: "danger", onclick: () => offerDowngrade(v) }))),
    ].filter(Boolean));
  }

  async function lookForOlder() {
    olderButton.disabled = true;
    const checked = await attempt(() => api.get("server/downgrade?refresh=true"));
    olderButton.disabled = false;
    if (!checked || !ctx.current) return;
    d = checked;
    drawDowngrades();
  }

  async function offerDowngrade(release) {
    const version = release.version;
    if (!await confirm({
      title: `Go back to TapQueue ${version}?`,
      message: `This server runs ${d.current}. It will hand out the ${version} clients and station build, which every PC and station installs within about a minute, ` +
        `then install server ${version} and restart; printing, releasing and this console stop for up to a minute. ` +
        `Anything only ${d.current} keeps track of is dropped from the database on the way back; a backup is made first. Held jobs, users and settings are kept.`,
      confirmLabel: `Go back to ${version}`,
    })) return;
    toast(`Getting ${version} from GitHub…`);
    const started = await attempt(() => api.post("server/downgrade", { version }));
    if (!started || !ctx.current) return;
    d = started;
    followUpgrade(version, "Going back");
  }

  async function cancelScheduled() {
    const updated = await attempt(() => api.del("server/update"), "Scheduled upgrade called off");
    if (updated && ctx.current) { u = updated; drawUpdates(); }
  }

  /** Waits for the server to come back on the new version, or for the updater to say it failed. */
  async function followUpgrade(version, doing = "Upgrading") {
    toast(`${doing} to ${version}…`);
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
          if (d) { d = await api.maybe("server/downgrade", d); drawDowngrades(); }
          return toast(`Now running TapQueue server ${version}`);
        }
        if (now.lastRun?.state === "failed") {
          draw();
          return toast(`That didn't work: ${now.lastRun.message}`, "bad");
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
        field("Held documents one person may have waiting (MB)",
          input("heldMegabytesPerUser", { type: "number", min: 0, max: 1000000, required: true, value: s.heldMegabytesPerUser }),
          "0 for no limit. One job can be 512 MB at most."),
        field("Jobs that arrive without a PC key", select("addressMatching", [
          ["on", "Match them to whoever is signed in at that address"],
          ["off", "Don't match them to anyone"],
        ], s.addressMatching),
          "Clients from 0.8 on give each PC a key, so its jobs are matched by PC and PC user, not address."),
        field("Keep job history for (days)", input("jobHistoryDays", { type: "number", min: 0, max: 3650, required: true, value: s.jobHistoryDays }),
          "0 keeps it for ever; otherwise 32 or more, so monthly page limits can still be counted. Reports reach back only this far."),
        field("Keep the activity log for (days)", input("activityDays", { type: "number", min: 0, max: 3650, required: true, value: s.activityDays }),
          "0 keeps it for ever."),
        field("Time zone", h("div", { class: "field-row" },
          input("timeZone", { required: true, value: s.timeZone, placeholder: "Europe/Oslo", list: "time-zones" }),
          select("timeZoneSource", [["here", "Set here"], ["machine", "Use this machine's"]], changed("timeZone") ? "here" : "machine")),
          "An IANA name like Europe/Oslo or America/Chicago. Days start at midnight there."),
        h("datalist", { id: "time-zones" }, ...(Intl.supportedValuesOf?.("timeZone") ?? []).map((z) => h("option", { value: z }))),
        field("A tap waits for the printer for (seconds)", input("tapWaitSeconds", { type: "number", min: 0, max: 120, required: true, value: s.tapWaitSeconds }),
          "0 to 120. After this the station reads the next card; the job carries on, and goes back on hold if the printer refuses it."),
        field("Retry a busy printer for (seconds)", input("printerBusySeconds", { type: "number", min: 0, max: 3600, required: true, value: s.printerBusySeconds })),
        field("Sending one job may take (seconds)", input("printerSendSeconds", { type: "number", min: 10, max: 3600, required: true, value: s.printerSendSeconds }),
          "10 to 3600. Raise it for very large jobs on slow printers."),
        field("Wrong passwords before sign-in is refused", h("div", { class: "field-row" },
          input("signInAttempts", { type: "number", min: 3, max: 100, required: true, value: s.signInAttempts }),
          input("signInLockoutMinutes", { type: "number", min: 1, max: 1440, required: true, value: s.signInLockoutMinutes, "aria-label": "Minutes locked" })),
          "Tries (3 to 100), then minutes locked (1 to 1440), for one name from one address."),
      ],
      onSubmit: async (v) => {
        const body = { reset: [] };
        if (v.holdHoursSource === "file") body.reset.push("holdHours"); else body.holdHours = Number(v.holdHours);
        if (v.sessionTimeoutSource === "file") body.reset.push("sessionTimeoutMinutes"); else body.sessionTimeoutMinutes = Number(v.sessionTimeoutMinutes);
        if (v.quotaOverrun !== s.quotaOverrun) body.quotaOverrun = v.quotaOverrun;
        if (v.addressMatching !== s.addressMatching) body.addressMatching = v.addressMatching;
        if (Number(v.heldMegabytesPerUser) !== s.heldMegabytesPerUser) body.heldMegabytesPerUser = Number(v.heldMegabytesPerUser);
        for (const key of ["jobHistoryDays", "activityDays", "tapWaitSeconds", "printerBusySeconds", "printerSendSeconds", "signInAttempts", "signInLockoutMinutes"])
          if (Number(v[key]) !== s[key]) body[key] = Number(v[key]);
        if (v.timeZoneSource === "machine") body.reset.push("timeZone");
        else if (v.timeZone.trim() !== s.timeZone || !changed("timeZone")) body.timeZone = v.timeZone.trim();
        s = await api.patch("server/settings", body);
        draw();
        toast("Settings saved");
      },
    });
  }

  /** The code is shown once, here: the server only keeps its hash. */
  function newEnrollmentCode() {
    formDialog({
      title: s.enrollmentCodeRequired ? "Make a new enrollment code?" : "Require an enrollment code?",
      description: s.enrollmentCodeRequired
        ? "The old code stops working for PCs that haven't registered yet. PCs that already have a key aren't affected."
        : "New PCs will need the code to register. PCs that already have a key (see Workstations) aren't affected; ones without need client 0.9.5 and the code.",
      submitLabel: "Make code",
      body: [],
      onSubmit: async () => {
        const made = await api.post("server/enrollment-code");
        s = await api.get("server");
        draw();
        return h("div", null,
          h("p", { style: "margin-top:0" }, "Copy it now; it isn't shown again."),
          h("p", null, h("code", { style: "user-select:all;word-break:break-all" }, made.code)),
          h("p", { class: "muted", style: "margin-bottom:0" },
            "On each new PC: enrollment_code in client.toml, /ENROLL=<code> for the Windows installer, or TAPQUEUE_ENROLLMENT_CODE for install.sh and install.ps1."));
      },
    });
  }

  async function stopEnrollmentCode() {
    if (!await confirm({
      title: "Stop asking new PCs for a code?",
      message: "Any machine that can reach the server will be able to register as a PC again.",
      confirmLabel: "Stop asking",
    })) return;
    if (await attempt(() => api.del("server/enrollment-code"), "New PCs no longer need a code") === undefined) return;
    s = await api.get("server");
    draw();
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
          if (st) { st = await api.maybe("server/startup", st); drawStartup(); }
          toast(`Server is back (${s.version})`);
          return;
        }
      } catch { /* still down */ }
    }
    if (ctx.current) toast("The server hasn't come back after a minute. Check it with: systemctl status tapqueue-server", "bad");
  }

  draw();
  if (st) drawStartup();
  if (u) drawUpdates();
  if (d) drawDowngrades();
  if (b) drawBackups();
  ctx.every(30000, async () => {
    s = await api.get("server");
    if (u) u = await api.get("server/update");
    if (b) b = await api.get("server/backups");
    if (st) st = await api.get("server/startup");
    if (ctx.current) { draw(); if (st) drawStartup(); if (u) drawUpdates(); if (b) drawBackups(); }
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
