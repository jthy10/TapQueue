# Admin sign-in and roles

Who can use the admin console, and what each person may do there.

Admins are ordinary TapQueue users, local or from Active Directory. A **role** gives a user, or
everyone in a group, access to one **area** of the console, or to all of them. Someone with roles
from several places gets the highest one per area.

## Setting up a new server

1. Install the server. The install script ends with a **setup code**, like `K7QM-2XWD-9RTB`.
2. Open `https://<server>:8632/admin`. A server that nobody can sign in to yet shows **Set up
   this server** instead of a sign-in: enter the setup code, choose a username and a password,
   and you're signed in as its first full admin.
3. Add the people who will run TapQueue: users on the **Users** page, or through the
   [Active Directory](active-directory.md) or [Entra ID](single-sign-on.md#entra-id-sync) sync.
4. On **Admins**, give them roles. A group works too: with a directory, give `Print-Admins` (or
   whatever yours is called) the role, and whoever is in it there is an admin.
5. Make sure they can sign in:
   - A **local user** needs a TapQueue password. Set it on their user page, or with
     `tapqueue-admin users password <username>`.
   - An **AD user** signs in with their domain password. There's nothing to set up beyond the AD
     connection.
   - With [single sign-on](single-sign-on.md), anyone the provider vouches for, AD or Entra ID.

   The Admins page warns you while nobody can sign in as a full admin.

The setup code is made when the server starts, kept only in memory, and works only while nobody
could sign in as a full admin. It's in the server's log (`journalctl -u tapqueue-server`), and
`sudo tapqueue-admin server setup-code` shows it again; both need root on the server, which could
do anything anyway. Five wrong codes from one address lock that address out for a while.

`auth.mode = "dev"` still exists for trying TapQueue out: the console is then open to anyone on
the network with no sign-in at all. A real server never needs it.

If you lock yourself out (the full admin left, or AD is down): `tapqueue-admin` keeps full access
with `admin.token` from server.toml, so as root on the server you can give someone a role
(`tapqueue-admin admins grant alice admin`) and a password (`tapqueue-admin users password alice`).
And once nobody can sign in as a full admin, the setup code works again.

## Areas

| Area | Pages | Operators may also |
|------|-------|--------------------|
| Jobs | Jobs, Reports | release and cancel jobs |
| People | Users, Groups, Cards, Balances, page limits | link, edit and remove cards, see cards nobody owns yet, and add to or take from someone's balance |
| Active Directory | Active Directory | run a sync |
| Fleet | Printers, Queues, Stations, Workstations | restart stations, sign people out of PCs, update or forget a PC |
| Updates | Updates | |
| Server | Server, Notifications, crash reports | clear crash reports |

Overview and Activity are open to every admin. So are the lists of printers and queues, because
releasing jobs and editing groups need their names. Overview only shows the parts the admin's
areas cover.

## Roles

- **Viewer**: sees everything in the area and changes nothing.
- **Operator**: a viewer who can also do the day-to-day work listed above.
- **Admin**: everything in the area, including settings, adding and removing.

**Full admins** are admin in every area. Only full admins can:

- open the Admins page, give or remove roles, and set up [single sign-on](single-sign-on.md);
- set or remove the password of someone who has an admin role (a People admin sets everyone
  else's, for the [web page](web-page.md) and the tray);
- change someone who has admin rights (rename, disable, delete), or change the members of a
  group that gives admin rights (or delete it);
- change which domain controller Active Directory is read from, its CA certificate, or the bind
  account. Whoever answers as the domain controller decides who each AD admin is;
- list, make, download or delete [backups](backup.md). A backup holds everything at once,
  hashed passwords and the server's private key included.

An **admin of Updates** decides when every PC and station moves to the newest TapQueue release.
They can't hand out a program of their own: builds only come from TapQueue's releases on GitHub,
which the server downloads and checks itself, nothing can be uploaded, and every PC and station
checks what it's offered against the release on GitHub before installing it (since 0.17). Going back
to an older release is on the Server page and needs an admin of Server.

The rule about people with admin rights stops a People admin from adding themselves to an admin
group, or disabling the admins above them.

Outside dev mode, TapQueue refuses any change that would leave no full admin who can sign in:
removing the last one's role, disabling or deleting them, removing them from the group that makes
them an admin, or removing their password.

## Signing in

The console signs in with a username and password:

- **Local users**: their TapQueue username and password (at least 10 characters, stored as a
  PBKDF2-SHA256 hash). A local admin changes their own password from the sidebar. The same
  password signs them in to the [web page](web-page.md), and to the tray where it asks for a
  name and password.
- **AD users**: their TapQueue username, `DOMAIN\name` or `name@domain`, and their domain
  password, checked by binding to AD as them over LDAPS. It must be the same AD account the sync
  linked to that user.

After 5 wrong passwords for a name from one address within 15 minutes, sign-in as that name is
refused from that address for 15 minutes; an address that gets 20 wrong, whatever the names, is
refused altogether. It's counted per address so that someone guessing at an admin's name locks
themselves out, not the admin. Both numbers can be changed: **Server → Settings**, or
`tapqueue-admin server set sign-in-attempts 10` and `sign-in-lockout 30` (the address limit is
four times the attempts). Behind a reverse proxy, set `server.trusted_proxies`
([install-server.md](install-server.md#behind-a-reverse-proxy)), or every sign-in comes from the
proxy's address.

A session is an HTTP-only, `SameSite=Strict` cookie. It ends after 8 hours without use, when the
admin signs out, or when they're disabled. Changing a local password signs that user out
everywhere else. Roles are checked on every request, so removing a role takes effect at once.

Every change is recorded in the activity log under the name of the admin who made it. Changes
made with `admin.token` or in dev mode's open console are recorded as `admin`. Sign-ins and
failed attempts are recorded under **Sign-ins**.

Use the console over `https://` ([TLS](tls.md)): over plain HTTP, passwords cross the network in
the clear. A browser on another machine that opens the http:// address is sent to https:// when
the server has it.

**Single sign-on**: where it's set up, the sign-in also offers **Sign in with …**, through your
organisation's own sign-in. See [single-sign-on.md](single-sign-on.md).

## Command line

```
tapqueue-admin admins                                    # people, their roles, and every grant
tapqueue-admin admins grant alice admin                  # full admin
tapqueue-admin admins grant @helpdesk operator --area jobs,fleet
tapqueue-admin admins revoke 3                           # grant id from `admins`
tapqueue-admin users password alice                      # asks for it twice
tapqueue-admin users password alice --clear
```

## How it works

- Grants are rows in `admin_grants`: a user or a group, an area (`*` for all of them) and a role.
  Console sessions are in `admin_sessions`, stored as token hashes. Both came in schema 14.
- `AdminAccess.Requirement` in `src/TapQueue.Server/Admins/AdminAccess.cs` decides which role
  every `/api/v1/admin` request needs, from its method and path. Reads need a viewer, the listed
  day-to-day changes an operator, other changes an admin. A path it doesn't know needs a full
  admin, so a new endpoint stays locked down until it's added there.
- The console sends `X-TapQueue-Console: 1` with every request. A change made with a console
  cookie but without that header is refused, so another website can't use an admin's open
  session.
- Sign-in methods are separate from roles. Roles belong to TapQueue users and groups, however
  those people prove who they are, so single sign-on (OIDC or SAML) can be added later as another
  sign-in method.
