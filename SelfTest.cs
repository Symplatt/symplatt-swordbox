using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SwordBox;

internal static class SelfTest
{
    public static void Run(string root, string[] desktops)
    {
        if (!root.Contains("sandbox", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test requires --sandbox.");
        var lines = new List<string>();
        void Check(bool ok, string description) { if (!ok) throw new Exception(description); lines.Add("PASS " + description); }
        try
        {
            var store = new Storage(root, desktops);
            var box = new Box();
            var other = new Box();
            store.Config.Boxes.AddRange(new[] { box, other });
            string path = Path.Combine(desktops[0], "Notes.md");
            File.WriteAllText(path, "# Original");
            var attributes = File.GetAttributes(path);
            store.Add(box, path);
            Check(store.ItemPath(path) == path && File.GetAttributes(path) == attributes &&
                !Directory.EnumerateDirectories(desktops[0]).Any(), "collection preserves path and attributes without creating a directory");
            File.WriteAllText(path, "# Overwrite");
            string replacement = path + ".tmp";
            File.WriteAllText(replacement, "# Atomic save");
            File.Replace(replacement, path, null);
            store.Transfer(box, other, new[] { path });
            Check(box.Paths.Count == 0 && other.Paths.Contains(path), "transfer changes membership only");
            Check(store.CloseBox(other).Count == 0 && File.ReadAllText(path) == "# Atomic save", "closing preserves desktop file");
            store.ReopenBox(other);
            Check(other.Paths.Contains(path), "reopening retains membership");
            store.Remove(other, path);
            Check(other.Paths.Count == 0 && File.Exists(path), "removing membership preserves desktop file");

            string managed = Path.Combine(desktops[0], "legacy-managed");
            Directory.CreateDirectory(managed);
            string original = Path.Combine(desktops[0], "Migrated.md");
            string stored = Path.Combine(managed, "Migrated.md");
            File.WriteAllText(stored, "# Migrated");
            box.Paths.Add(original);
            store.Save();
            store.Journal.Add(new RecoveryItem { Path = original, StoredPath = stored, Identity = Native.Identity(stored) });
            store.Write("recovery.json", store.Journal);
            Check(store.RestoreAll().Count == 0 && File.ReadAllText(original) == "# Migrated" &&
                !File.Exists(stored) && box.Paths.Contains(original), "old managed file returns to its original path with membership");

            string created = store.CreateItem(box, "New.md", directory: false);
            string folder = store.CreateItem(box, "New folder", directory: true);
            Check(created == Path.Combine(desktops[0], "New.md") && File.Exists(created) &&
                folder == Path.Combine(desktops[0], "New folder") && Directory.Exists(folder), "new items are created at normal desktop paths");
            string renamed = store.RenameItem(created, "Renamed.md");
            Check(!File.Exists(created) && File.Exists(renamed) && box.Paths.Contains(renamed),
                "renaming updates the file and collection membership");
            lines.Add("ALL PASSED");
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); Environment.ExitCode = 1; }
        File.WriteAllLines(Path.Combine(root, "test-results.txt"), lines);
    }
}
