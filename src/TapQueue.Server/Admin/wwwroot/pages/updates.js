import { api } from "../api.js";
import { h, pageHead, panel, button, pill, time, dateTime, bytes, table, empty, field, input, formDialog, loading, plural } from "../ui.js";

export async function render(root, ctx) {
  const list = h("div", null, loading());
  const stationList = h("div", null, loading());
  root.append(pageHead("Updates", "Builds the server hands out. Clients and stations compare themselves to the newest one and install it on their own.",
    button("Publish station build", { iconName: "updates", onclick: publishStation }),
    button("Publish client build", { kind: "primary", iconName: "updates", onclick: publish })),
  panel({ title: "Windows client", description: "The newest build is the one every client installs, within about a minute. Publishing an older build again rolls clients back.", flush: true, body: list }),
  panel({ title: "Release stations", description: "Stations install the newest build within 15 seconds and restart. Installing an archive by hand on a station takes over from this.", flush: true, body: stationList }));

  async function load() {
    const [builds, clients, stationBuilds, stations] = await Promise.all([
      api.get("client-builds"), api.get("clients"), api.get("station-builds"), api.get("stations"),
    ]);
    if (!ctx.current) return;
    stationList.replaceChildren(table({
      rows: stationBuilds,
      empty: empty("No station builds published", "Until one is, stations keep the version installed on them.",
        button("Publish station build", { onclick: publishStation })),
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
        button("Publish client build", { kind: "primary", onclick: publish })),
      columns: [
        { label: "Version", value: (b) => [h("span", { class: "mono cell-strong" }, b.version), " ", b === builds[0] && pill("Current", "accent")] },
        { label: "Running it", value: (b) => plural(clients.filter((c) => c.clientVersion === b.version).length, "workstation") },
        { label: "Size", class: "num", value: (b) => bytes(b.sizeBytes) },
        { label: "SHA-256", value: (b) => h("code", { title: b.sha256 }, b.sha256.slice(0, 12) + "…") },
        { label: "Published", value: (b) => h("span", { title: dateTime(b.publishedAt) }, time(b.publishedAt)) },
      ],
    }));
  }

  function publish() {
    formDialog({
      title: "Publish a client build",
      description: "Every signed-in client downloads and installs it on its next heartbeat.",
      submitLabel: "Publish",
      body: [
        field("TapQueueClient.exe", input("file", { type: "file", accept: ".exe", required: true }), "From the client archive (TapQueue_client_<version>_win-x64.zip)."),
        field("Version", input("version", { required: true, placeholder: "e.g. 0.3.0+1a2b3c4" }), "As in the archive's version.txt. Shown on this page and in each client's tray menu."),
      ],
      onSubmit: async ({ file, version }) => {
        if (!file) throw new Error("Choose TapQueueClient.exe.");
        await api.upload(`client-builds?version=${encodeURIComponent(version)}`, file);
        await load();
      },
    });
  }

  function publishStation() {
    formDialog({
      title: "Publish a station build",
      description: "Every station downloads it, checks it runs, installs it and restarts, within about 15 seconds.",
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
}
