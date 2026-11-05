# GRITWALL — Comprehensive Security & Privacy Audit

**Document Version:** 1.0.0  
**Target Codebase:** Umbra (.NET 10, C# 14, WPF, WinUI)  
**Repository:** `D:\Projects\Gritwall`  
**Current Git Commit:** `5978b0d`  
**Auditor:** Senior Security Architect  
**Status:** Complete Independent Audit  

---

## 1. Executive Summary

This security and privacy audit examines the complete source code of the Umbra desktop application, background watchdog, native messaging host, and browser extension. 

### Key Findings
1. **Zero Telemetry Verified:** The application is completely free of telemetry, analytics, user profiling, and third-party tracking libraries. User privacy is fundamentally respected.
2. **Elevated Privilege Boundary Flaw:** The elevated watchdog process (`Umbra.exe --watchdog`), which possesses administrator privileges to modify the Windows `hosts` file and firewall rules, can be terminated by any unprivileged standard user via a plaintext signal file (`watchdog.stop-request`).
3. **Unauthenticated Configuration State:** Core blocking rules and active Hard Mode session state are persisted in plaintext, unsigned JSON files in the user profile (`%APPDATA%`), allowing trivial bypass through file editing or deletion.
4. **Wall-Clock Vulnerability:** Session duration and recurring schedules rely strictly on mutable system wall-clock time (`DateTimeOffset.UtcNow`), enabling instant bypass by advancing the Windows system clock.
5. **DNS-over-HTTPS (DoH) Evasion:** The firewall rule blocking DoH only targets 6 static IPv4 addresses, leaving IPv6 DoH, alternative DNS resolvers, and DNS-over-TLS (port 853) unrestricted.

---

## 2. Threat Model: The Dual-Role User Paradigm

In conventional endpoint security, the user is defended against malicious external adversaries. In digital wellbeing and distraction management systems like GRITWALL, the threat model is inverted:
- **The Authorized User is Also the Adversary:** The user willingly installs the application and configures restrictions to foster focus and discipline. However, during moments of acute distraction, cravings, or deadline fatigue, that same user becomes an active, motivated adversary seeking bypass mechanisms.
- **Integrity Goals:** The enforcement mechanism must resist impulse-driven evasion ("friction-first" protection) while maintaining system stability and preventing unrecoverable lockouts.
- **Attacker Capabilities:** The user has standard user rights, physical access to the machine, keyboard/mouse input, access to file explorers, Task Manager, browser developer tools, and optionally local administrator rights.

---

## 3. Exhaustive Bypass & Vulnerability Matrix

| ID | Attack Vector | Privilege Required | Exploit Mechanism | Code Location | Difficulty | Severity |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **SEC-01** | **Signal File Watchdog Backdoor** | Standard User | Create empty file `%APPDATA%\UmbraNative\data\watchdog.stop-request` | [`WatchdogLoop.cs:332`](file:///D:/Projects/Gritwall/Umbra.Core/WatchdogLoop.cs#L332) | Trivial (1 CLI command) | **Critical** |
| **SEC-02** | **Session File Deletion / Modification** | Standard User | Delete `session.json` or edit `"hardMode": false` / `"active": false` | [`Session.cs:40`](file:///D:/Projects/Gritwall/Umbra.Core/Session.cs#L40), [`WatchdogLoop.cs:125`](file:///D:/Projects/Gritwall/Umbra.Core/WatchdogLoop.cs#L125) | Trivial (Notepad / File Explorer) | **High** |
| **SEC-03** | **Blocklist File Emptying** | Standard User | Edit `blocklist.json` and set `"apps": []`, `"sites": []` | [`Blocklist.cs:45`](file:///D:/Projects/Gritwall/Umbra.Core/Blocklist.cs#L45), [`WatchdogLoop.cs:158`](file:///D:/Projects/Gritwall/Umbra.Core/WatchdogLoop.cs#L158) | Trivial | **High** |
| **SEC-04** | **System Clock Advancement** | Standard / Admin | Advance Windows system clock past session `EndTs` or period end time | [`Session.cs:52`](file:///D:/Projects/Gritwall/Umbra.Core/Session.cs#L52), [`Periods.cs:110`](file:///D:/Projects/Gritwall/Umbra.Core/Periods.cs#L110) | Low (Settings UI) | **High** |
| **SEC-05** | **Process Executable Renaming** | Standard User | Rename blocked `.exe` (e.g., `steam.exe` $\rightarrow$ `steam_game.exe`) | [`Blocker.cs:150`](file:///D:/Projects/Gritwall/Umbra.Core/Blocker.cs#L150) | Low | **Medium** |
| **SEC-06** | **DNS-over-HTTPS (DoH) via IPv6** | Standard User | Enable DoH in browser; resolves over IPv6 bypassing IPv4 firewall rule | [`Config.cs:70`](file:///D:/Projects/Gritwall/Umbra.Core/Config.cs#L70), [`Blocker.cs:90`](file:///D:/Projects/Gritwall/Umbra.Core/Blocker.cs#L90) | Low | **High** |
| **SEC-07** | **Alternative DoH Resolvers** | Standard User | Configure browser to use NextDNS, AdGuard, or OpenDNS DoH URLs | [`Config.cs:70`](file:///D:/Projects/Gritwall/Umbra.Core/Config.cs#L70) | Low | **High** |
| **SEC-08** | **Extension Disablement & Incognito** | Standard User | Disable extension in `chrome://extensions` or open Incognito window | [`background.js:15`](file:///D:/Projects/Gritwall/Umbra.App/browser-extension/background.js#L15) | Low | **Medium** |
| **SEC-09** | **Unmanaged Browser Usage** | Standard User | Launch Mozilla Firefox, Tor Browser, or Brave without extension | [`BrowserIntegration.cs:18`](file:///D:/Projects/Gritwall/Umbra.App/BrowserIntegration.cs#L18) | Low | **Medium** |
| **SEC-10** | **Watchdog Task Termination** | Administrator | Open Task Manager as Admin $\rightarrow$ End Task on elevated `Umbra.exe` | OS Process Boundary | Low | **High** |
| **SEC-11** | **Reactive Polling Spawn Window** | Standard User | Launch game repeatedly; runs for up to 2.0 seconds between watchdog ticks | [`WatchdogLoop.cs:13`](file:///D:/Projects/Gritwall/Umbra.Core/WatchdogLoop.cs#L13) | Trivial | **Low** |
| **SEC-12** | **Uninstall During Active Hard Mode** | Standard User | Run `unins000.exe`; leaves hosts file and firewall permanently blocked | [`installer/Umbra.iss:172`](file:///D:/Projects/Gritwall/installer/Umbra.iss#L172) | Low | **High** |

---

## 4. Deep Technical Analysis of Identified Vulnerabilities

---

### SEC-01: The Signal File Watchdog Backdoor

#### Root Cause
In [`WatchdogLoop.cs`](file:///D:/Projects/Gritwall/Umbra.Core/WatchdogLoop.cs#L332-L340):
```csharp
if (File.Exists(Config.WatchdogStopRequestFile))
{
    Log("watchdog stopping for update");
    await enforcer.CleanupAsync();
    try { File.Delete(Config.WatchdogStopRequestFile); } catch { }
    try { File.Delete(Config.WatchdogPidFile); } catch { }
    try { File.WriteAllText(Config.WatchdogStoppedFile, DateTime.UtcNow.ToString("O")); } catch { }
    return;
}
```

`Config.WatchdogStopRequestFile` is defined as:
```csharp
Path.Combine(DataDir, "watchdog.stop-request")
```
Where `DataDir` resides under `%APPDATA%\UmbraNative\data\`.

#### Exploitation
The directory `%APPDATA%` belongs entirely to the logged-in unprivileged user. A user can bypass any active Hard Mode session, regardless of remaining time or locks, by opening a command prompt and typing:
```cmd
echo stop > "%APPDATA%\UmbraNative\data\watchdog.stop-request"
```
Within 2,000 milliseconds, the elevated watchdog detects the file, invokes `CleanupAsync()` (which strips all block entries from `C:\Windows\System32\drivers\etc\hosts` and deletes the `UmbraNativeBlockSecureDNS` firewall rule), and cleanly terminates its own process.

#### Remediation
1. Remove all file-based stop triggers immediately.
2. Transition control signaling to an authenticated Windows Named Pipe (`\\.\pipe\GritwallWatchdog`) with explicit ACLs denying unprivileged termination requests when Hard Mode is active.

---

### SEC-02 & SEC-03: Unauthenticated Configuration Tampering

#### Root Cause
In [`WatchdogLoop.cs`](file:///D:/Projects/Gritwall/Umbra.Core/WatchdogLoop.cs#L125-L161):
```csharp
var session = Session.Load();
// ...
var globalBl = Blocklist.Load();
foreach (var a in globalBl.Apps) apps.Add(a);
foreach (var st in globalBl.Sites) sites.Add(st);
```
Every 2 seconds, `Session.Load()` and `Blocklist.Load()` re-read JSON directly from `%APPDATA%\UmbraNative\data\session.json` and `blocklist.json`.

#### Exploitation
- Modifying `session.json` to `{"active": false}` causes the watchdog to believe the session has ended, immediately clearing all active blocks.
- Deleting `blocklist.json` or editing `apps: []` and `sites: []` causes the watchdog to rewrite `hosts` with zero blocked domains and kill zero processes.
- While `Periods.cs` has partial in-memory protection via `_trustedHardPeriods`, this protection is wiped if the watchdog is restarted or stopped via SEC-01.

#### Remediation
1. Store active session lock records in a protected file signed with an HMAC key accessible only to the elevated watchdog or encrypted via Windows DPAPI (`DataProtectionScope.LocalMachine`).
2. When the UI writes a blocklist change during an active Hard Mode session, the watchdog must reject the change and retain the in-memory block targets established at session start.

---

### SEC-04: System Wall-Clock Spoofing

#### Root Cause
In [`Session.cs`](file:///D:/Projects/Gritwall/Umbra.Core/Session.cs#L52-L144):
```csharp
private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

public static double RemainingSeconds(SessionState s)
{
    if (!s.Active) return 0;
    return Math.Max(0, (s.EndTs - NowMs) / 1000.0);
}
```
And in [`Periods.cs`](file:///D:/Projects/Gritwall/Umbra.Core/Periods.cs#L110):
```csharp
var mins = now.Hour * 60 + now.Minute;
```

#### Exploitation
The session remaining time is computed as a direct subtraction against current wall-clock UTC time. Moving the Windows clock forward by 30 minutes in `ms-settings:dateandtime`:
1. Instantly forces `RemainingSeconds(s) <= 0`.
2. Sets `Session.CanStop(s) == true`.
3. Causes `WatchdogLoop.TickAsync()` to conclude the session has expired and lift all blocks.

#### Remediation
Implement dual-clock verification using hardware monotonic tick counters:
```csharp
long startTick = Environment.TickCount64;
// Elapsed real time in milliseconds:
long elapsedMs = Environment.TickCount64 - startTick;
```
If the wall-clock shifts significantly while `Environment.TickCount64` indicates insufficient physical time has passed, flag clock manipulation and enforce the full duration based on monotonic ticks.

---

### SEC-06 & SEC-07: DNS-over-HTTPS (DoH) Filtering Gaps

#### Root Cause
In [`Config.cs`](file:///D:/Projects/Gritwall/Umbra.Core/Config.cs#L70-L75):
```csharp
public static readonly string[] DohBlockIps =
{
    "1.1.1.1", "1.0.0.1", // Cloudflare
    "8.8.8.8", "8.8.4.4", // Google
    "9.9.9.9", "149.112.112.112", // Quad9
};
```
And in [`Blocker.cs`](file:///D:/Projects/Gritwall/Umbra.Core/Blocker.cs#L94-L96):
```csharp
var ips = string.Join(",", Config.DohBlockIps);
var args = $"advfirewall firewall add rule name=\"{Config.DohFirewallRuleName}\" dir=out action=block remoteip={ips} protocol=TCP remoteport=443";
```

#### Exploitation
1. **IPv6 DoH:** Cloudflare (`2606:4700:4700::1111`) and Google (`2001:4860:4860::8888`) provide DoH over IPv6. On modern Windows 11 machines with dual-stack IPv4/IPv6 connections, browsers query DoH over IPv6, completely bypassing the IPv4 firewall rule.
2. **Alternative Providers:** NextDNS, AdGuard (`94.140.14.14`), CleanBrowsing, OpenDNS (`208.67.222.222`), and custom private DoH resolvers are completely unblocked.
3. **DNS-over-TLS (DoT):** Port 853 is not blocked.

#### Remediation
1. Expand the firewall block rule to include IPv6 addresses for all major DoH resolvers.
2. Add an outbound block rule for UDP/TCP port 853.
3. Configure group policy or registry entries (`Software\Policies\Google\Chrome\DnsOverHttpsMode = "off"`, `Software\Policies\Microsoft\Edge\DnsOverHttpsMode = "off"`) during active Hard Mode sessions if the browser extension is uninstalled.

---

### SEC-12: Uninstaller State Corruption During Active Hard Mode

#### Root Cause
In [`installer/Umbra.iss`](file:///D:/Projects/Gritwall/installer/Umbra.iss#L23-L182):
- Line 23: `PrivilegesRequired=lowest`
- Line 172-182:
  ```pascal
  Exec('taskkill.exe', '/IM Umbra.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  ```

#### Exploitation
1. The uninstaller runs at standard user integrity (`lowest`).
2. When it executes `taskkill.exe /IM Umbra.exe /F`, the command fails against the elevated watchdog process with `ERROR: Access is denied`.
3. The uninstaller proceeds to delete the application binaries from `%LOCALAPPDATA%\Programs\Umbra`.
4. It does not invoke `Blocker.RemoveSiteBlock()` or `Blocker.RemoveDohBlockAsync()`.
5. **Result:** The system `C:\Windows\System32\drivers\etc\hosts` file remains modified, and the outbound firewall block rule remains permanently active, leaving the user with permanently blocked websites and no installed app to remove them.

#### Remediation
1. Introduce an `InitializeUninstall()` check in Inno Setup that inspects `session.json` and `periods.json`. If Hard Mode is active, abort uninstallation with a clear diagnostic dialog.
2. Request elevation for uninstallation if cleanup of system `hosts` or firewall rules is required.
3. Unconditionally execute cleanup routines in `CurUninstallStepChanged(usPostUninstall)`.

---

## 5. Privacy and Network Call Audit

A rigorous static analysis of all outbound network connections was conducted across all files.

### Network Behavior Audit Table

| Source File | Function / Method | Destination URL / Endpoint | Purpose | User Data Sent | Telemetry? |
| :--- | :--- | :--- | :--- | :--- | :--- |
| [`Updater.cs`](file:///D:/Projects/Gritwall/Umbra.Core/Updater.cs#L45) | `CheckUpdateAsync()` | `https://api.github.com/repos/zixload/umbra/releases/latest` | Version update check | Standard GitHub HTTP headers (`User-Agent: Umbra-App`) | **No** |
| [`Updater.cs`](file:///D:/Projects/Gritwall/Umbra.Core/Updater.cs#L148) | `DownloadAndRunInstallerAsync()` | GitHub Release asset URL | Downloads new installer binary | None | **No** |
| [`CrashReporter.cs`](file:///D:/Projects/Gritwall/Umbra.Core/CrashReporter.cs#L22) | `LogCrash()` | Local file only (`%APPDATA%\...\logs\crash-*.log`) | Local crash diagnostics | None — written strictly to local disk | **No** |
| [`PiperTts.cs`](file:///D:/Projects/Gritwall/Umbra.Core/PiperTts.cs#L104) | `DownloadModelAsync()` | `https://huggingface.co/rhasspy/piper-voices/...` | Downloads offline voice model | None (only GET request for model files) | **No** |
| [`SpotifyControl.cs`](file:///D:/Projects/Gritwall/Umbra.Core/SpotifyControl.cs) | Media transport controls | Local Win32 `SendMessage` (`WM_APPCOMMAND`) | Spotify desktop play/pause/skip | None (local window message) | **No** |
| [`BrowserHost`](file:///D:/Projects/Gritwall/Umbra.BrowserHost/Program.cs) | Native Messaging loop | Standard I/O streams (`Console.OpenStandardInput`) | Local browser communication | None (local pipe stdin/stdout) | **No** |

### Privacy Verdict
- **Confirmed Zero Telemetry:** The application makes **zero network requests** during normal operation, tracking, focus sessions, or blocking.
- **Privacy-by-Design Website Activity Storage:** Website tracking is strictly constrained to normalized hostnames (e.g., `github.com`) and minimal duration metadata. Full URLs, URL paths, query parameters, URL fragments, and page document titles are explicitly stripped and never persisted by default, preventing sensitive browsing data from being captured in local logs.
- The only outbound network requests occur during explicit user actions:
  1. Manual or automatic update check against the public GitHub Releases API.
  2. Downloading an offline speech synthesis voice model from Hugging Face.
- No personal identifying information (PII), browser history, application names, keystrokes, or usage statistics ever leave the host machine.

---

## 6. Third-Party Dependency Security Audit

Umbra maintains an exceptionally lean dependency profile with only 3 external NuGet packages:

| Package | Version | License | Vulnerability Scan (CVEs) | Risk Assessment |
| :--- | :--- | :--- | :--- | :--- |
| **`Wpf.Ui`** | `4.3.0` | MIT | 0 known vulnerabilities | Low risk. Modern WinUI-style controls for WPF. Safe. |
| **`Microsoft.Toolkit.Uwp.Notifications`** | `7.1.3` | MIT | 0 known vulnerabilities | Low risk. Official Microsoft notification library. Safe. |
| **`NAudio.Wasapi`** | `2.2.1` | MIT | 0 known vulnerabilities | Low risk. Unmanaged audio output for ambient noise. Safe. |

### Conclusion
The existing dependency footprint is minimal, modern, and completely clean of known security advisories or supply-chain vulnerabilities.

---

## 7. Hardening Recommendations for GRITWALL

To transform the distraction blocker into an enterprise-grade, tamper-resistant digital wellbeing tool, the following defense-in-depth mitigations must be implemented in order of priority:

1. **Deprecate `watchdog.stop-request`:** Enforce watchdog shutdown exclusively through a secure Named Pipe guarded by Windows ACLs and token authentication.
2. **Implement Monotonic Hardware Timers:** Compute all remaining times and period coverages using `Environment.TickCount64` and `Stopwatch.GetTimestamp()` to prevent clock-shifting bypasses.
3. **Guard the Uninstaller:** Prevent Inno Setup from uninstalling while an active Hard Mode session is underway, and ensure unconditional hosts/firewall restoration.
4. **Expand Firewall Rules for Dual-Stack DoH:** Add IPv6 DoH addresses and port 853 blocking to the Windows Firewall rule.
5. **Protect Configuration Files:** Hash or sign active session records to detect manual file tampering.

