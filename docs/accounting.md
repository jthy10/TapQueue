# Cost accounting and balances

TapQueue can put a price on every page, record what each job cost, and have people pay for what
they print from a balance. It's off until you turn it on, and page limits
([admin-cli.md](admin-cli.md#page-limits)) work with or without it.

## Three modes

| Mode | What happens |
|---|---|
| **Off** (the default) | Printing costs nothing. Jobs have no cost. |
| **Costs recorded** (`track`) | Each job's cost is worked out and kept when it's released, for [reports](reports.md) and charging departments back. Nobody is refused. |
| **Paid from balances** (`enforce`) | The cost also comes off the person's balance. A job they can't pay for stays held, and the tray, the web page and the station's screen say why. |

Set it on the console's **Balances** page under **Prices**, or:

```sh
sudo tapqueue-admin accounting                       # what's set
sudo tapqueue-admin accounting set mode enforce      # off, track or enforce
sudo tapqueue-admin accounting set currency "$"      # a symbol, or letters like kr
```

## Prices

A price is for **one page** (one side of a sheet), in black and white or in colour. Fractions of
a cent are fine: `0.035`.

```sh
sudo tapqueue-admin accounting set price-mono 0.05
sudo tapqueue-admin accounting set price-color 0.20
sudo tapqueue-admin accounting set duplex-discount 25     # two-sided pages cost 25% less
```

Those are the server's prices. A printer can have its own (a big colour printer that's cheaper to
run, say), on its **Edit** dialog in the console or:

```sh
sudo tapqueue-admin printers edit colour-mfp --price-mono 0.04 --price-color 0.15
sudo tapqueue-admin printers edit colour-mfp --price-color default     # back to the server's
```

A job's cost is worked out **when it's released**, for the printer it's released at:

    pages × copies × (the colour price if it prints in colour, otherwise the black and white price)
    less the two-sided discount if it prints on both sides

- It's priced as it will actually come out. A colour job released at a printer that can't print
  colour costs the black and white price; a job from a queue that doesn't offer colour is always
  black and white ([how-it-works.md](how-it-works.md#what-a-queue-offers)).
- A job whose pages couldn't be counted costs one page a copy, as it counts against a page limit.
- A job that asked for neither colour nor black and white (it left the choice to the printer)
  costs the black and white price. Make a queue offer colour only if its printers should print it.

## Balances

With **Paid from balances**, everyone has a balance.

```sh
sudo tapqueue-admin balances                              # everyone's
sudo tapqueue-admin users balance jsmith                  # one person's, and what changed it
sudo tapqueue-admin users balance jsmith +5.00 --note "paid at the front desk"
sudo tapqueue-admin users balance jsmith -2.50
sudo tapqueue-admin users balance jsmith =10.00           # set it
sudo tapqueue-admin groups balance students +5.00 --note "autumn term"   # everyone in a group
sudo tapqueue-admin accounting set initial-balance 2.00   # what new users start with
```

The console's **Balances** page does the same: click someone to see every change to their
balance, **Adjust balance** to add, take or set, and **Top up a group** to give everyone in a
group the same amount at once.

- **Topping up is operator work** in the People area, so a help desk can do it without being able
  to change prices. Prices, the mode and topping up a whole group need a People admin.
- **Unrestricted** people (staff, typically) print with nothing on their balance; it just goes
  negative, so their printing is still accounted for. `tapqueue-admin users unrestricted jsmith on`,
  or the button on their balance.
- Every change is a line in the person's history with the balance it left: what they printed and
  where, what was given back, and what an admin added or took, by whom and with what note.
- People see their own balance in the tray menu, on the [web page](web-page.md) with its history
  and what a page costs at each printer, and on the station's screen after a tap ("That's $0.15;
  you have $4.85 left.").

## Money back

Pages that don't print aren't paid for, by the same rules that give them back to a page limit
([how-it-works.md](how-it-works.md#a-jobs-life)):

- A job stopped at the printer before it printed is paid back in full.
- One stopped part way is paid back for the pages that didn't print, when the printer says how
  many did.
- A job the printer gave up on goes back on hold and costs nothing until it's released again.

## Reports

With costs recorded or paid, [reports](reports.md) add a **Cost** column: by person, group,
printer, queue, day or month, in the console, `tapqueue-admin report` and the CSV (as a plain
number, with the currency in the heading). Jobs printed while accounting was off cost nothing.

## Limits

- Amounts are kept in thousandths of the currency's unit, so prices can't be finer than 0.001.
- There's no scheduled allowance yet (so much every month by itself); use **Top up a group** at
  the start of a term, or a page limit, which does reset by itself.
- People can't add money themselves; an admin or operator tops them up. There are no shared or
  department accounts to charge a job to; the group report shows what each group's people spent.
- Like page counts, costs are for accounting among your own users. Someone who edits their PDFs
  by hand can make a long job count as a short one; the printer's own counter is the check.
