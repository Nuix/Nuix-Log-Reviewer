using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Inspects archive files (zip/gz/tar/tar.gz/tbz2/bz2/7z/... — anything SharpCompress reads) that a
    /// customer's log directory might contain (Nuix's rolling log appender produces zip/gz rollups for
    /// prior days), and extracts any INNER entries whose names match the same structured-log name
    /// patterns used for loose files. Extracted files are written under a caller-provided temp directory
    /// (the repo's own folder, so the existing orphan sweep cleans them up) and their paths are fed into
    /// the normal load pipeline.
    ///
    /// Design notes / safety:
    /// <list type="bullet">
    /// <item>Password-protected entries are skipped silently (never prompt, never fail the load).</item>
    /// <item>Entry names are matched by BASENAME against the caller's DOS-style globs (same semantics as
    /// <c>Directory.EnumerateFiles</c>), so inner logs are recognized exactly like loose ones. Their
    /// INNER RELATIVE PATH is then preserved on disk (not flattened), so same-named logs in different
    /// archive folders stay distinct and the downstream worker-log/path and workflow detection work.</item>
    /// <item>Zip-slip is prevented: the resolved output path must stay within the extraction root.</item>
    /// <item>Nested archives (an archive whose entries include other archives, e.g. daily
    /// "engine-server.&lt;date&gt;.log.zip" rollups inside a support bundle) are recursed into, bounded by
    /// a small depth cap plus the per-entry/total size caps.</item>
    /// <item>Per-entry and total extraction size are capped to bound a decompression bomb.</item>
    /// <item>Everything is best-effort: a broken/partial/unsupported archive is skipped, never throws
    /// out of <see cref="ExtractMatchingLogs"/>.</item>
    /// </list>
    /// Kept free of WPF so the matching/extraction can be harness-tested directly.
    /// </summary>
    public static class ArchiveLogExtractor
    {
        /// <summary>Archive file extensions we attempt to open (superset of what Nuix produces).</summary>
        private static readonly string[] ArchiveExtensions =
        {
            ".zip", ".gz", ".tgz", ".tar", ".bz2", ".tbz2", ".7z", ".rar", ".xz", ".lzma",
        };

        /// <summary>Per-entry extraction cap (2 GiB) and per-archive total cap (8 GiB) to bound bombs.</summary>
        private const long MaxEntryBytes = 2L * 1024 * 1024 * 1024;
        private const long MaxArchiveBytes = 8L * 1024 * 1024 * 1024;

        /// <summary>
        /// How deep to recurse into archives-within-archives. Nuix log bundles nest at most one level
        /// (an outer support zip whose entries include daily "engine-server.&lt;date&gt;.log.zip" rollups),
        /// so a small cap recovers those while bounding pathological nesting / zip bombs.
        /// </summary>
        private const int MaxNestDepth = 4;

        /// <summary>An inner log recovered from an archive: where it was extracted + how to label it.</summary>
        public sealed class ExtractedLog
        {
            /// <summary>Absolute path of the extracted temp file (fed to the load pipeline).</summary>
            public string ExtractedPath;
            /// <summary>Display name, e.g. "logs-2026-09-20.zip!sub/nuix.log", for the grid's file column.</summary>
            public string DisplayName;
        }

        /// <summary>True if the path looks like an archive we should inspect (by extension).</summary>
        public static bool IsArchive(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileName(path).ToLowerInvariant();
            foreach (var ext in ArchiveExtensions)
            {
                if (name.EndsWith(ext, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Finds every archive under <paramref name="directory"/> (recursively) and extracts inner logs
        /// matching <paramref name="namePatterns"/> (and not in <paramref name="excludedNames"/>) into a
        /// fresh subfolder of <paramref name="extractRoot"/>. Returns the extracted logs (possibly empty).
        /// Never throws; per-archive failures are reported via <paramref name="onWarning"/> if provided.
        /// </summary>
        public static IReadOnlyList<ExtractedLog> ExtractFromDirectory(
            string directory,
            string extractRoot,
            IReadOnlyList<string> namePatterns,
            ISet<string> excludedNames,
            Action<string> onWarning = null)
        {
            var results = new List<ExtractedLog>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return results;

            string[] archives;
            try
            {
                archives = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(IsArchive)
                    .ToArray();
            }
            catch (Exception ex)
            {
                onWarning?.Invoke("Could not scan for archives: " + ex.Message);
                return results;
            }

            foreach (var archive in archives)
            {
                try
                {
                    results.AddRange(ExtractMatchingLogs(archive, extractRoot, namePatterns, excludedNames, onWarning));
                }
                catch (Exception ex)
                {
                    // Belt-and-suspenders: ExtractMatchingLogs is already fail-soft, but never let one
                    // archive abort the whole directory load.
                    onWarning?.Invoke($"Skipped archive '{Path.GetFileName(archive)}': {ex.Message}");
                }
            }
            return results;
        }

        /// <summary>
        /// Extracts the matching inner logs from a single archive. Each archive gets its own uniquely
        /// named subfolder under <paramref name="extractRoot"/> so entries from different archives with
        /// the same inner name (e.g. two "nuix.log") never collide. Inner entries that are THEMSELVES
        /// archives (e.g. daily "engine-server.&lt;date&gt;.log.zip" rollups inside a support bundle) are
        /// recursed into, up to <see cref="MaxNestDepth"/> levels, so their logs come in too.
        /// </summary>
        public static IReadOnlyList<ExtractedLog> ExtractMatchingLogs(
            string archivePath,
            string extractRoot,
            IReadOnlyList<string> namePatterns,
            ISet<string> excludedNames,
            Action<string> onWarning = null)
        {
            return ExtractMatchingLogs(archivePath, extractRoot, namePatterns, excludedNames, onWarning, 0);
        }

        private static IReadOnlyList<ExtractedLog> ExtractMatchingLogs(
            string archivePath,
            string extractRoot,
            IReadOnlyList<string> namePatterns,
            ISet<string> excludedNames,
            Action<string> onWarning,
            int depth)
        {
            var results = new List<ExtractedLog>();
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath)) return results;

            string archiveName = Path.GetFileName(archivePath);

            // Use the streaming ReaderFactory rather than the random-access ArchiveFactory: it handles
            // every format we care about uniformly INCLUDING compressed-tar layering (.tar.gz/.tar.bz2),
            // which ArchiveFactory can't always sniff. Forward-only is fine — we extract each entry once.
            var ctx = new ExtractContext
            {
                ArchivePath = archivePath,
                ArchiveName = archiveName,
                ExtractRoot = extractRoot,
                NamePatterns = namePatterns,
                ExcludedNames = excludedNames,
                OnWarning = onWarning,
                Results = results,
                Depth = depth,
            };

            try
            {
                using (var stream = File.OpenRead(archivePath))
                using (var reader = SharpCompress.Readers.ReaderFactory.OpenReader(stream, new SharpCompress.Readers.ReaderOptions()))
                {
                    while (reader.MoveToNextEntry())
                    {
                        var entry = reader.Entry;
                        if (entry == null || entry.IsDirectory) continue;

                        if (entry.IsEncrypted)
                        {
                            onWarning?.Invoke($"Skipped encrypted entry in '{archiveName}'.");
                            continue;
                        }

                        string innerName = InnerFileName(entry.Key, archivePath);
                        if (string.IsNullOrEmpty(innerName)) continue;

                        if (entry.Size > MaxEntryBytes)
                        {
                            onWarning?.Invoke($"Skipped oversized entry '{innerName}' in '{archiveName}'.");
                            continue;
                        }
                        if (ctx.ExtractedTotal + Math.Max(0, entry.Size) > MaxArchiveBytes)
                        {
                            onWarning?.Invoke($"Archive '{archiveName}' exceeded the extraction size cap; remaining entries skipped.");
                            break;
                        }

                        // A nested archive (e.g. "engine-server.<date>.log.zip") must be RECURSED into,
                        // not treated as a log — otherwise we'd hand the parser a raw .zip. Check this
                        // before the log-name match, since our patterns (…log*) also match "…log.zip".
                        if (IsArchive(innerName))
                        {
                            if (depth + 1 > MaxNestDepth)
                            {
                                onWarning?.Invoke($"Skipped deeply-nested archive '{innerName}' in '{archiveName}' (max depth {MaxNestDepth}).");
                                continue;
                            }
                            ExtractNestedArchive(reader, entry.Key, innerName, ctx);
                            continue;
                        }

                        if (!MatchesAnyPattern(innerName, namePatterns)) continue;
                        if (excludedNames != null && excludedNames.Contains(innerName)) continue;

                        // Preserve the entry's INNER relative path (not just the basename) so the grid can
                        // show distinct names for same-named logs in different folders, and so path-based
                        // detection (worker job id, workflow source file) keeps working.
                        string innerRelPath = InnerRelativePath(entry.Key, archivePath);
                        ExtractCurrentEntry(reader, entry.Key, innerRelPath, ctx);
                    }
                }
            }
            catch (Exception ex)
            {
                // Not a supported/valid archive (or truncated/partial) — skip quietly.
                onWarning?.Invoke($"Could not read archive '{archiveName}': {ex.Message}");
            }

            return results;
        }

        /// <summary>Mutable state threaded through per-entry extraction for a single archive.</summary>
        private sealed class ExtractContext
        {
            public string ArchivePath;
            public string ArchiveName;
            public string ExtractRoot;
            public IReadOnlyList<string> NamePatterns;
            public ISet<string> ExcludedNames;
            public Action<string> OnWarning;
            public List<ExtractedLog> Results;
            public string DestDir;        // created lazily on first match
            public long ExtractedTotal;
            public int Depth;             // nesting level (0 = the archive the user/dir load picked)
        }

        /// <summary>
        /// Handles an inner entry that is itself an archive: writes it to a temp file under the extract
        /// root, recurses <see cref="ExtractMatchingLogs"/> on it (depth+1), and merges the results.
        /// The nested archive's own logs thus come in exactly as if it had been a top-level archive; the
        /// temp copy stays under the repo's ExtractedArchives folder (swept on startup). Fail-soft.
        /// </summary>
        private static void ExtractNestedArchive(SharpCompress.Readers.IReader reader, string entryKey, string innerName, ExtractContext ctx)
        {
            // Stage the nested archive under a private temp subfolder (its inner logs are extracted from
            // there, so its own path never appears as a loaded "log"). Keep the archive's base name so
            // any warnings are legible.
            string stageDir = Path.Combine(ctx.ExtractRoot, "_nested", Guid.NewGuid().ToString("N").Substring(0, 8));
            string stagedArchive;
            try
            {
                Directory.CreateDirectory(stageDir);
                stagedArchive = Path.Combine(stageDir, SafeFolderName(Path.GetFileName(innerName)));
                using (var es = reader.OpenEntryStream())
                using (var fs = new FileStream(stagedArchive, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    es.CopyTo(fs);
                }
            }
            catch (Exception ex)
            {
                ctx.OnWarning?.Invoke($"Failed to stage nested archive '{innerName}' from '{ctx.ArchiveName}': {ex.Message}");
                return;
            }

            try { ctx.ExtractedTotal += new FileInfo(stagedArchive).Length; } catch { /* advisory */ }

            var nested = ExtractMatchingLogs(
                stagedArchive, ctx.ExtractRoot, ctx.NamePatterns, ctx.ExcludedNames, ctx.OnWarning, ctx.Depth + 1);

            foreach (var log in nested)
            {
                // Qualify the display name so the grid shows the containing rollup, e.g.
                // "support.zip!engine-server.2026-09-20.log.zip!engine-server.2026-09-20.log".
                ctx.Results.Add(new ExtractedLog
                {
                    ExtractedPath = log.ExtractedPath,
                    DisplayName = ctx.ArchiveName + "!" + innerName + "!" + StripArchivePrefix(log.DisplayName),
                });
            }
        }

        /// <summary>Drops the leading "&lt;archive&gt;!" segment a nested result carries, so the caller can
        /// re-qualify it with the OUTER archive context (avoids a doubled inner-archive name).</summary>
        private static string StripArchivePrefix(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return displayName;
            int bang = displayName.IndexOf('!');
            return bang >= 0 ? displayName.Substring(bang + 1) : displayName;
        }

        /// <summary>Writes the reader's current entry to the extraction dir, recording the result.</summary>
        private static void ExtractCurrentEntry(SharpCompress.Readers.IReader reader, string entryKey, string innerRelPath, ExtractContext ctx)
        {
            if (ctx.DestDir == null)
            {
                // Folder layout: <extractRoot>/<uniqueId>/<archiveName>/<inner relative path>. The
                // uniqueId parent keeps two same-named archives apart WITHOUT polluting the visible tail,
                // so the grid's shortest-unique-name shows a clean "<archiveName>/<inner rel path>".
                string unique = Guid.NewGuid().ToString("N").Substring(0, 8);
                ctx.DestDir = Path.Combine(ctx.ExtractRoot, unique, SafeFolderName(ctx.ArchiveName));
                Directory.CreateDirectory(ctx.DestDir);
            }

            // Preserve the inner RELATIVE PATH (folders + name) so same-named logs in different inner
            // folders stay distinct and worker-job/workflow path detection keeps working. Zip-slip guard
            // ensures the resolved path stays within DestDir; collisions still get a numeric suffix.
            string outPath = SafeOutputPath(ctx.DestDir, innerRelPath);
            if (outPath == null)
            {
                ctx.OnWarning?.Invoke($"Skipped unsafe entry path '{entryKey}' in '{ctx.ArchiveName}'.");
                return;
            }
            outPath = Deduplicate(outPath);

            try
            {
                // Create any inner subdirectories the relative path introduces.
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                using (var es = reader.OpenEntryStream())
                using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    es.CopyTo(fs);
                }
            }
            catch (Exception ex)
            {
                ctx.OnWarning?.Invoke($"Failed to extract '{innerRelPath}' from '{ctx.ArchiveName}': {ex.Message}");
                return;
            }

            try { ctx.ExtractedTotal += new FileInfo(outPath).Length; } catch { /* size is advisory */ }

            ctx.Results.Add(new ExtractedLog
            {
                ExtractedPath = outPath,
                DisplayName = ctx.ArchiveName + "!" + innerRelPath,
            });
        }

        /// <summary>
        /// The basename to match against the patterns. For a real entry key ("dir/sub/nuix.log") that's
        /// the last path segment. For single-stream formats (a bare ".gz"/".bz2"/".xz" that isn't a tar),
        /// SharpCompress may report a null/empty entry key, so we derive the inner name from the archive
        /// file name by stripping the trailing compression extension (e.g. "nuix.log.gz" -> "nuix.log").
        /// </summary>
        internal static string InnerFileName(string entryKey, string archivePath)
        {
            if (!string.IsNullOrEmpty(entryKey))
            {
                // Normalize separators and take the final segment.
                string norm = entryKey.Replace('\\', '/').TrimEnd('/');
                int slash = norm.LastIndexOf('/');
                string name = slash >= 0 ? norm.Substring(slash + 1) : norm;
                if (!string.IsNullOrEmpty(name)) return name;
            }

            // Fall back to the archive's own name minus one compression extension.
            string archiveName = Path.GetFileName(archivePath);
            foreach (var ext in new[] { ".gz", ".bz2", ".xz", ".lzma", ".z" })
            {
                if (archiveName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    return archiveName.Substring(0, archiveName.Length - ext.Length);
                }
            }
            return null;
        }

        /// <summary>
        /// The inner RELATIVE PATH (folders + file name) to reproduce under the extraction dir, so
        /// same-named logs in different archive folders stay distinct and path-based detection (worker
        /// job id, workflow source file) keeps working. Normalizes separators to '/', drops any leading
        /// slashes and drive/UNC roots, and strips '.'/'..' segments (defence-in-depth against zip-slip;
        /// <see cref="SafeOutputPath"/> is the hard guard). For single-stream formats with no entry key,
        /// falls back to the derived <see cref="InnerFileName"/> (a bare name, no folders).
        /// </summary>
        internal static string InnerRelativePath(string entryKey, string archivePath)
        {
            if (string.IsNullOrEmpty(entryKey))
            {
                return InnerFileName(entryKey, archivePath); // single-stream: bare name only
            }

            string norm = entryKey.Replace('\\', '/');
            // Drop a leading slash and any Windows drive / UNC prefix an archive might carry.
            norm = norm.TrimStart('/');
            int colon = norm.IndexOf(':');
            if (colon >= 0 && colon < norm.IndexOf('/')) { norm = norm.Substring(colon + 1).TrimStart('/'); }

            var kept = new List<string>();
            foreach (var seg in norm.Split('/'))
            {
                if (seg.Length == 0 || seg == ".") { continue; }
                if (seg == "..") { if (kept.Count > 0) { kept.RemoveAt(kept.Count - 1); } continue; }
                kept.Add(seg);
            }
            if (kept.Count == 0) { return InnerFileName(entryKey, archivePath); }
            return string.Join("/", kept);
        }

        /// <summary>Case-insensitive DOS-glob match of a basename against any pattern (EnumerateFiles semantics).</summary>
        internal static bool MatchesAnyPattern(string fileName, IReadOnlyList<string> patterns)
        {
            if (patterns == null) return false;
            foreach (var p in patterns)
            {
                if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                        p, fileName, ignoreCase: true))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolves the output path for an inner file inside <paramref name="destDir"/>, preserving the
        /// entry's RELATIVE PATH (<paramref name="innerRelPath"/> uses '/' separators) and rejecting
        /// anything that would escape the destination (zip-slip guard — the hard check, complementing the
        /// segment sanitizing in <see cref="InnerRelativePath"/>).
        /// </summary>
        internal static string SafeOutputPath(string destDir, string innerRelPath)
        {
            // Build the candidate from the sanitized relative segments (never an absolute/rooted path).
            var segs = (innerRelPath ?? "").Split('/', '\\').Where(s => s.Length > 0 && s != "." && s != "..");
            string rel = Path.Combine(segs.ToArray());
            if (string.IsNullOrEmpty(rel)) { return null; }

            string candidate = Path.Combine(destDir, rel);
            string fullDest = Path.GetFullPath(destDir + Path.DirectorySeparatorChar);
            string fullCandidate = Path.GetFullPath(candidate);
            if (!fullCandidate.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase))
            {
                return null; // escapes the extraction root
            }
            return fullCandidate;
        }

        /// <summary>Appends " (n)" before the extension until the path is unused, to avoid clobbering.</summary>
        private static string Deduplicate(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string next = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(next)) return next;
            }
        }

        /// <summary>Makes a filesystem-safe folder name from the archive file name (keeps it readable).</summary>
        private static string SafeFolderName(string archiveName)
        {
            return string.Join("_", archiveName.Split(Path.GetInvalidFileNameChars()));
        }
    }
}
