Nuix Log Reviewer
=================

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](http://www.apache.org/licenses/LICENSE-2.0)

View the GitHub project [here](https://github.com/Nuix/Nuix-Log-Reviewer) or download the latest release [here](https://github.com/Nuix/Nuix-Log-Reviewer/releases).

# Overview

This is a [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48) application which provides a quick and easy way to parse, index and search log files produced by [Nuix Workstation](https://www.nuix.com/products/nuixworkstation) (including worker logs).

![image](https://user-images.githubusercontent.com/11775738/81017035-67325d00-8e16-11ea-91df-4b14abd1a41a.png)

# Parsing

Load one or more selected Nuix log files or a whole directory of Nuix log files (including worker logs).  A directory relative to the exe will be created containing a SQLite database and a Lucene search index.  The application will parse each log entry into a series of searchable fields:

- `line`: Line number of the first line of a given log entry (an entry may span multiple lines).
- `channel`
- `level`: Log entry level (`TRACE`,`DEBUG`,`WARN`,`ERROR`,etc).
- `source`: Typically the [Java Package](https://docs.oracle.com/javase/tutorial/java/concepts/package.html) and class name of the code which generated a given log entry.
- `content`: The message part of the log entry, may span multiple lines.
- `date`: Date of a given log entry.
- `flag`: Classifications for log entries based on same basic classification logic.

For example the following log entry:

```
2020-04-29 13:45:01.031 -0700 [AWT-EventQueue-0] 59133 INFO  com.nuix.investigator.evidence.add.i - Spawning worker.
```

Would may be indexed as follows:

| Field | Value |
|-------|-------|
| `line` | `56` |
| `channel` | `AWT-EventQueue-0` |
| `level` | `INFO` |
| `source` | `com.nuix.investigator.evidence.add.i` |
| `content` | `Spawning worker.` |
| `date` | `20200429` |

This allows for searching through log entries with a query like the following:

```
flag:worker_log AND level:error
```

Which should show a listing of entries from any worker logs you have loaded which are of level ERROR.

# Classification

The application performs basic classification of log entries.  Classifications are indexed in the field `flag` allowing for easier searching.

| Classification | Description |
|----------------|-------------|
| `disk_space` | Log entry's **Source** value contains `Disk space message received` |
| `memory` | Log entry's **Source** value contains `com.nuix.monitoring.memory` |
| `nuix_package` | Log entry's **Source** starts with `com.nuix` |
| `script` | Log entry's **Channel** value is `SCRIPT` |
| `worker_log` | Log entry's source log file's path appears to be in a "job" sub directory (where workers usually record their logs for a given job). |
| `worker_state` | Log entry's **Content** contains `Changing state`.  Helps find worker messages like `Changing state from PENDING to RUNNING` |
| `multi_line` | Log entry spans multiple lines. |

So if you were looking for log entries regarding memory, you might try searching for `flag:memory`.

# Log Entry Grid

Log entries from multiple files are presented in the user interface as a single mereged listing in chronological order.  This can be helpful when reviewing logs from a session which has not only main app logs, but also multiple worker logs.

# License

```
Copyright 2020 Nuix

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