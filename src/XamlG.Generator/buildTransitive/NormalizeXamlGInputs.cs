using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace XamlG.Build
{
    /// <summary>Metadata-only item normalization. Never reads or writes application source.</summary>
    public sealed class NormalizeXamlGInputs : Task
    {
        public ITaskItem[] AdditionalFiles { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] FrameworkSources { get; set; } = Array.Empty<ITaskItem>();
        [Required] public string ProjectDirectory { get; set; }
        public bool UseFrameworkDefaults { get; set; }
        public bool EnableDefaults { get; set; } = true;
        [Output] public ITaskItem[] NormalizedFiles { get; private set; }
        [Output] public string Fingerprint { get; private set; }

        public override bool Execute()
        {
            var paths = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var groups = new Dictionary<string, List<ITaskItem>>(paths);
            var unrelated = new List<ITaskItem>();
            try
            {
                var root = Path.GetFullPath(ProjectDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Func<ITaskItem, string> fullPath = item => Path.GetFullPath(Path.IsPathRooted(item.ItemSpec) ? item.ItemSpec : Path.Combine(root, item.ItemSpec));
                var framework = new HashSet<string>(FrameworkSources.Where(IsXaml).Select(fullPath), paths);
                Action<ITaskItem> add = item =>
                {
                    var path = fullPath(item);
                    List<ITaskItem> group;
                    if (!groups.TryGetValue(path, out group)) groups.Add(path, group = new List<ITaskItem>());
                    group.Add(item);
                };
                foreach (var item in AdditionalFiles)
                    if (IsXaml(item)) add(item); else unrelated.Add(new TaskItem(item));
                foreach (var item in FrameworkSources.Where(IsXaml)) add(item);
                foreach (var item in Sources)
                {
                    if (!IsXaml(item)) { Error("XamlGSource must identify a .xaml, .axaml or .paml file: " + item.ItemSpec); continue; }
                    var isDefault = string.Equals(item.GetMetadata("XamlGDefaultItem"), "true", StringComparison.OrdinalIgnoreCase);
                    if (isDefault && (!EnableDefaults || UseFrameworkDefaults && !framework.Contains(fullPath(item)))) continue;
                    add(item);
                }
                var logicalOwners = new Dictionary<string, string>(StringComparer.Ordinal);
                var results = new List<ITaskItem>(unrelated);
                foreach (var pair in groups.OrderBy(p => p.Key, paths))
                {
                    string logical = null;
                    bool? compile = null;
                    var result = new TaskItem(pair.Key);
                    foreach (var item in pair.Value)
                    {
                        // Preserve custom metadata for other generators. Conflicting unrelated
                        // metadata is diagnosed rather than silently becoming last-writer-wins.
                        foreach (DictionaryEntry metadata in item.CloneCustomMetadata())
                        {
                            var key = (string)metadata.Key;
                            var value = (string)metadata.Value;
                            if (key == "XamlGDefaultItem" || key == "XamlGLogicalPath" || key == "XamlGCompile" || key == "Link" || key == "SourceItemGroup") continue;
                            var previous = result.GetMetadata(key);
                            if (previous.Length != 0 && value.Length != 0 && previous != value)
                                Error("Conflicting metadata '" + key + "' for " + pair.Key);
                            else if (value.Length != 0) result.SetMetadata(key, value);
                        }
                        var path = item.GetMetadata("XamlGLogicalPath");
                        if (string.IsNullOrWhiteSpace(path)) path = item.GetMetadata("Link");
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            path = NormalizeLogical(path);
                            if (logical != null && logical != path) Error("Conflicting logical paths for " + pair.Key);
                            logical = path;
                        }
                        var configured = item.GetMetadata("XamlGCompile");
                        if (!string.IsNullOrWhiteSpace(configured))
                        {
                            bool value;
                            if (!bool.TryParse(configured, out value)) Error("XamlGCompile must be true or false for " + pair.Key);
                            else if (compile.HasValue && compile.Value != value) Error("Conflicting XamlGCompile values for " + pair.Key);
                            else compile = value;
                        }
                    }
                    if (logical == null)
                    {
                        var prefix = root + Path.DirectorySeparatorChar;
                        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        if (!pair.Key.StartsWith(prefix, comparison))
                        { Error("External XAML requires Link or XamlGLogicalPath: " + pair.Key); continue; }
                        logical = NormalizeLogical(pair.Key.Substring(prefix.Length));
                    }
                    string owner;
                    if ((compile ?? true) && logicalOwners.TryGetValue(logical, out owner) && !paths.Equals(owner, pair.Key))
                        Error("Duplicate logical XAML path '" + logical + "' for " + owner + " and " + pair.Key);
                    else if (compile ?? true) logicalOwners[logical] = pair.Key;
                    result.SetMetadata("XamlGCompile", (compile ?? true) ? "true" : "false");
                    result.SetMetadata("XamlGLogicalPath", logical);
                    result.SetMetadata("Link", logical);
                    results.Add(result);
                }
                NormalizedFiles = results.ToArray();
                var text = new StringBuilder();
                foreach (var item in results.OrderBy(i => i.ItemSpec, paths))
                    text.Append(item.ItemSpec.Length).Append(':').Append(item.ItemSpec).Append('|')
                        .Append(item.GetMetadata("XamlGLogicalPath")).Append('|').Append(item.GetMetadata("XamlGCompile")).Append('\n');
                using (var hash = SHA256.Create()) Fingerprint = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
                return !Log.HasLoggedErrors;
            }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is NotSupportedException)
            { Error(error.Message); return false; }
        }
        private static bool IsXaml(ITaskItem item)
        {
            var extension = Path.GetExtension(item.ItemSpec);
            return extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".paml", StringComparison.OrdinalIgnoreCase);
        }
        private static string NormalizeLogical(string value)
        {
            value = value.Replace('\\', '/');
            while (value.StartsWith("./", StringComparison.Ordinal)) value = value.Substring(2);
            if (value.StartsWith("/", StringComparison.Ordinal) || value.Contains(":") || value.Any(char.IsControl) || value.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
                throw new ArgumentException("Logical XAML paths must be normalized project-relative paths: " + value);
            return value;
        }
        private void Error(string message) => Log.LogError(null, "XG2001", null, null, 0, 0, 0, 0, message);
    }
}
