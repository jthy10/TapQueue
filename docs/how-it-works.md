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
    │           │          └─(printer refused / unreachable)──▶ held, with the error
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

## How a job is matched to a person

IPP jobs carry a username (`requesting-user-name`), but it's just a string the sending computer
fills in, and anyone can put anything there. TapQueue doesn't trust it on its own.

Instead, the **tray app signs in** with the user's token and keeps a session open with a heartbeat.
When a job arrives, the server looks at the **IP address it came from** and finds the signed-in
session(s) on that address:

| Signed-in users at that address | Result |
|---|---|
| exactly one | The job is theirs. |
| several (e.g. a terminal server) | The one whose Windows username matches the job's username. |
| none | The job is held with no owner. With `auth.mode = "dev"` only, the job's username is trusted. |

Jobs with no owner show up in `tapqueue-admin jobs` as "unowned" and expire normally. The logic
lives in [`JobOwnerResolver`](../src/TapQueue.Server/Jobs/JobOwnerResolver.cs).

### Limits of matching by address

Matching by address works when each PC reaches the server from its own IP, which is true on a normal
office LAN. It breaks down when that isn't the case:

- **NAT between clients and the server** (a VPN concentrator, a remote site behind one public IP,
  a guest network): many PCs share one address. Jobs are matched by Windows username among the
  users signed in behind that address, so two people with the same Windows username behind the same
  NAT can't be told apart.
- **Terminal servers / VDI hosts**: every session shares the host's IP. Matching falls back to
  the Windows username, which works as long as each user runs the tray app in their own session.
- **The tray app isn't running** (not started, crashed, not installed): the job has no owner.
- **Someone sets `requesting-user-name` by hand on a shared host** and happens to match another
  signed-in user's Windows username: they can put a job in that user's queue. They can't release
  or read anyone else's jobs, though.

Proper per-job authentication (IPP over TLS with each user's own credentials) is on the roadmap
and would replace address matching. In the meantime, the resolver is the only place that decides
ownership, so it can be swapped out without touching the rest of the server.

## Security notes

- TLS isn't implemented yet. Client, station and admin traffic, including tokens and documents,
  crosses the network in the clear. Run TapQueue on a network you trust until TLS lands.
- Tokens (user, station, session) are stored hashed. Badge numbers are stored hashed, with only
  the last four characters kept so admins can tell badges apart.
- Held documents live in `/var/lib/tapqueue/spool`, readable only by the `tapqueue` user.
- Card numbers from cheap 125 kHz and MIFARE readers are easy to copy. A tap means "this person is
  standing at the printer", nothing stronger.
- `auth.mode = "dev"` lets anyone sign in as anyone. It's for testing.
