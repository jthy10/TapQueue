import { api } from "../api.js";
import { h, pageHead, panel, button, pill, time, dateTime, bytes, table, empty, field, input, select, formDialog, confirm, attempt, toast, loading, plural } from "../ui.js";

const platforms = [["win-x64", "Windows"], ["linux-x64", "Linux"]];
const platformName = (p) => platforms.find(([v]) => v === p)?.[1] ?? p;

export async function render(root, ctx) {
  const list = h("div", null, loading());
  const stationList = h("div", null, loading());
  const clients = releaseCheck({
    path: "client-builds/release",
    what: "client",
    confirmMessage: (v) => `The server downloads the Windows and Linux clients of release ${v} from GitHub, checks them against the release's SHA256SUMS and publishes them. Every PC installs its one within about a minute.`,
  });
  const stationRelease = releaseCheck({
    path: "station-builds/release",
    what: "station build",
    confirmMessage: (v) => `The server downloads the station build of release ${v} from GitHub, checks it against the release's SHA256SUMS and publishes it. Every station installs it within about 15 seconds and restarts.`,
  });
  root.append(pageHead("Updates", "Builds the server hands out. Clients and stations compare themselves to the newest one and install it on their own.",
    button("Upload station build", { iconName: "updates", onclick: publishStation }),
    button("Upload client build", { iconName: "updates", onclick: publish })),
  panel({ title: "Clients", description: "Each PC installs the newest build for its platform, within about a minute. Publishing an older build again rolls PCs back.",
    actions: clients.button, flush: true, body: [clients.status, list] }),
  panel({ title: "Release stations", description: "Stations install the newest build within 15 seconds and restart. Installing an archive by hand on a station takes over from this.",
    actions: stationRelease.button, flush: true, body: [stationRelease.status, stationList] }));

  /**
   * "Check for updates" for one kind of build: asks GitHub for the newest release, shows it against
   * what's handed out now, and publishes it on a click. Nothing is asked of GitHub until the button is pressed.
   */
  function releaseCheck({ path, what, confirmMessage }) {
    const canPublish = ctx.can("updates", "admin");
    const status = h("div");
    const check = button("Check for updates", { small: true, iconName: "refresh", onclick: () => run(true) });
    let r = null;

    function draw() {
      const latest = r?.latest;
      status.replaceChildren(...[
        r?.checkError ? h("div", { class: "callout bad", style: "margin:12px 16px 0" }, r.checkError) : null,
        latest ? h("div", { class: "toolbar" },
          h("span", null, "Newest release: ", h("a", { href: latest.url, target: "_blank", rel: "noopener" }, latest.version), " ",
            r.updateAvailable ? pill("Update available", "accent") : pill("Up to date", "ok"),
            latest.publishedAt && h("span", { class: "muted" }, ` Released ${dateTime(latest.publishedAt)}`)),
          h("div", { class: "grow" }),
          r.updateAvailable && canPublish && button(`Publish ${latest.version}…`, { kind: "primary", small: true, onclick: publishLatest })) : null,
      ].filter(Boolean));
    }

    async function run(refresh) {
      check.disabled = true;
      const found = await attempt(() => api.get(refresh ? `${path}?refresh=true` : path));
      check.disabled = false;
      if (!found || !ctx.current) return;
      r = found;
      draw();
      if (refresh && !r.checkError && !r.updateAvailable) toast(`The newest ${what} (${r.latest.version}) is already published`);
    }

    async function publishLatest() {
      const version = r.latest.version;
      if (!await confirm({ title: `Publish ${what} ${version}?`, message: confirmMessage(version), confirmLabel: "Publish", danger: false })) return;
      toast(`Getting ${what} ${version} from GitHub…`);
      const done = await attempt(() => api.post(path, { version }), `Published ${what} ${version}`);
      if (!done || !ctx.current) return;
      r = done;
      draw();
      await load();
    }

    return { status, button: check, load: () => run(false) };
  }

  async function load() {
    const [builds, workstations, stationBuilds, stations] = await Promise.all([
      api.get("client-builds"), api.maybe("workstations", []), api.get("station-builds"), api.maybe("stations", []),
    ]);
    const current = new Set(platforms.map(([p]) => builds.find((b) => b.platform === p)).filter(Boolean));
    if (!ctx.current) return;
    stationList.replaceChildren(table({
      rows: stationBuilds,
      empty: empty("No station builds published", "Until one is, stations keep the version installed on them.",
        button("Check for updates", { onclick: () => stationRelease.button.click() })),
      columns: [
        { label: "Version", value: (b) => [h("span", { class: "mono cell-strong" }, b.version), " ", b === stationBuilds[0] && pill("Current", "accent")] },
        { label: "Running it", value: (b) => plural(stations.filter((s) => s.version === b.version).length, "station") },
        { label: "Size", class: "num", value: (b) => bytes(b.sizeBytes) },
        { label: "SHA-256", value: (b) => h("code", { title: b.sha256 }, b.sha256.slice(0, 12) + "…") },
        { label: "Published", value: (b) => h("span", { title: dateTime(b.publishedAt) }, time(b.publishedAt)) },
      ],
    }));
    list.replaceChildren(table({
      rows: builds,
      empty: empty("No client builds published", "Until one is, clients keep whatever version they were installed with.",
        button("Check for updates", { kind: "primary", onclick: () => clients.button.click() })),
      columns: [
        { label: "Version", value: (b) => [h("span", { class: "mono cell-strong" }, b.version), " ", current.has(b) && pill("Current", "accent")] },
        { label: "Platform", value: (b) => platformName(b.platform) },
        { label: "Running it", value: (b) => plural(workstations.filter((w) => w.platform === b.platform && w.version === b.version).length, "workstation") },
        { label: "Size", class: "num", value: (b) => bytes(b.sizeBytes) },
        { label: "SHA-256", value: (b) => h("code", { title: b.sha256 }, b.sha256.slice(0, 12) + "…") },
        { label: "Published", value: (b) => h("span", { title: dateTime(b.publishedAt) }, time(b.publishedAt)) },
      ],
    }));
  }

  function publish() {
    formDialog({
      title: "Upload a client build",
      description: "For a build that isn't a GitHub release. Every PC of that platform downloads and installs it when it next checks in.",
      submitLabel: "Publish",
      body: [
        field("Platform", select("platform", platforms, "win-x64")),
        field("Program", input("file", { type: "file", required: true }),
          "Windows: TapQueueClient.exe from TapQueue_client_<version>_win-x64.zip. Linux: tapqueue-client from TapQueue_client_<version>_linux-x64.tar.gz."),
        field("Version", input("version", { required: true, placeholder: "e.g. 0.5.0+1a2b3c4" }), "As in the archive's version.txt. Shown on this page and in each client's tray menu."),
      ],
      onSubmit: async ({ platform, file, version }) => {
        if (!file) throw new Error("Choose the client program.");
        await api.upload(`client-builds?version=${encodeURIComponent(version)}&platform=${encodeURIComponent(platform)}`, file);
        await load();
      },
    });
  }

  function publishStation() {
    formDialog({
      title: "Upload a station build",
      description: "For a build that isn't a GitHub release. Every station downloads it, checks it runs, installs it and restarts, within about 15 seconds.",
      submitLabel: "Publish",
      body: [
        field("tapqueue-station", input("file", { type: "file", required: true }), "The program from the station archive (TapQueue_station_<version>_linux-x64.tar.gz)."),
        field("Version", input("version", { required: true, placeholder: "e.g. 0.3.0+1a2b3c4" }), "What `tapqueue-station --version` prints."),
      ],
      onSubmit: async ({ file, version }) => {
        if (!file) throw new Error("Choose the tapqueue-station program.");
        await api.upload(`station-builds?version=${encodeURIComponent(version)}`, file);
        await load();
      },
    });
  }

  await load();
  // What the last check found, if there was one since the server started.
  await Promise.all([clients.load(), stationRelease.load()]);
}
