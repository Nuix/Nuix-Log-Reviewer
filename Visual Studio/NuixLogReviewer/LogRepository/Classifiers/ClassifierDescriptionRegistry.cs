using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NuixLogReviewer.LogRepository.Classifiers.Scripting;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>
    /// A process-wide, lazily-built map of classifier flag name =&gt; human-readable description, used to
    /// fill the "Description" column in the Classifiers tab. Contributions come from two sources:
    /// <list type="bullet">
    /// <item>Compiled classifiers that implement <see cref="IClassifierDescriptions"/>.</item>
    /// <item>Scripted classifiers that declare a <c>descriptions</c> map (via
    /// <see cref="ScriptClassifierDefinition.Descriptions"/>).</item>
    /// </list>
    /// Descriptions are metadata only - unrelated to whether a flag actually occurs in the loaded set -
    /// so the registry can be built once at startup independent of any load. Lookups for flags without a
    /// declared description return null (blank column). Tolerant of duplicates (first writer wins) and of
    /// classifiers that throw while reporting descriptions.
    /// </summary>
    public static class ClassifierDescriptionRegistry
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _map;

        /// <summary>Returns the description for a flag, or null if none is declared.</summary>
        public static string DescriptionFor(string flag)
        {
            if (string.IsNullOrEmpty(flag)) { return null; }
            var map = GetMap();
            return map.TryGetValue(flag, out var desc) ? desc : null;
        }

        /// <summary>The full flag-&gt;description map (built once, cached).</summary>
        public static IReadOnlyDictionary<string, string> GetMap()
        {
            var cached = _map;
            if (cached != null) { return cached; }

            lock (_lock)
            {
                if (_map != null) { return _map; }
                _map = Build();
                return _map;
            }
        }

        private static Dictionary<string, string> Build()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            AddCompiled(map);
            AddScripted(map);

            return map;
        }

        /// <summary>
        /// Instantiates each compiled classifier that has a public parameterless constructor and, if it
        /// implements <see cref="IClassifierDescriptions"/>, folds its declared descriptions into the map.
        /// (The scripted classifier is excluded - it has no parameterless ctor and carries its own
        /// descriptions via its definition, handled separately.)
        /// </summary>
        private static void AddCompiled(Dictionary<string, string> map)
        {
            var descType = typeof(IClassifierDescriptions);
            var classifierType = typeof(IEntryClassifier);

            IEnumerable<Type> types;
            try
            {
                types = Assembly.GetExecutingAssembly().GetExportedTypes()
                    .Where(t => !t.IsInterface && !t.IsAbstract
                                && classifierType.IsAssignableFrom(t)
                                && descType.IsAssignableFrom(t)
                                && t.GetConstructor(Type.EmptyTypes) != null);
            }
            catch
            {
                return;
            }

            foreach (var type in types)
            {
                try
                {
                    var instance = (IClassifierDescriptions)Activator.CreateInstance(type);
                    foreach (var d in instance.GetFlagDescriptions() ?? Enumerable.Empty<ClassifierFlagDescription>())
                    {
                        AddIfNew(map, d.Flag, d.Description);
                    }
                }
                catch
                {
                    // A classifier that throws while describing itself is skipped, not fatal.
                }
            }
        }

        /// <summary>Folds descriptions declared by scripted classifiers into the map.</summary>
        private static void AddScripted(Dictionary<string, string> map)
        {
            IReadOnlyList<ScriptClassifierDefinition> defs;
            try
            {
                defs = ScriptClassifierLoader.GetDefinitions();
            }
            catch
            {
                return;
            }

            foreach (var def in defs)
            {
                if (def.Descriptions == null) { continue; }
                foreach (var kv in def.Descriptions)
                {
                    AddIfNew(map, kv.Key, kv.Value);
                }
            }
        }

        private static void AddIfNew(Dictionary<string, string> map, string flag, string description)
        {
            if (string.IsNullOrWhiteSpace(flag) || string.IsNullOrWhiteSpace(description)) { return; }
            string norm = ScriptedClassifier.NormalizeFlag(flag);
            if (string.IsNullOrEmpty(norm)) { return; }
            if (!map.ContainsKey(norm)) { map[norm] = description.Trim(); }
        }
    }
}
