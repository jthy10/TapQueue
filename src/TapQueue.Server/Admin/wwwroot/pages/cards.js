import { api, enc } from "../api.js";
import { h, icon, pageHead, panel, button, time, table, empty, field, select, input, formDialog, confirm, attempt, loading, pill } from "../ui.js";

export async function render(root, ctx) {
  const unknownPanel = h("div");
  const list = h("div", null, loading());
  root.append(pageHead("Cards", "Badges linked to people. Tapping one at a station releases its owner's jobs.",
    button("Enroll card", { kind: "primary", iconName: "plus", onclick: () => enrollCard({ onDone: load }) })),
  unknownPanel,
  panel({ flush: true, body: list }));

  async function load() {
    // Cards nobody owns are listed whole, so only those who may enroll them get to see them.
    const [badges, unknown] = await Promise.all([api.get("badges"), ctx.can("people", "operator") ? api.get("badges/unknown") : []]);
    if (!ctx.current) return;

    unknownPanel.replaceChildren(unknown.length ? panel({
      title: "Recently tapped, not linked to anyone",
      description: "Cards tapped at a station in the last hour that nobody owns. Link one to a person to enroll it.",
      flush: true,
      body: table({
        rows: unknown,
        columns: [
          { label: "Card", value: (t) => h("code", null, hint(t.card)) },
          { label: "Station", value: (t) => h("a", { href: `stations/${enc(t.stationId)}` }, t.stationId) },
          { label: "Tapped", value: (t) => time(t.at) },
          { label: "", class: "num", value: (t) => button("Link to person…", { small: true, onclick: () => enrollCard({ card: t.card, onDone: load }) }) },
        ],
      }),
    }) : "");

    list.replaceChildren(table({
      rows: badges,
      search: (b) => `${b.username} ${b.cardHint}`,
      empty: empty("No cards yet", "Enroll one by tapping it at a station.",
        button("Enroll card", { kind: "primary", onclick: () => enrollCard({ onDone: load }) })),
      columns: [
        { label: "Card", value: (b) => h("span", { class: "cell-strong" }, h("code", null, b.cardHint), h("span", { class: "sub" }, `Badge #${b.id}`)) },
        { label: "Person", value: (b) => h("a", { href: `users/${enc(b.username)}` }, b.username) },
        { label: "Label", value: (b) => b.label || h("span", { class: "muted" }, "—") },
        { label: "Enrolled", value: (b) => time(b.createdAt) },
        { label: "Last used", value: (b) => b.lastUsedAt ? time(b.lastUsedAt) : h("span", { class: "muted" }, "Never") },
        { label: "", class: "num nowrap", value: (b) => [
          button("Edit", { small: true, kind: "ghost", onclick: () => editCard(b, load) }),
          button("Remove", { small: true, kind: "ghost danger", onclick: () => removeCard(b, load) }),
        ] },
      ],
    }));
  }

  await load();
  ctx.every(5000, () => { if (!document.querySelector("dialog[open]")) load(); });
}

/** Only the end of a card number is shown, the same way the server stores it. */
export const hint = (card) => (card.length <= 4 ? card : `…${card.slice(-4)}`);

/** Relabel a card, or give it to someone else (a lost card found, a shared spare…). */
export async function editCard(badge, onDone) {
  const users = await api.get("users");
  formDialog({
    title: `Card ${badge.cardHint}`,
    description: "Moving a card takes effect on its next tap.",
    body: [
      field("Belongs to", select("username", users.map((u) => [u.username, `${u.displayName} (${u.username})`]), badge.username)),
      field("Label", input("label", { value: badge.label, placeholder: "e.g. blue fob, spare" }), "A note to tell someone's cards apart."),
    ],
    onSubmit: async (values) => {
      await api.patch(`badges/${badge.id}`, values);
      await onDone?.();
    },
  });
}

export async function removeCard(badge, onDone) {
  if (!await confirm({
    title: `Remove card ${badge.cardHint}?`,
    message: `${badge.username} won't be able to release jobs with it. The card can be enrolled again later.`,
    confirmLabel: "Remove card",
  })) return;
  if (await attempt(() => api.del(`badges/${badge.id}`), "Card removed") !== undefined) await onDone?.();
}

const stationKey = "tapqueue.enroll.station";

/** The station picked the last time a card was enrolled in this browser, if it still exists. */
function rememberedStation(stations) {
  try {
    const id = localStorage.getItem(stationKey);
    return stations.some((s) => s.id === id) ? id : "";
  } catch {
    return "";
  }
}

function rememberStation(id) {
  try {
    localStorage.setItem(stationKey, id);
  } catch {
    // Private windows and locked-down browsers: the choice just isn't remembered.
  }
}

const stationName = (s) => s.name || s.id;

function stationLabel(s) {
  const state = !s.enabled ? " (out of service)" : !s.online ? " (offline)" : "";
  return `${stationName(s)}${s.location ? `, ${s.location}` : ""}${state}`;
}

/**
 * Links a card to a person. With no card given, it waits for one to be tapped (or takes a typed
 * number): at the station picked in the dialog, or at any of them. Pass username to enroll for
 * someone specific.
 */
export async function enrollCard({ username, card, onDone }) {
  const [users, stations] = await Promise.all([api.get("users"), card ? [] : api.get("badges/stations").catch(() => [])]);
  if (users.length === 0) {
    formDialog({ title: "Enroll card", body: h("p", null, "Add a user first, then give them a card."), submitLabel: "OK", onSubmit: () => {} });
    return;
  }
  const opened = new Date();
  const cardInput = input("card", { type: card ? "hidden" : "text", value: card ?? "", placeholder: "Card number as the reader reads it" });
  const where = h("b");
  const waiting = h("div", { class: "tap-wait" }, h("div", { class: "ring" }), where, h("p", { class: "muted", style: "margin:4px 0 0" }, "Waiting for a card nobody owns yet…"));
  const tapped = h("div", { class: "callout info", hidden: true });
  let mode = card ? "given" : "tap";
  let timer;

  // With more than one reader, cards tapped elsewhere in the building mustn't end up on this person.
  const stationSelect = select("station", [["", "Any station"], ...stations.map((s) => [s.id, stationLabel(s)])], rememberedStation(stations));
  const stationField = field("Station", stationSelect, "Where the card will be tapped. Taps at other stations are left alone.");
  stationField.hidden = stations.length < 2;
  const station = () => (stations.length < 2 ? "" : stationSelect.value);
  stationSelect.addEventListener("change", () => {
    rememberStation(stationSelect.value);
    // A card already caught at another station doesn't count for this one.
    if (mode === "tap") { cardInput.value = ""; tapped.hidden = true; }
    update();
  });

  const toggle = h("div", { class: "segmented", style: "margin-bottom:14px" });
  function drawToggle() {
    toggle.replaceChildren(
      h("button", { type: "button", class: mode === "tap" ? "on" : "", onclick: () => { mode = "tap"; update(); } }, "Tap at station"),
      h("button", { type: "button", class: mode === "type" ? "on" : "", onclick: () => { mode = "type"; update(); } }, "Type number"));
  }
  function update() {
    drawToggle();
    if (mode !== "given") cardInput.type = mode === "type" ? "text" : "hidden";
    waiting.hidden = mode !== "tap" || cardInput.value !== "";
    const picked = stations.find((s) => s.id === station());
    where.textContent = picked ? `Tap the card at ${stationName(picked)}` : "Tap the card at any station";
    stationField.hidden = stations.length < 2 || mode !== "tap";
    if (mode === "type") { tapped.hidden = true; cardInput.focus(); }
  }

  async function poll() {
    if (mode !== "tap" || cardInput.value) return;
    const at = station();
    const taps = await api.get(`badges/unknown${at ? `?station=${enc(at)}` : ""}`).catch(() => []);
    // The station may have been changed while that was on its way.
    if (at !== station() || mode !== "tap" || cardInput.value) return;
    const fresh = taps.find((t) => new Date(t.at) >= opened);
    if (!fresh) return;
    cardInput.value = fresh.card;
    tapped.replaceChildren(icon("check"), ` Card ${hint(fresh.card)} tapped at ${fresh.stationId}.`);
    tapped.hidden = false;
    update();
  }

  const dialog = formDialog({
    title: "Enroll card",
    description: username ? `Give ${username} a card for releasing their jobs.` : "Link a badge to a person.",
    submitLabel: "Enroll",
    body: [
      field("Person", select("username", users.map((u) => [u.username, `${u.displayName} (${u.username})`]), username)),
      card ? h("div", { class: "callout info" }, `Card ${hint(card)}`) : [toggle, stationField, waiting, tapped],
      h("div", { class: "field" }, cardInput),
      field("Label", input("label", { placeholder: "Optional, e.g. blue fob" })),
    ],
    onSubmit: async (values) => {
      if (!values.card) throw new Error(mode === "tap" ? "Tap a card at a station first." : "Enter the card number.");
      const badge = await api.post("badges", { username: values.username, card: values.card, label: values.label });
      await onDone?.();
      return h("div", null, h("p", { style: "margin:0" }, "Card ", h("code", null, badge.cardHint), ` now releases ${badge.username}'s jobs.`), pill("Enrolled", "ok"));
    },
  });
  update();
  if (!card) timer = setInterval(poll, 1500);
  dialog.addEventListener("close", () => clearInterval(timer));
}
