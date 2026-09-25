# Admin sign-in and roles

Who can use the admin console, and what each person may do there.

Admins are ordinary TapQueue users, local or from Active Directory. A **role** gives a user, or
everyone in a group, access to one **area** of the console, or to all of them. Someone with roles
from several places gets the highest one per area.

## Setting up a new server

1. Install the server with `auth.mode = "dev"` in `/etc/tapqueue/server.toml`. In dev mode the
   console at `http://<server>:8631/admin` is open to anyone on the network. Don't leave it
   like that.
2. Add the people who will run TapQueue: users on the **Users** page, or through the
   [Active Directory sync](active-directory.md).
3. On **Admins**, give at least one person **Admin** in **every area**, which makes them a full
   admin. A group works too: with AD, give `Print-Admins` (or whatever yours is called) the role,
   and whoever is in it in AD is an admin.
4. Make sure they can sign in:
   - A **local user** needs a console password. Set it on their user page, or with
     `tapqueue-admin users password <username>`.
   - An **AD user** signs in with their domain password. There's nothing to set up beyond the AD
     connection.

   The Admins page warns you while nobody can sign in as a full admin.
5. Set `auth.mode = "token"` and restart the server. The console now asks everyone to sign in,
   and each person sees and does only what their roles allow.

If you lock yourself out (the full admin left, or AD is down), set `auth.mode = "dev"` again as
root, fix the roles, and switch back. `tapqueue-admin` also keeps full access with `admin.token`
from server.toml.

## Areas

| Area | Pages | Operators may also |
|------|-------|--------------------|
| Jobs | Jobs | release and cancel jobs |
| People | Users, Groups, Cards, page limits | link, edit and remove cards |
| Active Directory | Active Directory | run a sync |
| Fleet | Printers, Queues, Stations, Workstations | restart stations, sign people out of PCs, update or forget a PC |
| Updates | Updates | |
| Server | Server, crash reports | clear crash reports |

Overview and Activity are open to every admin. So are the lists of printers and queues, because
releasing jobs and editing groups need their names. Overview only shows the parts the admin's
areas cover.

## Roles

- **Viewer**: sees everything in the area and changes nothing.
- **Operator**: a viewer who can also do the day-to-day work listed above.
- **Admin**: everything in the area, including settings, adding and removing.

**Full admins** are admin in every area. Only full admins can:

- open the Admins page and give or remove roles;
- set or remove local users' console passwords;
- change someone who has admin rights (rename, disable, delete), or change the members of a
  group that gives admin rights (or delete it).

The last rule stops a People admin from adding themselves to an admin group, or disabling the
admins above them.

Outside dev mode, TapQueue refuses any change that would leave no full admin who can sign in:
removing the last one's role, disabling or deleting them, removing them from the group that makes
them an admin, or removing their password.

## Signing in

The console signs in with a username and password:

- **Local users**: their TapQueue username and console password (at least 10 characters,
  stored as a PBKDF2-SHA256 hash). A local admin changes their own password from the sidebar.
  Printing doesn't use it; the tray still uses a client token or the PC's user.
- **AD users**: their TapQueue username, `DOMAIN\name` or `name@domain`, and their domain
  password, checked by binding to AD as them over LDAPS. It must be the same AD account the sync
  linked to that user.

After 5 wrong passwords for a name, or 20 from one address, within 15 minutes, sign-in is refused
for 15 minutes.

A session is an HTTP-only, `SameSite=Strict` cookie. It ends after 8 hours without use, when the
admin signs out, or when they're disabled. Changing a local password signs that user out
everywhere else. Roles are checked on every request, so removing a role takes effect at once.

Every change is recorded in the activity log under the name of the admin who made it. Changes
made with `admin.token` or in dev mode's open console are recorded as `admin`. Sign-ins and
failed attempts are recorded under **Sign-ins**.

Until [TLS](roadmap.md) is in, passwords cross the network in the clear. Use the console from a
trusted network, or through an SSH tunnel.

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
