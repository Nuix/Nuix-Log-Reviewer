Nuix Log Reviewer
=================

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](http://www.apache.org/licenses/LICENSE-2.0)

View the GitHub project [here](https://github.com/Nuix/Nuix-Log-Reviewer) or download the latest release [here](https://github.com/Nuix/Nuix-Log-Reviewer/releases).

# Overview

This is a [.NET 8](https://dotnet.microsoft.com/en-us/download) (Windows / WPF) application which provides a quick and easy way to parse, index, search, and triage log files. In addition to [Nuix Workstation](https://www.nuix.com/products/nuixworkstation) logs (including worker logs), it recognizes and parses:

- **Nuix Workstation** and **Nuix Automate** engine logs
- **Derby** database server logs (`derby-server*.log*`)
- **Nuix Adaptive Security** microservice / container logs (`adaptive-prod-*.txt`), including Kubernetes-prefixed lines

The application detects each file's format automatically, so a mixed directory of logs loads and merges into a single chronological view.

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

Nuix's rolling log appender compresses prior days' logs into archives, and a copied log directory often contains these alongside the live `*.log` files.  When you **load a directory**, any archives in it are inspected and any inner log files (matching the same name patterns) are loaded automatically — no need to extract them first.  Supported archive types: `.zip`, `.gz`, `.tar`, `.tar.gz`/`.tgz`, `.tar.bz2`/`.tbz2`, `.bz2`, `.7z`, `.rar`, `.xz`.  Password-protected archives are skipped (you are not prompted, and the rest of the load continues).  A log recovered from an archive shows in the grid's File column as `archive-name/inner-log`.

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

The **Patterns** tab mines the loaded logs into recurring message templates (with variable parts masked), so you can see the shape of what's in the logs at a glance and how often each occurs. Patterns can be expanded into sub-patterns (by worker, exception, or sub-template), shown/hidden, and drilled into. Selected patterns can be exported as a findings report (see below).

# Insights

Click **Analyze** on the **Insights** tab for automatic findings — "threads to pull on" — across the whole loaded corpus: error spikes, silence gaps, error ramp-ups, first-error onset, and resource/throttling problems (out-of-memory, GC pressure, disk space). Each finding carries a ready-to-run query; double-click to jump to the evidence.

Insights are also **user-scriptable**: drop a JavaScript `analyze(ctx)` file in `Configuration/InsightScripts/` (an Adaptive Security example ships in the box).

# Findings report

From the Patterns tab you can **Save Summary to HTML** — a self-contained, offline report of the selected patterns suitable for sharing. Each pattern shows its count, level breakdown, first/last seen, span, peak-burst window, source files, and a real example. You can optionally include an **interactive timeline** that plots each pattern over time (log-Y and bars toggles, zoom, tooltips, onset markers) along with a burst summary and lead-lag hints to help spot co-dependencies. Summaries can also be copied to the clipboard as rich HTML, Markdown, Slack, or plain text.

# Log Entry Grid

Log entries from multiple files are presented in the user interface as a single mereged listing in chronological order.  This can be helpful when reviewing logs from a session which has not only main app logs, but also multiple worker logs.

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