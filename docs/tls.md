# TLS (HTTPS and IPPS)

The server listens twice: plain HTTP on port 8631 (`server.listen`) and TLS on port 8632
(`tls.listen`). Over TLS, the same endpoints are `https://` for the client API, stations and the
admin console, and `ipps://` for printing, so documents, tokens and passwords aren't readable on
the network. Point clients and stations at `https://<server>:8632` and they use it for everything,
including the printers they add.

## The certificate

Out of the box the server makes itself a **self-signed certificate** on first start and keeps it
in `/var/lib/tapqueue/tls` (`server.crt`, and `server.key` readable only by the server). It's
valid for ten years, for the machine's name, its full DNS name, `localhost` and every address it
has, plus anything in `tls.names`. It's an end-entity certificate, not a CA: a PC that trusts it
trusts this server and nothing else.

See it, with the SHA-256 fingerprint PCs check it by:

```sh
sudo tapqueue-admin server
```

```
tls              https:// and ipps:// on port 8632, plain HTTP still allowed
  certificate    CN=printsrv, O=TapQueue, self-signed, until 2036-10-08
  names          printsrv, printsrv.corp.example, localhost, 192.0.2.5
  file           /var/lib/tapqueue/tls/server.crt
  SHA-256        AF:EF:98:35:…:FE:AC:D7
```

The server's log says the same at startup, and warns if the self-signed certificate doesn't cover
one of the machine's current names or addresses (say, after an IP change). To make a new one, add
the name to `tls.names` if it isn't the machine's own, delete `/var/lib/tapqueue/tls` and restart.
PCs and stations that trusted the old certificate then have to be told to trust the new one (see
below), so give the server a fixed address and DNS name before rolling out clients.

### Your own certificate

A certificate from your company CA (AD Certificate Services, for one) or Let's Encrypt is trusted
by PCs without any pinning:

```toml
[tls]
cert_file = "/etc/tapqueue/tls/fullchain.pem"
key_file = "/etc/tapqueue/tls/privkey.pem"
```

Both are PEM; put any intermediate certificates after the server's in `cert_file`. The `tapqueue`
user has to be able to read them. The server looks at the files every 10 seconds and starts using
renewed ones by itself, with no restart.

## How PCs and stations trust a self-signed certificate

A certificate the PC already trusts (from a CA) is accepted as usual. A self-signed one is
accepted if it's **pinned**:

- **Trust on first use** (the default). The TapQueue service on a PC, or the release station,
  saves the certificate it sees the first time it connects and from then on accepts only that one.
  On a PC it's in `C:\ProgramData\TapQueue\server-certificate.pem` (Windows) or
  `/var/lib/tapqueue-client/server-certificate.pem` (Linux); the tray app checks against the same
  file. A station keeps it in `/var/lib/tapqueue-station`.
- **A fingerprint in the config**, for when you'd rather not trust whatever answers first:
  ```toml
  server_cert_fingerprint = "AF:EF:98:35:…:FE:AC:D7"   # client.toml or station.toml
  ```
  It wins over a saved certificate, which also makes it the way to move PCs to a new certificate
  (or delete the saved file and restart the TapQueue service).

A certificate that doesn't match is refused, and the tray app and logs say which fingerprint was
offered and which file or setting it was checked against.

**Windows printing.** Windows' built-in IPP driver checks the server's certificate itself, against
the PC's trusted root certificates. So when the server's certificate is self-signed, the TapQueue
service adds it there (only it: it's removed again when the server changes certificate or the
client is uninstalled). It refuses to for a certificate that could vouch for anything else: a CA,
one with a wildcard, or one that also names hosts outside the server's and the PC's DNS domains.
A server reached by a bare IP address needs its other names to be in the PC's domain too. If it
refuses, the tray app's **Refresh printers** says why; use a certificate from your own CA.

**Linux printing.** CUPS trusts a printer's certificate the first time it connects, the same way.

**tapqueue-admin** from another machine: `--fingerprint`, `TAPQUEUE_SERVER_FINGERPRINT` or
`server_cert_fingerprint` in `~/.config/tapqueue/admin.toml`. On the server itself it talks to
`localhost` over plain HTTP, which never leaves the machine.

## Moving everyone to TLS

1. Upgrade the server. TLS is on by default on port 8632; open it in the firewall
   (`sudo ufw allow 8632/tcp`). Nothing changes for existing clients yet.
2. Point clients and stations at `https://<server>:8632`: change `server_url` in `client.toml` and
   `station.toml` (on Windows, `TapQueue_client_X.Y.Z.exe /VERYSILENT /SERVER=https://<server>:8632`
   does it), then restart the TapQueue service or station. The printers move to `https://` on the
   next check-in. New installs find the server's HTTPS address by themselves.
3. Once nothing uses plain HTTP, set `tls.require = true` and restart. Plain HTTP from other
   machines is then refused with a message giving the `https://` address; `/` and `/healthz`
   (which the installers' search uses) still answer, and so does the server itself on `localhost`.

To turn TLS off, set `tls.listen = ""`.

## Settings

| Setting | Default | |
|---|---|---|
| `tls.listen` | `0.0.0.0:8632` | Address and port for HTTPS and IPPS. `""` = no TLS |
| `tls.cert_file`, `tls.key_file` | | Your own PEM certificate and key. Empty = self-signed |
| `tls.names` | `[]` | More names or addresses for the self-signed certificate |
| `tls.require` | `false` | Refuse plain HTTP from other machines |
| `server_cert_fingerprint` (client.toml, station.toml, admin.toml) | | SHA-256 of a self-signed server certificate. Empty = trust on first use (clients and stations) |
