# Roadmap

What's next for TapQueue, in rough priority order. The [README](../README.md#roadmap) has the short
checklist; [admin-ui.md](admin-ui.md#roadmap) has the admin console's own phases.

## Before a real deployment

1. **Printer health monitoring.** Poll every printer over IPP for its state, what's wrong
   (jammed, out of paper, door open) and supply levels. Show it in the console and CLI, record
   problems in the activity log, and don't release to a printer that can't print, so jobs stay
   held for another printer instead of disappearing into a stopped one.
2. **Admin sign-in and roles.** Admin accounts (Admin, Operator, Viewer) and the console
   available with `auth.mode = "token"`. Phase 7 in [admin-ui.md](admin-ui.md#roadmap).
3. **Directory sync.** Users and groups from Active Directory / LDAP / Entra ID. Users already
   have `source` and `external_id` for this.
4. **TLS everywhere.** IPP, the client API and the admin console over HTTPS, so print data and
   tokens aren't readable on the network.
5. **Per-job identity instead of IP-based ownership.** Jobs are matched to a person by the PC's
   address today ([how-it-works.md](how-it-works.md)), which breaks on shared PCs, terminal
   servers and behind NAT. Per-job credentials (IPP authentication, or a token the client adds)
   fix it, and belong with the auth and TLS work.
6. **Feedback at the station.** A tap with nothing to print, a refused release and an
   over-limit user all look the same today: nothing happens. A USB status light or a small
   kiosk screen; the `feedback` station setting is reserved for it.

## What people expect from print management

7. **Follow-me printing.** One queue, release at any station; the job goes to whichever
   printer you tap at.
8. **Reports.** Pages by user, group, printer and period, with CSV export and scheduled email.
   Page counts already exist for quotas.
9. **Cost accounting.** Per-page prices (mono/colour, simplex/duplex), balances or charge-back
   to departments.
10. **Self-service badge enrollment.** Tap an unknown badge, confirm it from the tray app.
11. **Release from a phone or the tray.** Pick jobs, delete them, change copies, for the day the
    badge is at home.

## Operations

12. **Notifications.** Email or webhook alerts for printer problems, stations going offline and
    crash reports.
13. **Backup and restore** of the database: a CLI command and a scheduled backup.
14. **Packaging.** Signed Windows builds (no SmartScreen warning), .deb packages, ARM builds for
    small release stations like a Raspberry Pi.
