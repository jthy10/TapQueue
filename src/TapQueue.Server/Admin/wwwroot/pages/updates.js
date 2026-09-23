import { api } from "../api.js";
import { h, pageHead, panel, button, pill, time, dateTime, bytes, table, empty, field, input, formDialog, loading, plural } from "../ui.js";

export async function render(root, ctx) {
  const list = h("div", null, loading());
  root.append(pageHead("Updates", "Builds the server hands out. Clients compare themselves to the newest one and install it within about a minute.",
    button("Publish client build", { kind: "primary", iconName: "updates", onclick: publish })),
  panel({ title: "Windows client", description: "The newest build is the one every client installs. Publishing an older build again rolls clients back.", flush: true, body: list }),
  panel({ title: "Release stations", body: h("p", { class: "muted", style: "margin:0" },
    "Stations are updated by installing the new tapqueue-station archive on each box for now. Publishing station builds here is on the roadmap.") }));

  async function load() {
    const [builds, clients] = await Promise.all([api.get("client-builds"), api.get("clients")]);
    if (!ctx.current) return;
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
        field("TapQueueClient.exe", input("file", { type: "file", accept: ".exe", required: true }), "From the client archive (tapqueue-client-<version>-win-x64.zip)."),
        field("Version", input("version", { required: true, placeholder: "e.g. 0.3.0+1a2b3c4" }), "As in the archive's version.txt. Shown on this page and in each client's tray menu."),
      ],
      onSubmit: async ({ file, version }) => {
        if (!file) throw new Error("Choose TapQueueClient.exe.");
        await api.upload(`client-builds?version=${encodeURIComponent(version)}`, file);
        await load();
      },
    });
  }

  await load();
}
