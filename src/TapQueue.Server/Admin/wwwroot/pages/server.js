import { api } from "../api.js";
import { h, pageHead, panel, pill, props, dateTime, duration, plural } from "../ui.js";

export async function render(root, ctx) {
  const s = await api.get("server");
  if (!ctx.current) return;
  root.append(pageHead("Server", "The TapQueue server this console is running on.",
    h("a", { class: "btn", href: "/healthz", target: "_blank" }, "Health check")),
  h("div", { class: "grid two" },
    panel({ title: "Status", body: props([
      ["Version", h("span", { class: "mono" }, s.version)],
      ["Running since", [dateTime(s.startedAt), h("span", { class: "sub" }, `Up ${duration(s.startedAt)}`)]],
      ["Listening on", h("code", null, s.listen)],
      ["Data", h("code", null, s.dataDir)],
      ["Database schema", `Version ${s.schemaVersion}`],
      ["Held jobs", s.heldJobs],
    ]) }),
    panel({ title: "Settings", description: "From /etc/tapqueue/server.toml. Editing them here is on the roadmap; for now change the file and restart tapqueue-server.",
      body: props([
        ["Client sign-in", s.authMode === "dev"
          ? [pill("Dev", "bad"), h("span", { class: "sub" }, "Anyone can sign in as any username, and this console is open.")]
          : [pill("Tokens", "ok"), h("span", { class: "sub" }, "Clients need the token from Users.")]],
        ["Jobs kept for", [plural(s.holdHours, "hour"), h("span", { class: "sub" }, "Held jobs nobody releases are deleted after this.")]],
        ["Session timeout", [plural(s.sessionTimeoutMinutes, "minute"), h("span", { class: "sub" }, "A client that stops checking in is signed out after this.")]],
      ]) }),
  ));
}
