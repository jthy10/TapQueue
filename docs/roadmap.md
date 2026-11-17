# Roadmap

What's next for TapQueue, in rough priority order. The [README](../README.md#roadmap) has the short
checklist; [admin-ui.md](admin-ui.md#roadmap) has the admin console's own phases.

## Before a real deployment

1. **Printer health monitoring.** *(Done in 0.6.0, see [how-it-works.md](how-it-works.md#printer-health).)* Poll every printer over IPP for its state, what's wrong
   (jammed, out of paper, door open) and supply levels. Show it in the console and CLI, record
   problems in the activity log, and don't release to a printer that can't print, so jobs stay
   held for another printer instead of disappearing into a stopped one.
2. **Admin sign-in and roles.** *(Done in 0.7.0, see [admin-roles.md](admin-roles.md).)* Viewer,
   operator and admin roles per console area for users and groups, local or from AD, and the
   console available with `auth.mode = "token"`. A new server's first admin is made in the
   console with a setup code (0.19), not by opening the console to the network.
3. **Directory sync.** *(Active Directory done in 0.6.0, see [active-directory.md](active-directory.md);
   Entra ID in 0.19, see [single-sign-on.md](single-sign-on.md#entra-id-sync).)*
   Users and groups from AD, by OU, group (nested) and single user, daily; from Entra ID, by group
   and single user, through Microsoft Graph.
4. **TLS everywhere.** *(Done in 0.8.0, see [tls.md](tls.md).)* IPP, the client API and the admin
   console over HTTPS, so print data and tokens aren't readable on the network.
5. **Per-job identity instead of IP-based ownership.** *(Done in 0.8.0, see
   [how-it-works.md](how-it-works.md#how-a-job-is-matched-to-a-person).)* Each PC gets a key and
   its printers a print key, so the server knows which PC a job came from; the PC's service vouches
   for which PC user runs each tray app, so the job goes to the person who printed it. Works on
   shared PCs, terminal servers and behind NAT. Address matching stays for older clients until
   it's turned off. From 0.19 the PC's service can also sign people in itself, so nobody types a
   token or a password and a job is its PC user's even when their tray app isn't running
   ([how-it-works.md](how-it-works.md#signed-in-by-the-pc)).
6. **Feedback at the station.** *(A screen: done in 0.17.0, see
   [release-station.md](release-station.md#a-screen-at-the-station).)* A tap with nothing to
   print, a refused release and an over-limit user all looked the same: nothing happened. Any
   browser next to the reader can now show what each tap did. Still to do: a USB status light or
   a sound for stations without a screen; the `feedback` station setting is reserved for it.

## What people expect from print management

7. **Follow-me printing.** *(Done: it's how TapQueue has always worked.)* One queue, release at
   any station; the job goes to whichever printer you tap at. A queue can also print straight
   through to one printer (0.19) for the printers that don't need a tap.
8. **Reports.** *(Done in 0.12.0, see [reports.md](reports.md).)* Pages by person, group,
   printer, queue, day and month over any period, in the console and `tapqueue-admin report`, with
   CSV export, and what they cost (0.19). Scheduled email is still to do, with notifications (item 12).
9. **Cost accounting.** *(Done in 0.19, see [accounting.md](accounting.md).)* A price per page
   (black and white, colour, a discount for two-sided) for the server and for each printer, each
   job's cost in reports, and balances people pay from, topped up by an admin. Still to do:
   allowances that arrive by themselves each month, shared or department accounts to charge a job
   to, and people paying in themselves.
10. **Self-service badge enrollment.** *(Done in 0.19.)* Choose the printer you're at in the tray
    or on the web page, tap a card nobody owns, and it's yours.
11. **Release from a phone or the tray.** *(Done: the tray from the start; the web page at /my in
    0.19, see [web-page.md](web-page.md).)* Pick jobs, release them, delete them. Changing copies
    after printing is still to do.

**Queue rules** came with 0.19 too: a queue can always print two-sided, print only in black and
white, and refuse jobs over so many pages; different people get different rules by printing to
different queues. Still to do: rules by group on one queue, and asking "are you sure?" for big
or colour jobs.

## Operations

12. **Notifications.** *(Email done in 0.15.0, see [notifications.md](notifications.md).)* Email
    alerts for printer problems, stations going offline and crash reports, sent through the
    mail server an admin enters in the console. Webhook alerts are still to do.
13. **Backup and restore.** *(Done in 0.11.0, see [backup.md](backup.md).)* A daily backup of the
    database, certificate and AD CA, one before each upgrade, `tapqueue-admin backups` and the
    console's Server page, and `tapqueue-server --restore`.
14. **Packaging.** A Docker image for the server, .deb packages, ARM builds for small release
    stations like a Raspberry Pi, signed Windows builds (no SmartScreen warning), and the server
    on Windows. *(Releases themselves are signed from 0.19, so PCs and stations update without
    reaching GitHub; see [releasing.md](releasing.md#signed-releases).)*
15. **Single sign-on.** *(Done in 0.19 for the admin console and the web page, see
    [single-sign-on.md](single-sign-on.md).)* OpenID Connect with Entra ID, Google, Okta or any
    other provider. Roles belong to users and groups rather than to a way of signing in, so it
    added a sign-in method and changed nothing else. SAML isn't planned.
16. **A macOS client.** Macs can print to a queue today (it's an ordinary IPP printer), but with
    no client there's nothing on the Mac to say whose job it is.
