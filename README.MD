Nuix Log Reviewer
=================

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](http://www.apache.org/licenses/LICENSE-2.0)

View the GitHub project [here](https://github.com/Nuix/Nuix-Log-Reviewer) or download the latest release [here](https://github.com/Nuix/Nuix-Log-Reviewer/releases).

# Overview

This is a [.NET 8](https://dotnet.microsoft.com/en-us/download) (Windows / WPF) application which provides a quick and easy way to parse, index, search, and triage log files. In addition to [Nuix Workstation](https://www.nuix.com/products/nuixworkstation) logs (including worker logs), it recognizes and parses:

- **Nuix Workstation** and **Nuix Automate** engine logs — including the Automate **engine-server**, **scheduler**, and per-job / init engine logs (and their daily rollups), plus legacy **Rampiva**-branded scheduler/engine-server logs
- **Derby** database server logs (`derby-server*.log*`)
- **Nuix Adaptive Security** microservice / container logs (`adaptive-prod-*.txt`), including Kubernetes-prefixed lines

The application detects each file's format automatically, so a mixed directory of logs loads and merges into a single chronological view. Some Automate engine per-job/init logs record only a time-of-day (no date); these are recognized too, with the calendar date recovered from the log's own context and rolled forward across midnight.

Beyond search, it mines the loaded logs into recurring **Patterns**, surfaces automatic **Insights** ("threads to pull on"), and can export a self-contained, shareable **HTML findings report** with an interactive timeline. Both classification and insights are **user-scriptable**.

<img width="1010" height="761" alt="Nuix Log Reviewer" src="https://github.com/user-attachments/assets/a90b99b9-ee1f-4464-8aa4-19a4769a3447" />

# Parsing

Load one or more selected log files or a whole directory of log files (including worker logs).  A directory relative to the exe will be created containing a SQLite database and a Lucene search index.  The application detects each file's format and parses every log entry into a series of searchable fields:

- `line`: Line number of the first line of a given log entry (an entry may span multiple lines).
- `channel`
- `level`: Log entry level (`TRACE`,`DEBUG`,`INFO`,`WARN`,`ERROR`, ...). Levels from other formats are normalized (e.g. Adaptive `EROR`/`WARN`/`INFORMATION`).
- `source`: Typically the [Java Package](https://docs.oracle.com/javase/tutorial/java/concepts/package.html) and class name (or the .NET source) of the code which generated a given log entry.
- `content`: The message part of the log entry, may span multiple lines.
- `timestamp`: Event time of a given log entry (supports dot- and comma-millisecond formats).
- `flag`: Classifications for log entries based on classification logic (built-in and user-scripted).

For example the following log entry:

```
2020-04-29 13:45:01.031 -0700 [AWT-EventQueue-0] 59133 INFO  com.nuix.investigator.evidence.add.i - Spawning worker.
```

Would be indexed as follows:

| Field | Value |
|-------|-------|
| `line` | `56` (or whatever line this was on) |
| `channel` | `AWT-EventQueue-0` |
| `level` | `INFO` |
| `source` | `com.nuix.investigator.evidence.add.i` |
| `content` | `Spawning worker.` |
| `timestamp` | `2020-04-29 13:45:01.031` |

This allows for searching through log entries with a query like the following:

```
flag:worker_log AND level:error
```

Which should show a listing of entries from any worker logs you have loaded which are of level ERROR.

## Archives

Nuix's rolling log appender compresses prior days' logs into archives, and a copied log directory often contains these alongside the live `*.log` files.  When you **load a directory** — or **select an archive file directly** — any archives are inspected and any inner log files (matching the same name patterns) are loaded automatically, with no need to extract them first.  Archives **nested inside archives** are handled too: a support bundle whose entries include daily `engine-server.<date>.log.zip` / `scheduler.<date>.log.zip` rollups is recursed into, so those logs come in as well.  Supported archive types: `.zip`, `.gz`, `.tar`, `.tar.gz`/`.tgz`, `.tar.bz2`/`.tbz2`, `.bz2`, `.7z`, `.rar`, `.xz`.  Password-protected archives are skipped (you are not prompted, and the rest of the load continues).  An inner log's folder structure is preserved, so the grid's File column shows a disambiguated path such as `archive-name/gateway/nuix.log` (rather than colliding same-named logs), and its **tooltip shows the real origin** — the source archive's full path plus the inner path (e.g. `C:\logs\support.zip → gateway/nuix.log`, or for a nested archive `C:\logs\support.zip → engine.<date>.log.zip → engine.log`) rather than the throwaway temp-extraction path.  Extraction and directory scanning run in the background, so the progress dialog appears immediately.

# Classification

The application performs classification of log entries.  Classifications are indexed in the field `flag` allowing for easier searching.  A selection of the built-in classifiers:

| Classification | Description |
|----------------|-------------|
| `disk_space` | Log entry's **Source** value contains `Disk space message received` |
| `memory` | Log entry's **Source** value contains `com.nuix.monitoring.memory` |
| `nuix_package` | Log entry's **Source** starts with `com.nuix` |
| `script` | Log entry's **Channel** value is `SCRIPT` |
| `worker_log` | Log entry's source log file's path appears to be in a "job" sub directory (where workers usually record their logs for a given job). |
| `worker_state` | Log entry's **Content** contains `Changing state`.  Helps find worker messages like `Changing state from PENDING to RUNNING` |
| `multi_line` | Log entry spans multiple lines. |
| `process_killed` | Log entry's **Content** starts with `Killing current process...` |
| `problem_processing_item` | Log entry's **Content** starts with `Problem processing item` |
| `session_is_closed` | Log entry's **Content** contains `The Session is closed` |
| `null_pointer` | A `NullPointerException`. |
| `http_timeout`, `http_forbidden`, `http_unauthorized`, `connection_refused` | Common network / HTTP failure signals. |
| `engine_load_failure`, `engine_comm_failure`, `license`, `third_party_rest`, `oidc_sync_failure`, `google_oidc` | Engine, licensing, and identity/integration signals. |

So if you were looking for log entries regarding memory, you might try searching for `flag:memory`.

You can also add your **own** classifiers as sandboxed JavaScript files in `Configuration/ClassifierScripts/` — each exposes a `classify(entry)` function returning one or more flags. See the in-app **User Guide** (Help menu) for the scripting reference. A ready-to-copy Adaptive Security classifier ships as an example.

# Patterns

The **Patterns** tab mines the loaded logs into recurring message templates (with variable parts masked), so you can see the shape of what's in the logs at a glance and how often each occurs. Each pattern's **Count** is color-coded by its dominant level (ERROR/WARN/INFO/DEBUG) for an at-a-glance severity read, with the full per-level breakdown in the ERR/WARN/INFO/DBG columns. A **quick-filter** box narrows the listed patterns to those whose template contains the typed text (case-insensitive) — a fast way to find a specific message shape without recomputing. Patterns can be expanded into sub-patterns (by worker, exception, or sub-template), shown/hidden, and drilled into. Selected patterns can be exported as a findings report (see below).

# Jobs & Workflows

The **Jobs / Workflows** tab reconstructs two related units of Automate/engine activity from the current results:

- **Worker jobs** — grouped by the `job-<id>` worker folder, with each job's lifespan, **worker count** (distinct `worker-N` lanes, so restarts of the same worker aren't double-counted), and entry/warn/error counts.
- **Workflow executions** — Automate engine-log workflow runs, delineated by the engine's own lifecycle logging. Click the expander arrow on a workflow row to show its per-operation breakdown with individual durations, so a long-running step stands out at a glance. Where the engine log names the worker job an operation waited on, that link is shown. Workflows are recognized whether the engine logs the fully-qualified logger name or the abbreviated form used in per-job engine logs.

A workflow may launch one or more worker jobs. On the timeline they appear as time-aligned tracks — workflows above, worker jobs below — so activity within a workflow is easy to place. The two are kept independent (no parent/child link is inferred from timing alone).

Above the worker-jobs list, a **concurrency summary** reports whole-scope worker usage: the **peak** number of workers running at once across jobs that overlapped in time (with the window it held), the **minimum** while any job was active, and the time-weighted **average**, plus the total worker-lanes seen. Because jobs can run in parallel, the peak reflects simultaneous workers across concurrent jobs — the figure that matters for sizing an engine's worker pool.

# Insights

Click **Analyze** on the **Insights** tab for automatic findings — "threads to pull on" — across the whole loaded corpus: error spikes, silence gaps, error ramp-ups, first-error onset, and resource/throttling problems (out-of-memory, GC pressure, disk space). Each finding carries a ready-to-run query; double-click to jump to the evidence.

Two Automate-aware insight scripts ship in the box and build on the Jobs / Workflows data:

- **Workflow insights** — flags workflow executions that started but never finished (a crash or truncated log), and correlates each workflow operation with the exact WARN/ERROR counts logged while it was running, so a problematic step (e.g. an "Add Evidence" that logged the errors) is called out with its own totals rather than a workflow-wide lump sum.
- **Worker-timeout insights** — surfaces engine watchdog timeouts ("Worker has not progressed within timeout of: N seconds") and, per job, the number of timeouts and total stall time, noted across the job's parallel workers so the raw stall figures read correctly even when they exceed the job's wall-clock runtime.

Insights are also **user-scriptable**: drop a JavaScript `analyze(ctx)` file in `Configuration/InsightScripts/`. Beyond the per-entry stream and pattern data, the script context now also exposes the mined **workflows**, the level-over-**timeline** buckets, and **workerTimeouts**, so scripts can reason about Automate activity directly. An Adaptive Security example and the two Automate scripts above ship as ready-to-copy references.

# Findings report

From the Patterns tab you can **Save Summary to HTML** — a self-contained, offline report of the selected patterns suitable for sharing. Each pattern shows its count, level breakdown, first/last seen, span, peak-burst window, source files, and a real example. You can optionally include an **interactive timeline** that plots each pattern over time (log-Y and bars toggles, zoom, tooltips, onset markers) along with a burst summary and lead-lag hints to help spot co-dependencies. Summaries can also be copied to the clipboard as rich HTML, Markdown, Slack, or plain text.

# Log Entry Grid

Log entries from multiple files are presented in the user interface as a single mereged listing in chronological order.  This can be helpful when reviewing logs from a session which has not only main app logs, but also multiple worker logs.

**Right-click a row** for a **Filter to ▸** menu that narrows the current results by that entry's file, job, source, day, level, or flag. Filters are added on top of the current query (so you can stack them to drill in), and **Back** undoes the last one. **Copy** puts the row's text on the clipboard.

Beneath the grid, the **timeline** chart plots log volume over the loaded time range, with **day-boundary dividers** so multi-day sessions are easy to read. When a **Find within results** term is active, its matching rows are also marked on the timeline as magenta ticks (matching the grid's magenta match dot), so you can see where matches cluster. When the current results are time-bounded, an **⤢ Expand** button widens the range around what you're viewing — either by a fixed amount (+1m/+5m/+15m/+1h/+1d) or by a multiplier (×2/×5/×10 of the current span) — to pull in the surrounding context without starting a new search.

# License

```
Copyright 2026 Nuix

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```