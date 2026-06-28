using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace PrecisionStockpileControl.Tests
{
    internal static class RepoPaths
    {
        private static readonly Lazy<string> LazyRoot = new Lazy<string>(FindRoot);

        public static string Root => LazyRoot.Value;

        public static string PathTo(params string[] parts)
        {
            string path = Root;
            for (int i = 0; i < parts.Length; i++) path = Path.Combine(path, parts[i]);
            return path;
        }

        public static string Read(params string[] parts)
            => File.ReadAllText(PathTo(parts)).Replace("\r\n", "\n");

        public static string[] ScribeNodes(params string[] parts)
        {
            string src = Read(parts);
            var matches = Regex.Matches(src,
                @"Scribe_(?:Values|Deep|Collections)\.Look\s*\([^;]*?,\s*""([^""]+)""",
                RegexOptions.Singleline);
            var values = new string[matches.Count];
            for (int i = 0; i < matches.Count; i++) values[i] = matches[i].Groups[1].Value;
            return values;
        }

        public static void AssertInOrder(string source, params string[] tokens)
        {
            int pos = -1;
            foreach (string token in tokens)
            {
                int next = source.IndexOf(token, pos + 1, StringComparison.Ordinal);
                Assert.That(next, Is.GreaterThan(pos), "Expected token in order: " + token);
                pos = next;
            }
        }

        private static string FindRoot()
        {
            string dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "AGENTS.md"))
                    && Directory.Exists(Path.Combine(dir, "Source"))
                    && Directory.Exists(Path.Combine(dir, "docs")))
                    return dir;

                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate PSC repo root from test directory.");
        }
    }
}
