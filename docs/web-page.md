# The web page for people's own jobs

Everyone who prints with TapQueue has a page at `https://<server>:8632/my`. From any browser, a
phone included, they sign in and:

- see what's **waiting to print**, release it at a printer they choose (all of it, or the jobs
  they tick), and delete what they no longer want;
- see what they printed lately, and stop a job that's still printing;
- see their **balance**, what changed it and what a page costs at each printer, where printing is
  [paid for](accounting.md), and how much of their **page limit** is left;
- **link a badge** by tapping it at a printer, and remove one they've lost;
- change their TapQueue password, if they have one.

It's for the day the badge is at home, for people without the tray app, and for releasing from
the printer's side of the room. It only ever shows someone their own jobs.

## Signing in

The same name and password as everywhere else:

- **Active Directory users**: their domain name and password, checked against AD.
- **Local users**: their TapQueue password. Set one on their page in the console (**Users** →
  the person → **Set password**) or with `tapqueue-admin users password <username>`. A People
  admin can set anyone's but an admin's; an admin's password takes a full admin.
- **Single sign-on**: where it's set up, the page offers **Sign in with …**
  ([single-sign-on.md](single-sign-on.md)). That's the way in for Entra ID users.

Wrong passwords are slowed down the same way as for the console
([admin-roles.md](admin-roles.md#signing-in)). A sign-in lasts until it hasn't been used for 7
days, the person signs out, changes their password or is disabled.

## What it isn't

**Signing in to the page doesn't make jobs yours.** Which jobs are whose is decided where they're
printed: by the TapQueue service and tray app on the PC
([how-it-works.md](how-it-works.md#how-a-job-is-matched-to-a-person)). The page is for what
happens to a job afterwards. Someone signed in on a phone isn't "at" any PC, and a job sent from
that phone's address isn't matched to them because of it.

## Turning it off

It's on by default. **Server → Settings → Web page for people's own jobs**, or:

```sh
sudo tapqueue-admin server set user-portal off
```

With it off, `/my` says so and its API answers 404.

## For admins

- Give people the address. `https://` matters: over plain HTTP from another machine the page
  redirects to HTTPS when the server has it ([tls.md](tls.md)). With the server's self-signed
  certificate, browsers warn first; a certificate from your own CA or Let's Encrypt avoids that,
  and is worth it for a page everyone opens.
- Releasing from the page goes through the same checks as a tap: page limits, balances, which
  printers the person's groups allow, whether the printer can print.
- Sign-ins and releases from the page are in the activity log ("from the web page").
- The page is plain HTML, CSS and one script in `src/TapQueue.Server/Portal/wwwroot`, embedded in
  the server like the console. It talks to `/api/v1/portal`, which is the tray app's own
  endpoints behind a cookie instead of a session token; a change made with the cookie needs the
  `X-TapQueue-Portal` header, so another website can't release or delete someone's jobs.
