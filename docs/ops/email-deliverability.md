# Email deliverability check

**Status: PENDING.** The manual gate (real delivery to a Gmail and an Outlook mailbox, see section 6) has not been
performed yet. Until it passes, real sending stays off: `Email:Smtp:Host` is left empty, the app uses the
`LoggingEmailSender` and outbox rows end as `Simulated`.

The sending mailbox and domain are **not decided yet**. A mailbox on another domain was offered and rejected for
technical reasons (that domain is hosted on Microsoft 365 and publishes `SPF -all` and `DMARC p=reject`, so a third
server sending as that domain would fail SPF and be rejected). Everything below is written for a placeholder
`<sending-domain>` and `<smtp-host>`; fill in the real values only in this document's results table and in the
server's configuration, never in committed `appsettings*.json`.

## 1. DNS checklist for `<sending-domain>`

| Record | What it must say | How to check |
|---|---|---|
| **SPF** (TXT on `<sending-domain>`) | Exactly one `v=spf1 ...` record that authorises the sending server's public IP (`ip4:<public-ip>`) or its relay (`include:...`), ending with `~all` or `-all`. At most 10 DNS lookups. | `Resolve-DnsName -Type TXT <sending-domain>` / `dig +short TXT <sending-domain>` |
| **DKIM** (TXT on `<selector>._domainkey.<sending-domain>`) | `v=DKIM1; k=rsa; p=<public key>` (2048-bit) matching the key the SMTP server signs with; the `d=` in signed mail must equal `<sending-domain>` (alignment). | `Resolve-DnsName -Type TXT <selector>._domainkey.<sending-domain>` |
| **DMARC** (TXT on `_dmarc.<sending-domain>`) | `v=DMARC1; p=none; rua=mailto:<reports-address>` to start; raise to `p=quarantine` and later `p=reject` once reports show aligned SPF/DKIM. | `Resolve-DnsName -Type TXT _dmarc.<sending-domain>` |
| **rDNS / PTR** of the sending IP | The PTR of `<public-ip>` resolves to a host name (for example `mail.<sending-domain>`) whose A record points back to the same IP (forward-confirmed). The SMTP server's `HELO`/`EHLO` name should be that host name. Set by the ISP / hosting provider. | `Resolve-DnsName -Type PTR <public-ip>`, then `Resolve-DnsName <ptr-name>` |
| **MX** (optional for sending) | Bounces and replies need somewhere to go; `Email:From` should be a mailbox (or alias) that exists. | `Resolve-DnsName -Type MX <sending-domain>` |

A residential IP (home server) is usually listed on Spamhaus PBL and many ISPs block outbound port 25; direct
delivery from it will be rejected or land in spam. In that case send through an authenticated submission relay
(port 587 with STARTTLS) whose IPs the SPF record authorises and which signs DKIM for `<sending-domain>`. The app
does not care which: it only talks SMTP to `Email:Smtp:Host`.

## 2. Outbound port tests (from the machine that will send)

```powershell
Test-NetConnection <smtp-host> -Port 587   # submission (STARTTLS)
Test-NetConnection <smtp-host> -Port 465   # submission (implicit TLS), if used
Test-NetConnection gmail-smtp-in.l.google.com -Port 25   # only if the server delivers directly (MX to MX)
```

```bash
openssl s_client -connect <smtp-host>:587 -starttls smtp -crlf -quiet   # expect "250" replies and a valid certificate chain
openssl s_client -connect <smtp-host>:465 -crlf -quiet
nc -vz gmail-smtp-in.l.google.com 25
```

`TcpTestSucceeded : False` (or a timeout) on port 25 means the ISP blocks direct delivery: use a relay.

## 3. Configuration (`Email:*`)

| Key | Meaning | Default |
|---|---|---|
| `Email:Smtp:Host` | SMTP server. **Empty = no real sending** (`LoggingEmailSender`, rows `Simulated`). | empty |
| `Email:Smtp:Port` | 587 (submission), 465 (implicit TLS) or 25 (local relay). | 587 |
| `Email:Smtp:UseStartTls` | `true`: STARTTLS required. `false`: implicit TLS on port 465, otherwise plain (trusted local relay only). | `true` |
| `Email:Smtp:User` / `Email:Smtp:Password` | SMTP authentication; empty user = none. **Secret.** | empty |
| `Email:Smtp:TimeoutSeconds` | Per SMTP operation; must be below `Email:Outbox:RetryBaseSeconds`. | 30 |
| `Email:From` / `Email:FromName` | Sender address (required with a host) and display name. | empty / `PositionTracker` |
| `Email:PublicBaseUrl` | Public base URL of the web app, absolute http(s); emailed links are built from it. Required with a host. | empty |
| `Email:Outbox:PollIntervalSeconds` | How often the worker looks for due mail. | 5 |
| `Email:Outbox:BatchSize` | Rows claimed per round. | 20 |
| `Email:Outbox:MaxAttempts` | Attempts per message including the first. | 6 |
| `Email:Outbox:RetryBaseSeconds` / `RetryMaxSeconds` | Retry delay: base, doubling, capped. | 60 / 3600 |
| `Email:Outbox:RetentionDays` | Finished rows (sent, simulated, failed) are deleted after this many days; 0 = at once. | 7 |

Committed files contain only empty values. Supply the real ones on the machine:

```powershell
# Development machine (stored outside the repository, shared by the API and tools/SendTestMail)
dotnet user-secrets --project src/PositionTracker.Api set "Email:Smtp:Host" "<smtp-host>"
dotnet user-secrets --project src/PositionTracker.Api set "Email:Smtp:User" "<smtp-user>"
dotnet user-secrets --project src/PositionTracker.Api set "Email:Smtp:Password" "<smtp-password>"
dotnet user-secrets --project src/PositionTracker.Api set "Email:From" "<sender>@<sending-domain>"
dotnet user-secrets --project src/PositionTracker.Api set "Email:PublicBaseUrl" "https://<public-host>"
```

```bash
# Server / container: environment variables (double underscore = section separator)
Email__Smtp__Host=<smtp-host>
Email__Smtp__User=<smtp-user>
Email__Smtp__Password=<smtp-password>
Email__From=<sender>@<sending-domain>
Email__PublicBaseUrl=https://<public-host>
```

Startup fails with a message naming the key when a host is set but `Email:From` or `Email:PublicBaseUrl` is
missing or malformed.

## 4. How the app sends (for reading logs)

- Callers add an outbox row in the same transaction as the change that causes the email. The `EmailOutboxWorker`
  claims due rows (`FOR UPDATE SKIP LOCKED`), sends them, and records `Sent`, `Simulated`, `Failed` or a retry.
- Transient errors (4xx replies, network, TLS, authentication) are retried with growing delay; permanent ones
  (5xx for the recipient or message, malformed address) and the last failed attempt end as `Failed`. A failed
  invitation sets its participant (if still `Invited`) to `EmailFailed`.
- **At-least-once:** a message is claimed before it is sent. If the process stops after the server accepted it but
  before the outcome was saved, it is sent again after its retry delay, so a participant can rarely get a duplicate.
  This is bounded: once the attempts are used up the row becomes `Failed` ("outcome unknown") without another send.
- Logs and `LastError` never contain addresses, names, subjects, bodies or links: only the outbox id, purpose,
  recipient domain (simulated sends), attempt number and SMTP status code / error kind. Bodies are cleared when a
  row finishes (the link inside is a credential until it expires); rows are deleted after the retention period.

## 5. Test-send tool

`tools/SendTestMail` reads the same configuration (API user-secrets, then `Email__*` environment variables). It
prints the plan (recipient domains, sender domain, port, TLS mode, whether authentication is configured; never
the host, user or password) and sends only with `--yes`:

```powershell
# Dry run: prints the plan, exit code 2
dotnet run --project tools/SendTestMail -- <you>@gmail.com <you>@outlook.com
# Send the plain test message and a sample invitation (real template, sample data) to each address
dotnet run --project tools/SendTestMail -- --yes --invitation <you>@gmail.com <you>@outlook.com
```

Exit codes: 0 all accepted by the SMTP server, 1 some failed, 2 dry run, 3 not configured or invalid
configuration, 64 bad arguments. "Accepted" means only that the configured server took the message; whether it
reached the inbox is checked by hand (section 6). Use only mailboxes you own.

## 6. Manual gate (human) and how to read the results

Run the tool with `--yes --invitation` from the machine that will send in production, to one Gmail and one Outlook
mailbox. Then, for each message (search for the printed run id, check Inbox **and** Spam/Junk):

- **Gmail:** open the message, menu ⋮ → *Show original*. The summary at the top must read
  `SPF: PASS with IP <public-ip>`, `DKIM: 'PASS' with domain <sending-domain>`, `DMARC: 'PASS'`.
- **Outlook.com:** open the message, ⋯ → *View* → *View message source*. In `Authentication-Results:` look for
  `spf=pass`, `dkim=pass header.d=<sending-domain>`, `dmarc=pass`, and check `X-MS-Exchange-Organization-SCL`
  (0–1 good, 5+ = junk).
- Also check the invitation renders (button, Slovak diacritics, footer) and that *Reply* goes to the organizer
  address (in the sample, the sender address).

If the mail lands in spam, `spf`/`dkim`/`dmarc` is not `pass`, or port 25/587 is blocked: record it below, keep
`Email:Smtp:Host` empty in production (simulated sending) and fix DNS or switch to a relay before trying again.
Real sending is switched on only after a row with both providers in the inbox and all three checks `pass`.

### Results

| Date | Sending host / relay | Recipient provider | Folder (Inbox/Spam) | SPF | DKIM | DMARC | Notes | Checked by |
|---|---|---|---|---|---|---|---|---|
| PENDING | | Gmail | | | | | | |
| PENDING | | Outlook.com | | | | | | |
