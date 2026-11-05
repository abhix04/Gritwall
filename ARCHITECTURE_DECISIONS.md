# GRITWALL — Architecture Decision Records (ADRs)

**Document Version:** 1.0.0  
**Project:** GRITWALL (built on Umbra)  
**Status:** Approved Architectural Blueprint  

---

## Index of Architectural Decisions

- [ADR-001: Retention and Extension of Static Core Architecture vs. Dependency Injection](#adr-001-retention-and-extension-of-static-core-architecture-vs-dependency-injection)
- [ADR-002: Inter-Process Communication: Named Pipes with Windows ACLs vs. File-Based Signaling](#adr-002-inter-process-communication-named-pipes-with-windows-acls-vs-file-based-signaling)
- [ADR-003: Elevated Watchdog Architecture: Transitioning to a Managed Windows Service](#adr-003-elevated-watchdog-architecture-transitioning-to-a-managed-windows-service)
- [ADR-004: Website Monitoring and Blocking: Manifest V3 Native Messaging vs. Local Transparent Proxy](#adr-004-website-monitoring-and-blocking-manifest-v3-native-messaging-vs-local-transparent-proxy)
- [ADR-005: Persistence Engine for High-Frequency Activity Data: SQLite vs. Flat JSON Files](#adr-005-persistence-engine-for-high-frequency-activity-data-sqlite-vs-flat-json-files)
- [ADR-006: Foreground Window Monitoring: WinEventHook vs. Polling Timer](#adr-006-foreground-window-monitoring-wineventhook-vs-polling-timer)
- [ADR-007: Temporal Integrity & Clock Tampering Defense: Monotonic Ticks vs. Wall-Clock UTC](#adr-007-temporal-integrity--clock-tampering-defense-monotonic-ticks-vs-wall-clock-utc)
- [ADR-008: Content Classification Engine: Local Taxonomy Engine vs. Cloud Domain Lookup](#adr-008-content-classification-engine-local-taxonomy-engine-vs-cloud-domain-lookup)

---

### ADR-001: Retention and Extension of Static Core Architecture vs. Dependency Injection

#### Status
**Accepted**

#### Context
The upstream Umbra codebase relies exclusively on static classes with pure or near-pure static methods across `Umbra.Core` (`Config.cs`, `Session.cs`, `Periods.cs`, `Blocker.cs`, `AtomicFile.cs`, `WatchdogLoop.cs`, `Json.cs`, `Settings.cs`). There is no dependency injection (DI) container (such as `Microsoft.Extensions.DependencyInjection`). Test isolation is achieved cleanly by redirecting the static root `Config.DataDir` to temporary directories during unit test fixtures (`TestEnvironment.cs`).

Introducing a full DI refactor across the entire codebase would require rewriting hundreds of call sites across `Umbra.App` pages (`FocusPage.xaml.cs`, `BlocklistPage.xaml.cs`, `PeriodsPage.xaml.cs`, `StatsPage.xaml.cs`), introducing significant regression risks and violating our core directive: *do not refactor existing working functionality without necessity*.

#### Decision
We will **retain the static architecture pattern** for existing domain modules (`Session`, `Periods`, `Blocker`, `AtomicFile`, `Config`). 

For newly introduced complex subsystems (such as the SQLite Activity Store, the WinEventHook window tracker, and the Named Pipe IPC server), we will implement clean interface-driven services (`IActivityTracker`, `IActivityStore`, `IIpcServer`) exposed via lightweight singleton accessors (e.g., `GritwallServices.ActivityTracker`). This enables complete testability and mockability for new features without destabilizing existing working logic.

#### Consequences
- **Positive:** Preserves 100% of existing, passing unit tests (129/129 tests pass without code modification). Zero regression risk for stable focus and period scheduling logic.
- **Negative:** Architectural duality between existing static modules and new service-based abstractions.
- **Mitigation:** Document clean boundaries: legacy focus timers and period schedules interact with new tracking services through explicit, documented facade methods.

---

### ADR-002: Inter-Process Communication: Named Pipes with Windows ACLs vs. File-Based Signaling

#### Status
**Accepted**

#### Context
Umbra performs inter-process communication (IPC) exclusively through unauthenticated file creation and polling in `%APPDATA%\UmbraNative\data\`:
- `watchdog.pid`: Written by watchdog to announce its PID.
- `watchdog.stop-request`: Written by UI to request watchdog shutdown.
- `session.json`, `blocklist.json`, `periods.json`: Written by UI and polled every 2000ms by watchdog.

As verified in `SOURCE_AUDIT.md` and confirmed by security analysis, this architecture introduces a severe vulnerability: **any unprivileged process running under the standard user account can terminate the elevated watchdog by simply creating an empty `watchdog.stop-request` file**, completely bypassing active Hard Mode sessions. Furthermore, file polling introduces an unavoidable 2-second latency window.

#### Decision
Replace file-based control signaling with **bidirectional Windows Named Pipes (`\\.\pipe\GritwallWatchdog`)**:
1. The elevated watchdog creates and owns the named pipe server.
2. Pipe security attributes are configured using Windows Access Control Lists (ACLs) via `PipeSecurity`:
   - `BUILTIN\Administrators`: Full Control.
   - Current User Token: Read/Write access only for message passing.
   - Anonymous / Other Users: Denied.
3. Every session command (`StopSession`, `UpdateBlocklist`, `RequestShutdown`) requires pipe message exchange with a cryptographic session nonce generated by the watchdog upon elevation.
4. Remove `watchdog.stop-request` entirely. The watchdog will only accept termination commands over the secured pipe after validating that Hard Mode is not active.

#### Consequences
- **Positive:** Completely closes the `watchdog.stop-request` bypass backdoor. Instantaneous (sub-millisecond) command processing replaces 2-second file polling. Eliminates file lock contention and `.tmp` rename collisions.
- **Negative:** Requires handling pipe reconnection, client disconnections, and thread-safe async I/O.
- **Mitigation:** Wrap named pipe communication in a resilient client/server wrapper with automatic reconnection backoff.

---

### ADR-003: Elevated Watchdog Architecture: Transitioning to a Managed Windows Service

#### Status
**Accepted (Phased)**

#### Context
Umbra launches its watchdog as a detached GUI executable (`Umbra.exe --watchdog`) using the UAC `runas` verb (`WatchdogSupervisor.cs`). This approach has substantial limitations:
1. Every time the watchdog starts, the user receives an intrusive UAC prompt.
2. If the user closes the main UI, the watchdog continues running orphaned in the background.
3. An administrative user can terminate the watchdog process instantly from Task Manager or `taskkill /F`.
4. Inno Setup's uninstaller (running at standard user level) cannot terminate the watchdog, leading to failed uninstallations and orphaned hosts/firewall entries.

#### Decision
Adopt a **two-phase evolution**:
- **Phase 1 (Immediate Hardening):** Maintain the `Umbra.exe --watchdog` elevated process model, but implement named-pipe IPC (ADR-002) and an uninstaller guard that prevents uninstallation while Hard Mode is active.
- **Phase 2 (Long-term Target):** Transition the watchdog engine into a dedicated Windows Service (`GritwallService.exe`) registered with `ServiceStartType.Automatic` and running under `NT AUTHORITY\SYSTEM`:
  - Starts automatically at Windows boot before user login.
  - No UAC prompts during daily desktop usage.
  - Protected by Service Control Manager (SCM) access rights.
  - Desktop UI communicates with the service exclusively via authenticated local Named Pipes.

#### Consequences
- **Positive:** Zero UAC prompts for the user during daily desktop operation. System-level persistence across user logoffs and reboots.
- **Negative:** Requires administrative privileges during initial application installation. Service lifecycle management requires proper installer actions (`Umbra.iss` / `Gritwall.iss`).
- **Mitigation:** Inno Setup handles service installation, startup, and clean uninstallation natively.

---

### ADR-004: Website Monitoring and Blocking: Manifest V3 Native Messaging vs. Local Transparent Proxy

#### Status
**Accepted**

#### Context
Website blocking in Umbra relies on three complementary layers:
1. OS `hosts` file modification (`127.0.0.1 <domain>`).
2. Windows Firewall rules blocking popular DoH provider IPs (`1.1.1.1`, `8.8.8.8`, `9.9.9.9`).
3. Chromium browser extension using `declarativeNetRequest` rules updated via Native Messaging (`Umbra.BrowserHost`).

To enable *granular website tracking* (e.g., active time spent on `reddit.com` vs `github.com`), we need real-time domain and tab visibility. Two architectures were evaluated:
- **Option A (Browser Extension + Native Messaging):** Enhance the existing browser extension to emit active tab domain heartbeats.
- **Option B (Local Transparent HTTPS Proxy / WFP Driver):** Install a local forward proxy (e.g., mitmproxy / Titanium) with a trusted root certificate to intercept all system HTTP/S traffic.

#### Decision
We select **Option A (Enhanced Extension + Native Messaging)** as the primary website tracking and blocking mechanism, retaining hosts file and firewall filtering as secondary defense-in-depth:
1. A transparent HTTPS proxy requires installing a root Certificate Authority (CA) in the Windows Certificate Store, which creates severe security and anti-malware flags, breaks certificate pinning (e.g., in banking apps, Steam, Discord), and significantly increases maintenance complexity.
2. The existing `Umbra.BrowserHost` native messaging infrastructure is lightweight, proven, and fully supported in Chrome, Edge, Brave, and Vivaldi.
3. We will extend `Umbra.BrowserHost` to support Firefox by registering the appropriate Mozilla native messaging manifest (`Software\Mozilla\NativeMessagingHosts\com.gritwall.native`).

#### Consequences
- **Positive:** Zero root CA installation; zero certificate pinning breakage; native browser performance; clean user consent model.
- **Negative:** Private browsing (Incognito) windows where extensions are disabled require fallback to hosts file blocking (binary block without granular time tracking).
- **Mitigation:** Detect private browsing windows through window title heuristics and enforce hosts file protection for blocked domains.

---

### ADR-005: Persistence Engine for High-Frequency Activity Data: SQLite vs. Flat JSON Files

#### Status
**Accepted**

#### Context
Umbra persists all state as human-readable JSON files via `AtomicFile.cs` (`session.json`, `blocklist.json`, `periods.json`, `history.json`, `music-history.json`, `settings.json`).
While JSON works acceptably for low-frequency writes (starting/stopping a 25-minute focus session), passive digital wellbeing tracking requires recording window switch intervals and tab heartbeats every few seconds.

Attempting to store high-frequency activity records in flat JSON files results in:
1. Continuous full-file serialization and disk rewrites (megabytes written per minute).
2. O(N) memory consumption when parsing historical logs for daily/weekly/monthly analytics.
3. High risk of file corruption and write lock contention.
4. Incapacity to run complex analytical aggregations (e.g., "sum active minutes grouped by category between 09:00 and 17:00").

#### Decision
Adopt **SQLite (`Microsoft.Data.Sqlite`)** as the dedicated storage engine for passive usage tracking, while retaining `AtomicFile` JSON persistence for static user preferences and focus session state:
- Database location: `%APPDATA%\Gritwall\data\activity.db`.
- WAL (Write-Ahead Logging) mode enabled (`PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;`).
- **Privacy-by-Design Website Schema:** Exclude `UrlPath` and `Title` from the persisted schema by default. Store only normalized hostnames (`Hostname`) and minimal metadata (`BrowserProcess`, `StartTimeUtc`, `EndTimeUtc`, `DurationSeconds`). Do not collect page titles or full URLs by default.
- **Deterministic Event-Driven Flush & Periodic Checkpoint Strategy:**
  - *Event-Driven Flushing:* Immediately commit and flush open activity intervals to SQLite upon application transitions (window focus change), idle state transitions, session/workstation lock (`WM_WTSSESSION_CHANGE`), and graceful application shutdown (`App.OnExit`).
  - *Periodic Checkpointing:* Run background WAL checkpointing (`PRAGMA wal_checkpoint(PASSIVE)`) every 15 seconds during continuous steady-state focus.
- **Crash Recovery & Invariant Reconciliation:**
  - On startup, execute `RecoverIncompleteIntervals()` to reconcile unclosed intervals resulting from unexpected process termination, power loss, or OS crashes, bounding them safely to the last known heartbeat or shutdown timestamp.
- Automatic vacuuming and 90-day data retention rolling partition.

#### Consequences
- **Positive:** Sub-millisecond indexed SQL queries for complex timeline and reporting views. Low memory footprint. ACID reliability with zero risk of file truncation. Strict user privacy with zero full URL or document title exposure. Guaranteed data consistency across crashes and workstation lock events.
- **Negative:** Adds `Microsoft.Data.Sqlite` dependency (~12MB native SQLite e_sqlite3 binary).
- **Mitigation:** `Microsoft.Data.Sqlite` is the official Microsoft-supported ADO.NET provider, highly reliable, and self-contained.

---

### ADR-006: Foreground Window Monitoring: WinEventHook vs. Polling Timer

#### Status
**Accepted**

#### Context
To track application usage accurately, the application must identify which window is actively focused by the user. Two Win32 approaches exist:
1. **Periodic Polling:** A timer firing every 500ms–1000ms calling `GetForegroundWindow()`.
2. **Win32 Event Hook:** Registering `SetWinEventHook` listening to `EVENT_SYSTEM_FOREGROUND` (0x0003) and `EVENT_OBJECT_NAMECHANGE` (0x800C).

Polling either misses quick task switches (if interval is large) or incurs unnecessary CPU wakeups (if interval is small), degrading laptop battery life. `SetWinEventHook` is the native OS event-driven standard used by professional time tracking utilities (e.g., RescueTime, ActivityWatch).

#### Decision
Implement **`SetWinEventHook` on a dedicated background STA thread** with a Win32 message pump (`GetMessage`/`DispatchMessage`), accompanied by a low-frequency (5000ms) safety watchdog polling fallback:
- Captures active window transitions instantaneously (0ms latency).
- Resolves executable name and full disk path via `QueryFullProcessImageName`.
- Resolves idle state via `GetLastInputInfo`.
- Consumes <0.1% CPU during idle or active desktop usage.

#### Consequences
- **Positive:** Zero missed transitions; optimal CPU and battery efficiency; instantaneous UI timeline updates.
- **Negative:** Requires careful Win32 P/Invoke interop and a dedicated message loop thread to avoid UI thread deadlocks.
- **Mitigation:** Encapsulate the hook lifecycle in an isolated, unit-testable `WinEventHookTracker` class that communicates via asynchronous channels (`System.Threading.Channels.Channel<T>`).

---

### ADR-007: Temporal Integrity & Clock Tampering Defense: Monotonic Ticks vs. Wall-Clock UTC

#### Status
**Accepted**

#### Context
In Umbra, session remaining time and period coverage are evaluated strictly using mutable system wall-clock time:
- `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` in `Session.cs`
- `DateTime.Now.Hour * 60 + DateTime.Now.Minute` in `Periods.cs`

As identified in `SECURITY_REVIEW.md`, shifting the Windows system clock forward past the end time immediately terminates active Hard Mode sessions and scheduled periods. This is a trivial bypass for any user seeking to escape a locked session.

#### Decision
Enforce temporal integrity using **dual-clock validation with monotonic hardware tick counters**:
1. At session or period start, capture both the wall-clock baseline (`DateTimeOffset.UtcNow`) and the monotonic hardware tick counter via `Environment.TickCount64` and `Stopwatch.GetTimestamp()`.
2. Evaluate remaining duration by comparing elapsed monotonic milliseconds:
   $$\text{ElapsedMs} = \text{Environment.TickCount64} - \text{StartTick64}$$
3. If the wall-clock shifts forward by more than 60 seconds relative to elapsed monotonic ticks:
   - Detect clock tampering.
   - Do NOT terminate the session or period.
   - Continue countdown based strictly on remaining monotonic ticks.
   - Log a tamper alert in the security log.

#### Consequences
- **Positive:** Completely eliminates clock-shifting bypasses. Hard Mode sessions remain locked for their full real-world physical duration.
- **Negative:** System hibernation/sleep accumulates tick counts differently across hardware platforms.
- **Mitigation:** Monitor Windows power management events (`WM_POWERBROADCAST`) to pause or adjust monotonic timers appropriately during genuine system suspend.

---

### ADR-008: Content Classification Engine: Local Taxonomy Engine vs. Cloud Domain Lookup

#### Status
**Accepted**

#### Context
GRITWALL requires categorizing applications and websites into categories (Productive, Social Media, Gaming, Entertainment, Adult Content).
Many commercial digital wellbeing apps send URLs and process names to remote cloud classification APIs. This directly violates GRITWALL's core requirement: **Local-first, Zero-Telemetry, and Strict Privacy**.

#### Decision
Implement a **100% Offline Local Classification Engine**:
1. Pre-bundle a curated offline taxonomy of domain roots and process names for Social Media, Gaming, and Entertainment (`Categories.json`).
2. Implement adult-content filtering via:
   - Integration of an offline compressed Trie containing known adult domain hashes.
   - Automated enforcement of Family DoH resolvers (e.g., Cloudflare Family `1.1.1.3`, CleanBrowsing Adult Filter) via the existing DoH firewall rules.
   - Optional local safe search query parameter injection for Google, Bing, DuckDuckGo, and YouTube.
3. Provide full user control to override categories, assign custom application paths, or add custom domain classification rules locally.

#### Consequences
- **Positive:** Complete data privacy. Zero external network traffic. Zero latency classification lookups (<1ms).
- **Negative:** Offline lists require periodic bundled updates to stay current with newly created domains.
- **Mitigation:** Distribute compressed taxonomy updates alongside standard application updates (`Updater.cs`) without transmitting user browsing data.

