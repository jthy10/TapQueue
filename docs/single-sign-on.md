# Single sign-on and Entra ID

Two separate things, often used together:

- **Single sign-on** lets people sign in to the admin console and the [web page](web-page.md)
  through your organisation's sign-in (Microsoft Entra ID, Google Workspace, Okta, Keycloak: any
  OpenID Connect provider) instead of a password TapQueue checks.
- **Entra ID sync** brings users and groups in from Microsoft Entra ID, the way
  [Active Directory sync](active-directory.md) does from AD.

Neither changes what anyone may do: roles ([admin-roles.md](admin-roles.md)), groups and limits
belong to TapQueue users, however they prove who they are.

## Single sign-on

### 1. Register TapQueue with the provider

Register a **web application**. Its redirect URI is this server's address as people's browsers
reach it, plus `/api/v1/sso/callback`:

    https://tapqueue.example.org:8632/api/v1/sso/callback

The console shows it on **Admins → Single sign-on** (`tapqueue-admin sso` prints it). You get a
**client ID** and make a **client secret**.

- **Entra ID**: App registrations → New registration, single tenant, platform *Web*. The address
  to give TapQueue is `https://login.microsoftonline.com/<tenant-id>/v2.0` (your tenant's own, not
  `common`).
- **Google**: an OAuth client of type *Web application*; `https://accounts.google.com`, and
  `email` as the username claim.
- **Okta, Keycloak and others**: the issuer address, the one with
  `/.well-known/openid-configuration` under it.

### 2. Enter it in TapQueue

**Admins → Single sign-on → Edit** (full admins only), or:

```sh
sudo tapqueue-admin sso set authority https://login.microsoftonline.com/<tenant-id>/v2.0
sudo tapqueue-admin sso set client-id <client-id>
sudo tapqueue-admin sso set client-secret <secret>
sudo tapqueue-admin sso set domain example.org
sudo tapqueue-admin sso set label Microsoft          # the button: "Sign in with Microsoft"
sudo tapqueue-admin sso on
```

**Test** checks the provider can be reached. The sign-in pages then offer **Sign in with …**
below the password form; passwords keep working.

### Who someone is

The provider says who signed in, in the ID token's `preferred_username` claim (change it with
`username-claim`; Google uses `email`). TapQueue then looks for that person:

1. Someone synced from Entra ID is found by their object id, whatever they're called.
2. Otherwise, a TapQueue user whose **username is exactly** what the provider calls them.
3. Otherwise, inside **your domain** only: `jake@example.org` is the TapQueue user `jake`.

Set `domain` to your organisation's own. Without it, rule 3 is off and names must match
usernames exactly. Never set it to a public domain: with `gmail.com`, anyone's
`admin@gmail.com` would be your user `admin`.

Someone who isn't a TapQueue user, or is disabled, isn't let in; for the console they also need a
role. While a directory sync is set up, only people from the directory sign in this way: a local
user (whose name is whatever an admin typed) keeps using their password.

### What TapQueue checks

It uses the authorization code flow with PKCE and a client secret, and checks the ID token
itself: the provider's signature (RS256, against the keys the provider publishes), that it was
issued by the provider set up here and to this TapQueue, that it hasn't expired, and that it
answers the sign-in this browser started. The server has to reach the provider over HTTPS;
people's browsers do the rest.

## Entra ID sync

For organisations whose users live in Entra ID rather than on-premises AD.

### 1. An app registration that can read the directory

In Entra ID: App registrations → New registration. Under **API permissions** add the
**application** permissions `User.Read.All` and `Group.Read.All` (Microsoft Graph) and grant
admin consent. Under **Certificates & secrets** make a client secret. TapQueue only reads.

It can be the same registration as single sign-on's.

### 2. Connect

Console → **Directory → Connection → Edit**, choose **Microsoft Entra ID**, and enter the tenant
(its ID or `contoso.onmicrosoft.com`), the application (client) ID and the secret. **Test** says
what Entra ID answered.

### 3. Choose who is synced

Add **groups** (their members come too, through nested groups) or single **users** to the scope,
preview, and sync, exactly as with [Active Directory](active-directory.md). Entra ID has no OUs.

What's different from AD:

- **Usernames** are the part of the sign-in name before the `@` (`jake@example.org` → `jake`),
  which is also what Windows calls someone on a PC joined to Entra ID. Accounts synced from
  on-premises AD keep their AD logon name. Guests aren't brought in: they have no such name.
- **Cards** can come from a user property: set the card attribute to `employeeId`, or another
  property Graph returns for users.
- **Passwords**: Entra ID doesn't check a password for anyone else, so there's no "domain
  password" for its users. They sign in to the console and the web page with **single sign-on**
  (above), and the tray signs them in as their PC's user: set tray sign-in to **vouched for by
  the PC** ([how-it-works.md](how-it-works.md#signed-in-by-the-pc)). Tray sign-in with a name and
  password isn't offered with Entra ID.
- A sync that would disable more than the safety limit still stops, and a secret that has
  expired fails the sync with what Entra ID said, in the sync's history.

One directory at a time: switch between Active Directory and Entra ID only with an empty scope.
