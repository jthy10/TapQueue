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
   console available with `auth.mode = "token"`. Single sign-on is item 15.
3. **Directory sync.** *(Active Directory done in 0.6.0, see [active-directory.md](active-directory.md).)*
   Users and groups from AD, by OU, group (nested) and single user, daily. Entra ID is still to do.
4. **TLS everywhere.** *(Done in 0.8.0, see [tls.md](tls.md).)* IPP, the client API and the admin
   console over HTTPS, so print data and tokens aren't readable on the network.
5. **Per-job identity instead of IP-based ownership.** *(Done in 0.8.0, see
   [how-it-works.md](how-it-works.md#how-a-job-is-matched-to-a-person).)* Each PC gets a key and
   its printers a print key, so the server knows which PC a job came from; the PC's service vouches
   for which PC user runs each tray app, so the job goes to the person who printed it. Works on
   shared PCs, terminal servers and behind NAT. Address matching stays for older clients until
   it's turned off.
6. **Feedback at the station.** *(A screen: done in 0.17.0, see
   [release-station.md](release-station.md#a-screen-at-the-station).)* A tap with nothing to
   print, a refused release and an over-limit user all looked the same: nothing happened. Any
   browser next to the reader can now show what each tap did. Still to do: a USB status light or
   a sound for stations without a screen; the `feedback` station setting is reserved for it.

## What people expect from print management

7. **Follow-me printing.** One queue, release at any station; the job goes to whichever
   printer you tap at.
8. **Reports.** *(Done in 0.12.0, see [reports.md](reports.md).)* Pages by person, group,
   printer, queue, day and month over any period, in the console and `tapqueue-admin report`, with
   CSV export. Scheduled email is still to do, with notifications (item 12).
9. **Cost accounting.** Per-page prices (mono/colour, simplex/duplex), balances or charge-back
   to departments. *(From 0.16.0 each job records whether it was sent in colour and on both
   sides, and reports add those up; prices are still to do.)*
10. **Self-service badge enrollment.** Tap an unknown badge, confirm it from the tray app.
11. **Release from a phone or the tray.** Pick jobs, delete them, change copies, for the day the
    badge is at home.

## Operations

12. **Notifications.** *(Email done in 0.15.0, see [notifications.md](notifications.md).)* Email
    alerts for printer problems, stations going offline and crash reports, sent through the
    mail server an admin enters in the console. Webhook alerts are still to do.
13. **Backup and restore.** *(Done in 0.11.0, see [backup.md](backup.md).)* A daily backup of the
    database, certificate and AD CA, one before each upgrade, `tapqueue-admin backups` and the
    console's Server page, and `tapqueue-server --restore`.
14. **Packaging.** Signed Windows builds (no SmartScreen warning), .deb packages, ARM builds for
    small release stations like a Raspberry Pi.
15. **Single sign-on** for the admin console (and maybe the tray): OIDC with Entra ID, Google or
    Okta, or SAML. Roles already belong to users and groups rather than to a way of signing in,
    so this adds a sign-in method and changes nothing else.
