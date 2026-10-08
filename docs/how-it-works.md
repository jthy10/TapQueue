# How TapQueue works

## The pieces

- **Queues** are what users print to. Each one looks like an ordinary
  [IPP Everywhere](https://www.pwg.org/ipp/everywhere.html) network printer at
  `ipp://<server>:8631/ipp/<queue-id>`, so Windows 11 prints to it with its built-in class driver.
  You choose the name users see in the print dialog.
- **Printers** are the physical printers jobs are released to, over IPP.
- **Users** have a client token for the tray app and any number of **badges**.
- **Release stations** are badge readers. Each one releases to one printer.

## A job's life

```
receiving ──▶ held ──▶ releasing ──▶ released
    │           │          │
    │           │          └─(printer refused, unreachable or stopped)──▶ held, with the error
    │           ├─(user deletes it)──▶ canceled
    │           └─(hold_hours pass)──▶ expired
    └─(client cancels, or still arriving after an hour)──▶ canceled
```

- The server stores the document (usually PDF) together with the options chosen in the print dialog
  (copies, page ranges, orientation…) and replays them when it releases the job.
- As soon as a job has fully arrived, the server tells Windows it's **completed**. From Windows'
  point of view it has been delivered, so it doesn't sit in the user's local print queue for hours.
- Releasing sends jobs oldest first. If a printer answers "busy", the server retries for up to two
  minutes. A job that can't be sent goes back to **held**, so the user can try another printer.
- The document is deleted from disk when the job is released, canceled or expires.

## Printer health

The server asks every printer for its status over IPP (Get-Printer-Attributes) when it starts
and every 30 seconds after that, and again whenever someone presses **Check now** or releases to
a printer last seen down. From the answer it works out:

| Health | Means | Releases |
|---|---|---|
| **ok** | Ready or printing, nothing reported | Go ahead |
| **warning** | Something to see to soon: toner or paper low, an unfamiliar status | Go ahead |
| **error** | A real problem: paper jam, out of paper, door open, a supply empty | Go ahead unless the printer has stopped |
| **offline** | Didn't answer | Held back |

A printer that has **stopped** (`printer-state` is stopped) or says it isn't accepting jobs
counts as unable to print. Releasing to it, from a station, the tray app or the console, refuses
straight away with the reason ("Office printer can't print right now (paper jam). Your jobs are
still held.") instead of sending jobs it won't print. Before refusing, the server checks the
printer once more in case it was just fixed.

Supply levels come from the printer's `marker-*` attributes (toner or ink name, colour, level
and its own "low" mark). Printers that don't report them just show no supplies.

Every change in a printer's problems (it goes offline, jams, runs low, is ready again) is
recorded in the activity log under **Printers**, logged by the server, and listed under
**Needs attention** on the console's Overview. `tapqueue-admin printers` shows the same.

## How a job is matched to a person

IPP jobs carry a username (`requesting-user-name`), but it's just a string the sending computer
fills in, and anyone can put anything there. TapQueue doesn't trust it on its own.

### By PC and PC user (clients 0.8 and later)

1. **The PC gets a key.** The first time the TapQueue service on a PC checks in, the server gives
   that PC a key. The service keeps it in `workstation-key.json` (in `%ProgramData%\TapQueue` on
   Windows, `/var/lib/tapqueue-client` on Linux), readable only by SYSTEM and administrators, or
   root. From then on the server only accepts check-ins for that computer name with that key.
2. **Its printers carry a print key.** The service adds the PC's printers with a path like
   `/ipp/secure/pc/<print key>`. The print key is made from the PC's key but can't be turned back
   into it, so although anyone on the PC can read a printer's path, it only tells the server which
   PC a job came from, whatever address the job arrives from.
3. **The service vouches for each tray app.** When someone's tray app signs in, it asks the
   service on the same PC (over the local pipe or socket it already uses for "Check for updates")
   to vouch for its session. The service asks the operating system which PC user is on the other
   end (Windows tells it the account behind the pipe; Linux the user behind the socket), and tells
   the server, with the PC's key: this session belongs to PC user `CORP\jake` on this PC. The tray
   app's own word for who it runs as isn't used.
4. **A job goes to the session of the PC user who printed it.** Windows' spooler and CUPS fill in
   the job's username with the PC user who printed. The server looks among the sessions this PC
   vouched for and gives the job to the one whose PC user matches (`CORP\jake`, `jake` and
   `jake@corp.local` all count as `jake`).

| Signed-in sessions this PC vouched for | Result |
|---|---|
| one for the PC user who printed | The job is theirs. |
| none for that PC user (even if someone else is signed in there) | The job is held with no owner. |
| the job has no username at all | It goes to the one person signed in there, if there's exactly one. |

This works the same on a terminal server with dozens of people, on PCs behind NAT or a VPN, and
on PCs whose address changes: neither the address nor the username alone decides anything.

### By address (keyless jobs)

Printers that clients older than 0.8 added, and jobs sent straight to the server, have no print
key. While **Jobs without a PC key** (Server page, or `tapqueue-admin server set address-matching`)
is on, the default, they're matched the old way: the server finds the signed-in session(s) at the
**IP address the job came from**:

| Signed-in users at that address | Result |
|---|---|
| exactly one | The job is theirs. |
| several (e.g. a terminal server) | The one whose Windows username matches the job's username. |
| none | The job is held with no owner. With `auth.mode = "dev"` only, the job's username is trusted. |

That breaks down behind NAT (many PCs share an address), on terminal servers (matching falls back
to a username the tray app reports itself) and when someone sets `requesting-user-name` by hand.
Once every PC under **Workstations** shows a key, turn address matching off: keyless jobs are then
held with no owner.

### Moving over

Nothing to do but update. A PC's service gets its key at its first check-in on client 0.8 with a
0.8 server, re-adds its printers with the print key (once, like **Refresh printers**), and tray
apps are vouched for at their next sign-in or heartbeat. Jobs already held keep their owner.

- A PC that was **reinstalled** loses its key, and its service is then turned away ("already has a
  TapQueue key"). Forget it under **Workstations** (or `tapqueue-admin workstations forget`); it
  gets a new key at its next check-in. Forgetting a PC also stops its old printers' jobs from being
  accepted until the service has re-added them.
- Going back to a client older than 0.8 on a PC that has a key needs the same: forget the PC.

### Limits

- **The tray app isn't running** (not started, crashed, not installed): that PC user's jobs have no
  owner.
- **Anyone on a PC can print into another signed-in PC user's queue there**, by sending a job
  straight to the server with that PC's print key (readable from the printer) and the other
  person's username. They can't release or read anyone else's jobs, and can't do it from another PC.
- Until [TLS](roadmap.md) is in, keys and print keys cross the network in the clear like
  everything else.

Jobs with no owner show up in `tapqueue-admin jobs` as "unowned" and expire normally. The logic
lives in [`JobOwnerResolver`](../src/TapQueue.Server/Jobs/JobOwnerResolver.cs), the only place that
decides ownership.

## Security notes

- Clients, stations and the admin console reach the server over TLS (`https://` and `ipps://` on
  port 8632), with a self-signed certificate they pin the first time they connect, or your own;
  see [TLS](tls.md). Plain HTTP on port 8631 still works until `tls.require` is on, and anything
  sent that way, tokens and documents included, crosses the network in the clear.
- Releasing to a printer uses whatever its `uri` says: `ipps://` encrypts it, and
  `--tls-skip-verify` accepts the printer's own self-signed certificate.
- Tokens (user, station, session) are stored hashed. Badge numbers are stored hashed, with only
  the last four characters kept so admins can tell badges apart.
- Held documents live in `/var/lib/tapqueue/spool`, readable only by the `tapqueue` user.
- Card numbers from cheap 125 kHz and MIFARE readers are easy to copy. A tap means "this person is
  standing at the printer", nothing stronger.
- `auth.mode = "dev"` lets anyone sign in as anyone. It's for testing.
