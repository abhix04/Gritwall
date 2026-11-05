# GRITWALL Product Specification

**Version:** 0.1 (pre-implementation)  
**Date:** 2026-11-05  
**Upstream:** Umbra v1.0.x by zixload  
**Status:** Requirements defined; implementation pending audit review

---

## 1. Product Vision

GRITWALL is a Windows 11 digital wellbeing and distraction-blocking application. It builds upon the open-source Umbra codebase to provide comprehensive usage tracking, intelligent time management, and strong restriction integrity — all while remaining local-first, privacy-respecting, and free of cloud accounts or telemetry.

## 2. Core Principles

1. **Local-first** — all data stays on the user's device
2. **No accounts** — no sign-up, no cloud sync, no login walls
3. **No telemetry** — no analytics, no usage reporting, no tracking pixels
4. **Transparent** — open-source, auditable, no obfuscation
5. **Respectful** — helps users build habits, not trap them

## 3. Target Platform

- Windows 10 version 2004+ (build 19041) and Windows 11
- 64-bit (x64) only
- .NET 10 self-contained (no separate runtime needed)
- Chromium browser support: Chrome, Edge, Vivaldi, Brave

## 4. Feature Matrix

### 4.1 Inherited from Umbra (Fully Implemented)

| Feature | Description | Source |
|---|---|---|
| Focus sessions (free timer) | Arbitrary-duration countdown sessions | `Session.StartCustom()` |
| Pomodoro sessions | Configurable work/break/cycles | `Session.StartPomodoro()` |
| Recurring schedules | Day-of-week + time-window periods | `Periods.cs` |
| One-time schedules | Date-specific blocking periods | `Period.Recurring=false` |
| Application blocking | Kill blocked processes every 2s | `Blocker.EnforceAppBlock()` |
| Website blocking (hosts) | Redirect domains to 127.0.0.1 | `Blocker.ApplySiteBlock()` |
| Website blocking (extension) | Manifest V3 declarativeNetRequest | `background.js` |
| DoH firewall block | Block 6 major DoH resolvers | `Blocker.ApplyDohBlockAsync()` |
| Hard mode (sessions) | Prevent stopping before timer expires | `Session.CanStop()` |
| Hard mode (schedules) | Tamper-resistant period enforcement | `WatchdogLoop.ResolveHardLockedPeriods()` |
| Always blocklist | Session-independent permanent blocks | `AlwaysBlocklist.cs` |
| Blocklist presets | Social, streaming, games, news, shopping, messaging | `BlocklistPresets.cs` |
| Saved blocklist profiles | Named reusable blocklist configurations | `SavedBlocklists` |
| Quest/task naming | Label sessions for statistics grouping | `SessionState.QuestName` |
| Focus session statistics | Day/week/month/all-time with streak tracking | `History.cs`, `StatsRange.cs` |
| Statistics navigation | Navigate through periods with day/week/month granularity | `StatsPeriod` |
| Hourly activity breakdown | 24-bucket distribution by session end time | `StatsRange.HoursIn()` |
| Music playback tracking | Per-track play time and count via GSMTC | `MusicHistory.cs`, `SpotifyControl.cs` |
| Blocked attempt tracking | Per-target blocked visit/launch counts | `BlockAttemptHistory.cs` |
| Ambient soundscapes | 22 built-in sounds with 3-sound mixing | `AmbientSoundService.cs` |
| Floating focus timer | Detachable, resizable, always-on-top | `FloatingFocusWindow.xaml` |
| Custom backgrounds | Image/video backgrounds with blur and opacity | `SettingsPage` |
| Localization | French and English | `Loc.cs` |
| Auto-updates | SHA-256 verified GitHub release downloads | `Updater.cs` |
| Smart reminders | Time-based session start suggestions | `App.CheckSmartReminder()` |
| Crash recovery | UI error tolerance + local crash reports | `CrashReporter.cs` |

### 4.2 Required by GRITWALL (Not Yet Implemented)

| # | Feature | Priority | Description |
|---|---|---|---|
| 1 | Passive app usage tracking | P0 | Track foreground application time continuously, not just during sessions |
| 2 | Individual app usage duration | P0 | Per-application daily/weekly/monthly time breakdown |
| 3 | Individual website tracking | P0 | Per-domain visit frequency and duration |
| 4 | Detailed hourly activity timelines | P1 | Continuous timeline (not just session-end buckets) |
| 5 | Daily/weekly/monthly analytics | P0 | Screen time totals with trend comparison |
| 6 | Individual app allowances | P1 | Per-app daily time limits (e.g., 30 min/day Discord) |
| 7 | Individual website allowances | P1 | Per-domain daily time limits (e.g., 15 min/day Reddit) |
| 8 | Category-based time budgets | P1 | Group apps/sites into categories with shared limits |
| 9 | Adult content filtering | P2 | Domain-based adult content blocklist |
| 10 | Social media blocking preset | P0 | ✅ Exists as preset; needs expanded domain list |
| 11 | Gaming application restrictions | P1 | Detect Steam/Epic/game processes; time-budget enforcement |
| 12 | Enhanced focus sessions | P1 | Session goals, session notes, session rating |
| 13 | Advanced recurring schedules | P1 | Multiple time windows per day; exception dates |
| 14 | Stronger restriction integrity | P1 | Service-based watchdog; harder to bypass |
| 15 | Local-first storage | P0 | ✅ Already implemented |
| 16 | Privacy without telemetry | P0 | ✅ Already implemented |
| 17 | CSV/JSON data export | P2 | Export usage data and session history |
| 18 | Idle detection | P1 | Distinguish active vs idle screen time |
| 19 | Screen time dashboard | P0 | At-a-glance daily screen time summary |
| 20 | Break reminders | P2 | Periodic break suggestions during long usage |

## 5. Data Model Extensions Required

### 5.1 New Data: Application Usage Events
```json
{
  "timestamp": 1730000000000,
  "app": "Discord.exe",
  "duration_seconds": 300,
  "idle": false
}
```

### 5.2 New Data: Website Usage Events
```json
{
  "timestamp": 1730000000000,
  "domain": "reddit.com",
  "duration_seconds": 120
}
```

### 5.3 New Data: App/Site Allowances
```json
{
  "target": "Discord.exe",
  "type": "app",
  "daily_limit_minutes": 30,
  "weekly_limit_minutes": null,
  "category": "social"
}
```

## 6. Non-Functional Requirements

| Requirement | Target |
|---|---|
| Memory usage | < 80 MB baseline |
| CPU usage (idle) | < 1% average |
| CPU usage (active tracking) | < 3% average |
| Startup time | < 3 seconds to tray |
| Data file size (1 year) | < 50 MB |
| Tracking granularity | 1-second foreground detection |
| Blocking enforcement delay | < 3 seconds |

## 7. Out of Scope (v1)

- Cloud sync or backup
- Mobile companion app
- Multi-user / family controls
- Enterprise / MDM integration
- Firefox extension support
- macOS or Linux support
- Real-time collaboration features
- AI-based usage insights

