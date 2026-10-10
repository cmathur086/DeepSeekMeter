# DeepSeekMeter — English fork

A **Windows system-tray app** that shows your DeepSeek account balance and real usage:
today's and this month's spend, request counts, output and cache-hit tokens, a per-model
breakdown, and a daily token trend.

Sign in with your DeepSeek account — **no API key, no manual configuration, no budget anchor
to invent**. Click the tray icon, sign in once, and it keeps itself up to date.

This is a fork of **[CWNU-Open-Source-Community/DeepSeekMeter](https://github.com/CWNU-Open-Source-Community/DeepSeekMeter)**
(MIT, by Moddy / gaoming), which is a macOS menu-bar app with Windows, iOS and Android ports.

---

## Why this fork exists

Two reasons, one cosmetic and one functional.

### 1. English UI

Upstream's entire interface is Chinese. The upstream project explicitly forbids changing
that (`AGENTS.md`, red line 8: *"不改语言基调"*), so an English build cannot be contributed
back and has to live in a fork. Every user-visible string here has been translated —
windows, tray menu, tooltips, status messages and error text. Source comments were left in
Chinese to keep the diff small and reviewable against upstream.

### 2. Upstream could not fetch any data (this is the important one)

When tested in October 2026, the upstream Windows build was **non-functional** — it would
open the login page, let you sign in, and then sit on *"Waiting for sign-in to complete…"*
forever without ever showing a number. Two independent causes:

**a) DeepSeek's platform API sits behind an AWS WAF.**
`platform.deepseek.com` answers `202` with an `x-amzn-waf-action` header to any client that
does not present a browser session. Measured directly: the endpoint returns an *identical*
202 for a real token, a deliberately invalid token, and no token at all — the response says
nothing about your credentials. Upstream's `PlatformService` used a bare `HttpClient`, which
has no cookie jar and no browser TLS fingerprint, so every request was challenged and every
call failed.

**b) The login state is a cookie *and* a token together.**
Sending only the session cookie returns HTTP 200 with a business-level error saying the token
is invalid. Upstream only ever read `localStorage.userToken`, and its extraction path did not
find it — so it never completed a login.

**The fix in this fork:** `PlatformService`'s transport is now injectable
(`IPlatformTransport`), and the WPF app supplies a **persistent offscreen WebView2**
(`BrowserHost`) that shares the login window's browser profile. Every API request is issued
as a `fetch()` *inside that page*, so it carries the session cookies, the WAF clearance and a
real browser fingerprint. The token is read from the page directly. On startup the app
adopts an existing browser session, so a sign-in survives restarts.

If you are looking for a Windows DeepSeek meter that works today, this is the difference that
matters — and it is why this fork exists publicly rather than as a private patch.

---

## Requirements

- **Windows 10 version 1809 or later, or Windows 11**
- Nothing else at runtime. The release build is self-contained; the .NET runtime is bundled.

Building from source additionally needs the **.NET 8 SDK**.

## Install

1. Download `DeepSeekMeter-win-x64.zip` from the [Releases](../../releases) page.
2. Extract it somewhere you can write (the app stores its browser profile under
   `%LOCALAPPDATA%\DeepSeekMeter`).
3. Run `DeepSeekMeter.exe`. No installer, no admin rights.

The binary is unsigned, so SmartScreen may warn you. There is no code-signing certificate on
this fork.

## First run

1. The tray icon appears — **it is probably hidden in the Windows 11 overflow**, behind the
   `^` chevron at the right of the taskbar. Drag it onto the taskbar to pin it.
2. Click the tray icon to open the panel.
3. Click **Log in**. An embedded browser opens on `platform.deepseek.com`. Sign in there.
4. The window closes by itself when the session is captured and the panel fills with data.

Settings, credentials and the refresh interval live in the panel's own settings section.

**Sign in with email + password inside the embedded browser.** Third-party OAuth (Google,
Apple) will not work: upstream's design deliberately cancels any navigation to a
non-`deepseek.com` host and hands it to your system browser, so the OAuth round-trip happens
outside the app and the app never sees the session.

## What it shows

- **Balance** — total, granted and topped-up, with currency code
- **Today** — cost, requests, output tokens
- **This month** — cost, total requests, output tokens, cache-hit tokens
- **Per model** — request count and cost for each model used
- **Token trend** — daily chart, switchable between output / cache-hit / total, with today's
  total and the month's peak day
- Status pill, last-updated time, tray tooltip with the balance, low-balance alerts, and a
  configurable refresh interval (15s – 10m)

Token counts use K/M/B units. Costs use the account's own currency.

## How it works

```
WPF UI ── MainViewModel ── PlatformService ── IPlatformTransport
                                                   │
                                    WebViewTransport │  (fetch() inside the page)
                                                   ▼
                                    BrowserHost — persistent offscreen WebView2
                                       shares the login window's profile
                                                   │
                                                   ▼
                            platform.deepseek.com (private, undocumented endpoints)
```

Requests go to DeepSeek's own private platform endpoints:

| Endpoint | Returns |
|---|---|
| `/auth-api/v0/users/current` | account identity + currency |
| `/api/v0/users/get_user_summary` | balance and cumulative spend |
| `/api/v0/usage/by_api_key/amount` | token usage (daily buckets) |
| `/api/v0/usage/by_api_key/cost` | cost (daily buckets) |

These are **not a public API contract**. They are the same endpoints the platform's own web
UI calls, and DeepSeek can change or protect them at any time — the WAF described above is
exactly that happening. If the app stops working after a DeepSeek change, this layer is
where to look first.

## Diagnostics

Release builds strip `Debug.WriteLine`, which left the app with no way to explain itself, so
this fork adds a small rolling log:

```
%LOCALAPPDATA%\DeepSeekMeter\diag.log
```

It records request URLs and HTTP status codes only — never a token.

## Privacy

- Requests go **only** to `platform.deepseek.com`, from the app's own browser session.
- No telemetry, no analytics, no proxy, no third-party upload.
- The session token is stored DPAPI-encrypted, bound to your Windows user account, in
  `%APPDATA%\DeepSeekMeter\settings.json`.
- Browser profile and session cookies live in
  `%LOCALAPPDATA%\DeepSeekMeter\WebView2`. **Sign out** in the panel clears them.

## Known limitations

- **The embedded login page is the live DeepSeek website**, so that one screen stays in
  whatever language DeepSeek serves.
- **DeepSeek's own server-side error messages are passed through verbatim** and are not
  translatable client-side. Client-side text is English.
- Third-party OAuth sign-in does not work (see *First run*).
- macOS, iOS and Android parts of the upstream project are untouched and remain Chinese.

## Building from source

```powershell
dotnet build windows/DeepSeekMeter.sln -c Release

# self-contained x64 release, same as the published ZIP
pwsh Scripts/publish-windows.ps1

# offline smoke test — DPAPI, settings, JSON decoding, aggregation (exit 0 required)
dotnet run --project windows/tests/DeepSeekMeter.Selftest -c Release
```

The self-test needs no network and no credentials.

**Do not** add `InvariantGlobalization` or `PublishTrimmed`: `Models.cs` resolves the
`"China Standard Time"` time zone and trimming breaks it.

## Attribution and licence

MIT, inherited from upstream. Original work © Moddy / gaoming and the
[CWNU Open Source Community](https://github.com/CWNU-Open-Source-Community/DeepSeekMeter);
the original README is preserved as [README.upstream.md](README.upstream.md).

Modifications in this fork, © 2026 cmathur086:

1. All user-visible UI text translated to English.
2. Token display units changed from 亿/万 (10⁸/10⁴) to B/M/K (10⁹/10⁶/10³).
3. Platform requests rerouted through a browser-context transport (`IPlatformTransport`,
   `BrowserHost`, `WebViewTransport`) to work against DeepSeek's WAF.
4. Login accepts the browser session and adopts it on startup.
5. Added `Diag` logging and version stamping (`0.3.1-en1`).

The `LICENSE` file is unmodified.

---

*Not affiliated with DeepSeek. With thanks to the upstream authors, whose architecture —
tray app, private-endpoint client, DPAPI storage — is entirely their design; this fork only
translated it and repaired the transport.*
