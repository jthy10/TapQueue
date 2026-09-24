# Active Directory sync

TapQueue can take its users and groups from Active Directory. You pick which OUs, groups and
single users to sync; TapQueue adds those people, keeps their names and enabled state in line
with AD every night, and turns the AD groups you picked into TapQueue groups so you can decide
what they may print to and how many pages they get.

Set it up on the console's **Active Directory** page, or check on it with
`tapqueue-admin directory`.

## What AD decides and what TapQueue decides

| | Decided by |
|---|---|
| Who is in TapQueue (from the scope) | AD |
| Username (`sAMAccountName`) and display name (`displayName`) | AD |
| Disabled: disabled in AD, account expired, or no longer in the scope | AD |
| Who is in an AD group, through nested groups too | AD |
| Card number, if a card attribute is set (see [Cards](#cards)) | AD |
| What a group may print to and release at, and its page limit | TapQueue |
| A user's own page limit, and TapQueue groups they're in | TapQueue |
| Disabling someone AD has enabled | TapQueue: an admin can always disable |

The console and CLI refuse changes that the next sync would undo: renaming an AD user, enabling
one AD disabled, adding or removing members of an AD group, deleting an AD user or group. Each
refusal says where to make the change instead.

## Connecting

TapQueue reads AD over **LDAPS** (port 636) with a read-only account. Plain LDAP isn't
offered: recent domain controllers refuse simple binds without signing, and the password would
cross the network in the clear.

- **Domain controller**: a DC's name, or the domain's name. The DC's certificate must be issued
  for that name, and signed by a CA TapQueue trusts.
- **Bind account**: an ordinary domain user; every domain user can read users and groups. Give it
  as `svc-tapqueue@example.org` or as a DN. The password is saved on the server and never shown
  again.
- **CA certificate**: paste the PEM of the CA that issued the DCs' certificates (for AD Certificate
  Services, the Enterprise Root CA's certificate). Only that CA is trusted then. Leave it empty if
  the server's operating system already trusts it.

**Test** on the page (or saving the connection) signs in and shows the domain it found.

## The scope

The scope says who is synced. Add any mix of:

- **OUs**: every user anywhere under the OU, including child OUs.
- **Groups**: every member, through nested groups to any depth. The group also becomes a TapQueue
  group, named after it, with everyone in it. Nested groups count only for their members; they
  don't become TapQueue groups unless you add them too.
- **Users**: one person.

Search AD from the console to pick them, or paste a distinguished name. The scope remembers each
item by its objectGUID, so moving or renaming an OU or group in AD doesn't break it.

Someone brought in by several items is one user. Computer accounts are never synced.

### Nested groups

A group's members are read, then the members of groups inside it, and so on down. A user who is
a direct member and also a member through a nested group shows as direct; otherwise the group's
page shows which nested group they came through ("Member through IT-Dept"). A group that contains
itself somewhere down the line is read once. Membership through a user's primary group (usually
Domain Users) isn't in AD's member attribute and isn't counted.

## What a sync does

1. Reads everything in the scope from AD. If AD can't be reached, the bind is refused, or a scope
   item is gone from AD, the sync fails and **nothing changes**.
2. Matches users by objectGUID. A user renamed in AD keeps their TapQueue history, cards and limits.
   On the first sync, a local TapQueue user with the same username is **linked** to their AD
   account instead of duplicated.
3. Adds new users, updates names, and disables or re-enables people:
   - disabled in AD, or `accountExpires` in the past: disabled;
   - no longer in the scope (deleted in AD, moved out of the OUs, removed from the groups):
     disabled, **not deleted**, so their held jobs and history stay;
   - enabled in AD and in the scope again: re-enabled, but only if the sync disabled them. Someone
     an admin disabled stays disabled.
4. Adds the scope's groups, updates their names and replaces their members. Groups removed from
   the scope are removed from TapQueue, with what they allowed.

Disabled users are signed out and can't print or release; their held jobs are kept until they
expire. Once someone is disabled for being out of the scope, an admin can delete them.

Every change is recorded in the activity log under **Active Directory**.

### Preview

**Preview** (or `tapqueue-admin directory sync --dry-run`) works out every change without making
any. Preview before the first sync: it shows who will be added, and which local users will be
linked.

### The safety limit

If a sync would disable more than a set share of the AD users who are enabled now (20% by
default, and never for fewer than 5 people), it stops without changing anything. That catches a
wrong scope or a DC answering with half the directory. Check the preview, then **Sync anyway**
(or `--force`) if it's right.

## The daily sync

Turn on **Daily sync** and pick a time (01:00 by default). The time is in the server's time zone
(`timedatectl` on the server shows it). If the server was down at that time, the sync runs a
minute after it starts, as long as the last one was more than 25 hours ago.

The daily sync only starts once an admin has run a sync by hand, so the first one, which can add
hundreds of people, is always a deliberate step.

## Cards

Set **Card number attribute** (for example `employeeNumber`) if AD holds each person's card
number, exactly as the release station's reader reads it. Then, for everyone with a value in it:

- that card is theirs, moved from whoever had it;
- their other cards are removed.

People with no value keep the cards enrolled in TapQueue. If two AD users have the same card
number, the first keeps it and the sync warns about the other.

Leave the attribute empty to manage cards in TapQueue only.

## Troubleshooting

| Message | What to check |
|---|---|
| Couldn't connect … over LDAPS | Port 636 open from the server to the DC; the DC has a certificate (AD CS, or one you installed); the name you gave matches the certificate; the CA certificate is right. |
| refused the bind account's name or password | The account name (UPN or DN) and password; the account isn't locked or expired. |
| … is no longer in Active Directory | A scope item was deleted. Remove it from the scope, or add it again if it was recreated (a recreated object has a new objectGUID). |
| Stopped: this would disable … | See [the safety limit](#the-safety-limit). |
| Skipped alice: TapQueue's alice is a different AD account | An AD account was deleted and recreated with the same name. Delete the old user in TapQueue (it's disabled as out of scope), and the next sync adds the new one. |
