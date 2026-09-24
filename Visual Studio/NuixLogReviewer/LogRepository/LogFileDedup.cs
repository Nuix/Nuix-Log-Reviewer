using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// De-duplicates log files by CONTENT so the same log loaded via two paths isn't ingested twice.
    /// The motivating case: a customer directory that contains both <c>logs.zip</c> and an already-
    /// extracted <c>logs/</c> copy of the same files — archive inspection would recover the zip's inner
    /// logs while the loose extracted copies are also found, doubling every entry.
    ///
    /// Identity is byte-content, independent of name/path: two files are "the same log" when they have
    /// the same length and the same content key. To stay fast on large logs the key is NOT a full-file
    /// hash — it's the file length plus a SHA-256 over the first and last <see cref="SampleBytes"/> of
    /// the file. For real log files (which differ early when different) this is effectively as accurate
    /// as a full hash at a fraction of the I/O.
    /// </summary>
    public static class LogFileDedup
    {
        /// <summary>Bytes sampled from the head and (separately) the tail of each file for the key.</summary>
        internal const int SampleBytes = 64 * 1024;

        /// <summary>
        /// Combines <paramref name="preferred"/> and <paramref name="secondary"/> file paths, dropping any
        /// SECONDARY file whose content matches a file already kept (a preferred file, or an earlier
        /// secondary). Preferred files are always all kept and are never dropped against each other, so
        /// this only removes redundant archive-extracted copies — existing loose-only load behavior is
        /// unchanged. Returns the kept paths (preferred first, then surviving secondary), and reports the
        /// number of secondary files skipped via <paramref name="onSkipped"/>.
        ///
        /// Dedup is EXACT: the fast length+head/tail key only nominates candidates; a secondary is
        /// dropped only after a full-content hash confirms it truly matches a kept file. That keeps the
        /// common case fast (full hash runs only on a quick-key collision) while never silently dropping
        /// a genuinely different log that merely shares length and head/tail bytes.
        /// </summary>
        public static string[] Combine(
            IReadOnlyList<string> preferred,
            IReadOnlyList<string> secondary,
            Action<int> onSkipped = null)
        {
            var kept = new List<string>((preferred?.Count ?? 0) + (secondary?.Count ?? 0));
            // Quick key -> list of kept paths sharing that key (usually one). Full hashes are computed
            // lazily and cached per path only when a collision must be resolved.
            var byQuickKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var fullHashCache = new Dictionary<string, string>(StringComparer.Ordinal);

            void Record(string path)
            {
                kept.Add(path);
                string qk = TryContentKey(path);
                if (qk == null) return; // unhashable: kept, but can't be matched against
                if (!byQuickKey.TryGetValue(qk, out var list)) { list = new List<string>(1); byQuickKey[qk] = list; }
                list.Add(path);
            }

            // Preferred (loose) files: keep all; index them so secondaries can dedup against them.
            if (preferred != null)
            {
                foreach (var path in preferred) Record(path);
            }

            // Secondary (archive-extracted) files: keep unless a kept file with the SAME quick key also
            // has an identical full-content hash.
            int skipped = 0;
            if (secondary != null)
            {
                foreach (var path in secondary)
                {
                    string qk = TryContentKey(path);
                    if (qk != null && byQuickKey.TryGetValue(qk, out var candidates))
                    {
                        if (IsTrueDuplicate(path, candidates, fullHashCache))
                        {
                            skipped++;
                            continue;
                        }
                    }
                    Record(path);
                }
            }

            if (skipped > 0) onSkipped?.Invoke(skipped);
            return kept.ToArray();
        }

        /// <summary>
        /// True if <paramref name="path"/> has the same full-content hash as any of <paramref name="candidates"/>
        /// (files that already share its quick key). A candidate or the path being unhashable => not a
        /// confirmed duplicate (fail-open: keep the file).
        /// </summary>
        private static bool IsTrueDuplicate(string path, List<string> candidates, Dictionary<string, string> fullHashCache)
        {
            string mine = TryFullHash(path, fullHashCache);
            if (mine == null) return false;
            foreach (var c in candidates)
            {
                string theirs = TryFullHash(c, fullHashCache);
                if (theirs != null && string.Equals(theirs, mine, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static string TryFullHash(string path, Dictionary<string, string> cache)
        {
            if (cache.TryGetValue(path, out var h)) return h;
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                {
                    h = Convert.ToHexString(sha.ComputeHash(fs));
                }
            }
            catch { h = null; }
            cache[path] = h;
            return h;
        }

        /// <summary>Content key or null if the file can't be read (caller treats null as "keep").</summary>
        internal static string TryContentKey(string path)
        {
            try { return ContentKey(path); }
            catch { return null; }
        }

        /// <summary>
        /// A content identity key: "len:&lt;length&gt;:&lt;sha256 of head+tail samples&gt;". Files shorter
        /// than <c>2 * SampleBytes</c> are hashed whole (head and tail overlap), which is exact for them.
        /// </summary>
        internal static string ContentKey(string path)
        {
            var fi = new FileInfo(path);
            long len = fi.Length;

            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            {
                if (len <= 2L * SampleBytes)
                {
                    // Small file: hash the whole thing (head and tail would overlap anyway).
                    var all = ReadUpTo(fs, (int)Math.Max(0, len));
                    sha.TransformFinalBlock(all, 0, all.Length);
                }
                else
                {
                    // Head sample.
                    var head = ReadUpTo(fs, SampleBytes);
                    sha.TransformBlock(head, 0, head.Length, null, 0);

                    // Tail sample.
                    fs.Seek(-SampleBytes, SeekOrigin.End);
                    var tail = ReadUpTo(fs, SampleBytes);
                    sha.TransformFinalBlock(tail, 0, tail.Length);
                }

                string hash = Convert.ToHexString(sha.Hash);
                return "len:" + len + ":" + hash;
            }
        }

        /// <summary>Reads up to <paramref name="count"/> bytes, tolerating short reads.</summary>
        private static byte[] ReadUpTo(Stream s, int count)
        {
            if (count <= 0) return Array.Empty<byte>();
            var buf = new byte[count];
            int total = 0, n;
            while (total < count && (n = s.Read(buf, total, count - total)) > 0) total += n;
            if (total == count) return buf;
            var trimmed = new byte[total];
            Array.Copy(buf, trimmed, total);
            return trimmed;
        }
    }
}
