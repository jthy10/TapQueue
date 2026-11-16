// Cost accounting: what printing costs, and what people have to spend on it.
import { api, enc } from "../api.js";
import { h, pageHead, panel, button, pill, table, empty, drawer, props, sectionTitle, field, input, select, checkbox, checkField,
  formDialog, attempt, loading, toast, time, money, parseMoney, plural } from "../ui.js";

const modes = {
  off: ["Off", "plain", "Printing costs nothing. Page limits (Users and Groups) still apply."],
  track: ["Costs recorded", "accent", "Each job's cost is worked out and kept, for reports and charging departments back. Nobody is refused."],
  enforce: ["Paid from balances", "ok", "Each job's cost comes off the person's balance. A job they can't pay for stays held until they're topped up."],
};
const kinds = { charge: "Printed", refund: "Given back", adjust: "Adjusted" };

export async function render(root, ctx) {
  let settings = null, accounts = [], groups = [];
  const canAdmin = ctx.can("people", "admin");
  const canAdjust = ctx.can("people", "operator");
  const settingsBody = h("div", null, loading());
  const list = h("div", null, loading());
  root.append(
    pageHead("Balances", "What printing costs, and what each person has left to spend on it.",
      canAdmin && button("Top up a group…", { iconName: "plus", onclick: topUpGroup })),
    panel({ title: "Prices", description: "A printer can have its own prices, on the Printers page.",
      actions: canAdmin && button("Edit", { small: true, onclick: editSettings }), body: settingsBody }),
    panel({ flush: true, body: list }));

  const m = (mills) => money(mills, settings.currency);

  async function load() {
    [settings, accounts, groups] = await Promise.all([api.get("accounting"), api.get("accounts"), api.maybe("groups", [])]);
    if (!ctx.current) return;
    const [label, tone, what] = modes[settings.mode] ?? [settings.mode, "plain", ""];
    settingsBody.replaceChildren(props([
      ["Accounting", [pill(label, tone), h("span", { class: "sub" }, what)]],
      settings.mode !== "off" && ["A page costs", [`${m(settings.priceMono)} in black and white, ${m(settings.priceColor)} in colour`,
        h("span", { class: "sub" }, settings.duplexDiscountPercent
          ? `Pages printed on both sides of the paper cost ${settings.duplexDiscountPercent}% less.`
          : "The same whether it's printed on one side of the paper or both.")]],
      settings.mode === "enforce" && ["New users start with", m(settings.initialBalance)],
    ]));
    const enforced = settings.mode === "enforce";
    list.replaceChildren(table({
      rows: accounts,
      search: (a) => `${a.username} ${a.displayName}`,
      onRowClick: (a) => open(a.username),
      empty: empty("No users yet", "Add people on the Users page, or sync them from Active Directory."),
      columns: [
        { label: "User", value: (a) => [h("span", { class: "cell-strong" }, a.displayName || a.username), h("span", { class: "sub" }, a.username)] },
        { label: "Balance", class: "num", value: (a) => h("span", { class: a.balance < 0 ? "bad-text" : "" }, m(a.balance)) },
        { label: "", value: (a) => a.unrestricted ? pill("Unrestricted", "plain")
          : enforced && a.balance <= 0 ? pill("Can't print", "warn") : "" },
      ],
    }));
    if (!enforced)
      list.prepend(h("div", { class: "callout info", style: "margin:12px 16px" },
        settings.mode === "off"
          ? "Balances aren't in use: accounting is off. Turn it on under Prices to charge for printing."
          : "Balances aren't in use: costs are only recorded. Choose \"Paid from balances\" under Prices to have people pay for what they print."));
  }

  async function open(username) {
    ctx.setId(username);
    const a = await attempt(() => api.get(`users/${enc(username)}/account`));
    if (!a) return ctx.setId(null);
    const refresh = async () => { await load(); open(username); };
    drawer({
      title: a.displayName || a.username,
      subtitle: a.username,
      onClose: () => ctx.setId(null),
      body: [
        props([
          ["Balance", h("b", { class: a.balance < 0 ? "bad-text" : "" }, m(a.balance))],
          ["May print with no balance", [a.unrestricted ? "Yes" : "No",
            h("span", { class: "sub" }, a.unrestricted ? "Their balance just goes negative. For staff, typically." : "A job they can't pay for stays held.")]],
        ]),
        sectionTitle("What changed it"),
        a.transactions.length ? table({
          rows: a.transactions,
          columns: [
            { label: "When", value: (t) => time(t.at) },
            { label: "", value: (t) => [kinds[t.kind] ?? t.kind, t.note && h("span", { class: "sub" }, t.note), t.kind === "adjust" && h("span", { class: "sub" }, `by ${t.actor}`)] },
            { label: "Amount", class: "num", value: (t) => h("span", { class: t.amount < 0 ? "" : "ok-text" }, `${t.amount > 0 ? "+" : ""}${m(t.amount)}`) },
            { label: "Balance", class: "num", value: (t) => m(t.balanceAfter) },
          ],
        }) : h("p", { class: "muted" }, "Nothing yet."),
      ],
      footer: [
        canAdmin && button(a.unrestricted ? "Restrict to balance" : "Let them print without one", { onclick: async () => {
          if (await attempt(() => api.put(`users/${enc(a.username)}/unrestricted`, { unrestricted: !a.unrestricted }))) refresh();
        } }),
        canAdjust && button("Adjust balance…", { kind: "primary", onclick: () => adjust(a, refresh) }),
      ],
    });
  }

  function adjust(a, done) {
    formDialog({
      title: `Adjust ${a.displayName || a.username}'s balance`,
      description: `It's ${m(a.balance)} now.`,
      submitLabel: "Save",
      body: [
        field("Do what", select("how", [["add", "Add to it"], ["take", "Take from it"], ["set", "Set it to"]], "add")),
        field(`Amount (${settings.currency})`, input("amount", { required: true, inputmode: "decimal", placeholder: "5.00" })),
        field("Note", input("note", { placeholder: "e.g. paid at the front desk", maxlength: 200 }), "Kept with the change, and shown to them."),
      ],
      onSubmit: async (v) => {
        const amount = parseMoney(v.amount);
        if (amount === null || amount < 0) throw new Error("Enter an amount like 5 or 2.50.");
        await api.post(`users/${enc(a.username)}/balance`, { amount: v.how === "take" ? -amount : amount, note: v.note, set: v.how === "set" });
        toast("Balance changed");
        done();
      },
    });
  }

  function topUpGroup() {
    if (!groups.length) return toast("There are no groups yet. Add one on the Groups page.", "bad");
    formDialog({
      title: "Top up a group",
      description: "Adds the same amount to the balance of everyone in the group who isn't disabled: a term's allowance, say.",
      submitLabel: "Add to each",
      body: [
        field("Group", select("group", groups.map((g) => [g.id, `${g.name} (${plural(g.memberCount, "member")})`]), groups[0].id)),
        field(`Amount each (${settings.currency})`, input("amount", { required: true, inputmode: "decimal", placeholder: "5.00" }), "A minus sign takes it away instead."),
        field("Note", input("note", { placeholder: "e.g. autumn term", maxlength: 200 })),
      ],
      onSubmit: async (v) => {
        const amount = parseMoney(v.amount);
        if (!amount) throw new Error("Enter an amount like 5 or 2.50.");
        const done = await api.post(`groups/${enc(v.group)}/balance`, { amount, note: v.note });
        toast(`Changed ${plural(done.users, "balance")}`);
        load();
      },
    });
  }

  function editSettings() {
    const price = (mills) => (mills / 1000).toFixed(mills % 10 ? 3 : 2);
    formDialog({
      title: "Prices",
      body: [
        field("Accounting", select("mode", Object.entries(modes).map(([v, [label]]) => [v, label]), settings.mode),
          "Off: printing costs nothing. Costs recorded: for reports, nobody is refused. Paid from balances: a job someone can't pay for stays held."),
        field("Currency", input("currency", { required: true, maxlength: 5, value: settings.currency }), "A symbol or a few letters: $, €, £, kr."),
        field("A page in black and white costs", input("priceMono", { required: true, inputmode: "decimal", value: price(settings.priceMono) }),
          "Fractions of a cent are fine: 0.035."),
        field("A page in colour costs", input("priceColor", { required: true, inputmode: "decimal", value: price(settings.priceColor) })),
        field("Two-sided pages cost less by (%)", input("duplexDiscountPercent", { type: "number", min: 0, max: 100, required: true, value: settings.duplexDiscountPercent }),
          "0 for the same price either way."),
        field("New users start with", input("initialBalance", { required: true, inputmode: "decimal", value: price(settings.initialBalance) }),
          "Only for people added from now on. Use Top up a group for the ones already here."),
      ],
      onSubmit: async (v) => {
        const [priceMono, priceColor, initialBalance] = [v.priceMono, v.priceColor, v.initialBalance].map(parseMoney);
        if ([priceMono, priceColor, initialBalance].some((n) => n === null || n < 0)) throw new Error("Amounts look like 0.05 or 2.50.");
        await api.patch("accounting", { mode: v.mode, currency: v.currency, priceMono, priceColor, duplexDiscountPercent: Number(v.duplexDiscountPercent), initialBalance });
        toast("Saved");
        load();
      },
    });
  }

  await load();
  if (ctx.id) open(ctx.id);
}
