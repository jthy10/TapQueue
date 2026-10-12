# Notifications

The server can email people when something needs a look:

| What | When | Then |
|---|---|---|
| **Printer problems** | A printer is unreachable, or says something a person has to fix: paper jam, out of paper, door open. | Another email when it's ready again. |
| **Printer warnings** (off unless you ask) | A printer still prints but wants attention soon: toner low, paper low. | Another email if it gets worse, or better. |
| **Stations going offline** | A release station stops checking in, so cards tapped there do nothing. | Another email when it's back. |
| **Crash reports** | The tray app or the TapQueue service on a PC crashed and sent a report. | At most one email an hour for each PC and program. |

The emails go out through **your own mail server or provider**. TapQueue doesn't run a mail
service and nothing is sent anywhere else: you enter the SMTP settings your provider gives you,
the same ones a copier's scan-to-email or any other program would use.

## Setting it up

In the console, open **System → Notifications** (the Server area; see
[admin-roles.md](admin-roles.md)).

1. **Mail server → Edit.** Enter the server, port and kind of encryption, the username and
   password if it asks for them, and the address the emails come from.
2. **Alerts → Edit.** One address per line for who gets the emails (a shared inbox or mailing
   list works well), what to email about, and how long to wait.
3. **Send test email.** It goes to everyone on the list. If it doesn't arrive, the console says
   what the mail server answered.
4. **Turn on.**

| Setting | What to enter |
|---|---|
| Server, port | From your provider. Usually port 587 with STARTTLS, or 465 with TLS. |
| Encryption | **STARTTLS** or **TLS** whenever a password is involved. With STARTTLS the server refuses to go on if the mail server doesn't offer it, so the password is never sent readable. **None** is for a relay on the same machine or a network you trust. |
| Accept any certificate | Only for a relay of your own with a self-signed certificate. |
| Username, password | The account that sends. Many providers want an *app password* made for TapQueue rather than the account's own. Leave the username empty for a relay that doesn't ask anyone to sign in. |
| Send from, Shown as | The address and name in the From line. Most providers only take an address that belongs to the account. |
| Wait | How long a printer or station must stay wrong before an email goes out, 5 minutes unless changed. One that recovers sooner sends nothing. 0 sends straight away. |

Some common providers:

| Provider | Server | Port | Sign in with |
|---|---|---|---|
| Gmail, Google Workspace | `smtp.gmail.com` | 587, STARTTLS | The address and an [app password](https://support.google.com/accounts/answer/185833) (needs 2-Step Verification) |
| Fastmail | `smtp.fastmail.com` | 465, TLS | The address and an app password |
| Amazon SES | `email-smtp.<region>.amazonaws.com` | 587, STARTTLS | SMTP credentials made in the SES console |
| Mailgun, SendGrid, Postmark, Brevo… | From their SMTP settings page | 587, STARTTLS | The SMTP username and password or API key they give you |
| A relay on your network (Postfix, Exchange connector) | Its name or address | 25, none | Usually nobody |

TapQueue signs in with a username and password (SMTP `AUTH PLAIN` or `LOGIN`). It doesn't do
OAuth. Microsoft 365 is turning username-and-password sign-in off for SMTP; if your tenant no
longer takes it, send through a relay or connector on your network, or one of the sending
services above.

## What an email looks like

```
Subject: TapQueue: Front desk printer is unreachable

Front desk printer is unreachable
  Where: 2nd floor
  Since: 2026-10-12 09:14
  Why: Connection refused (10.0.0.180:631)
  Jobs released to it stay on hold until it's fixed.
```

Everything found in one look (the server looks every 30 seconds) goes out as one email, so a
switch that takes five printers with it sends one email listing five, not five emails. Times are
on the server's clock.

## When an email can't be sent

The console's Notifications page shows why the last one failed, and the activity log records it
once. The email is kept and tried again, after a minute, then two, four and so on up to every half
hour, for a day. Emails still waiting when the server restarts are lost; what's still wrong is on
the console's Overview either way.

A printer or station that an email went out about is listed under **Wrong right now** until it
recovers. That list survives restarts, so restarting or upgrading the server doesn't send the
same alert again.

## From the command line

```sh
tapqueue-admin notifications        # the settings, the last email and what's wrong right now
tapqueue-admin notifications test   # send a test email to everyone on the list
tapqueue-admin notifications off    # or on
```

The mail server and its password are set in the console.

## The password

The mail account's password is kept in the server's database, like the Active Directory bind
password, because the server has to send it every time. It's never shown again, not in the console
and not through the API, and it's in [backups](backup.md). Changing the mail server, its port or
the kind of encryption takes the password again, so nobody can have the saved one sent somewhere
else. Use an account or app password that can only send mail.
