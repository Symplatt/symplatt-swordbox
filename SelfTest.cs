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
            if (store.Config.Boxes.Count == 0) store.Config.Boxes.Add(new Box());
            var box = store.Config.Boxes[0];
            string path = Path.Combine(desktops[0], "原始文件 — 完整名称.md");
            File.WriteAllText(path, "unchanged content");
            var original = File.GetAttributes(path);
            var created = File.GetCreationTimeUtc(path); var modified = File.GetLastWriteTimeUtc(path);
            store.Add(box, path);
            string stored = store.ItemPath(path);
            Check(!File.Exists(path) && File.ReadAllText(stored) == "unchanged content", "collection removes desktop icon and keeps content");
            Check(File.GetAttributes(stored) == original && (File.GetAttributes(Path.GetDirectoryName(stored)!) & Storage.Mask) == Storage.Mask,
                "only the managed folder has hidden and system bits");
            Check(File.GetCreationTimeUtc(stored) == created && File.GetLastWriteTimeUtc(stored) == modified, "creation and modification dates unchanged");
            store.Add(box, path); Check(box.Paths.Count(p => p == path) == 1, "duplicate collection rejected");
            var second = new Storage(root, desktops);
            Check(second.Journal.Count == 1 && second.Config.Boxes[0].Paths.Contains(path), "membership and recovery journal persisted");
            File.WriteAllText(stored, "# Overwrite");
            string replacement = stored + ".tmp";
            File.WriteAllText(replacement, "# Atomic save");
            File.Replace(replacement, stored, null);
            store.RefreshStoredPaths();
            string renamed = Path.Combine(Path.GetDirectoryName(stored)!, "重命名后的文件.md");
            File.Move(stored, renamed);
            store.RefreshStoredPaths();
            var target = new Box();
            store.Config.Boxes.Add(target);
            store.Transfer(box, target, new[] { renamed });
            Check(box.Paths.Count == 0 && target.Paths.Contains(path), "edited and renamed file transfers using its current path");
            string restored = Path.Combine(desktops[0], Path.GetFileName(renamed));
            File.WriteAllText(restored, "conflicting file");
            Check(store.CloseBox(target).Count == 0 && File.ReadAllText(restored) == "conflicting file" &&
                File.ReadAllText(target.Paths.Single()) == "# Atomic save", "close restores the saved file under a distinct name when necessary");
            store.ReopenBox(target);
            Check(File.ReadAllText(store.ItemPath(target.Paths.Single())) == "# Atomic save", "reopen collects the restored file");

            string legacy = Path.Combine(desktops[0], "旧版.md");
            File.WriteAllText(legacy, "# Legacy");
            store.Journal.Add(new RecoveryItem { Path = legacy, Identity = Native.Identity(legacy) });
            store.Write("recovery.json", store.Journal);
            File.SetAttributes(legacy, File.GetAttributes(legacy) | Storage.Mask);
            bool oldSaveFailed = false;
            try { File.WriteAllText(legacy, "# Save"); } catch (UnauthorizedAccessException) { oldSaveFailed = true; }
            Check(oldSaveFailed, "reproduced legacy overwrite failure");
            Check(store.RestoreAll(keepCollected: true).Count == 0, "legacy journal restored before migration");
            store.Add(box, legacy);
            File.WriteAllText(store.ItemPath(legacy), "# Migrated");
            Check(File.ReadAllText(store.ItemPath(legacy)) == "# Migrated", "legacy Markdown is writable after migration");

            string newFile = store.CreateItem(box, "新建 Markdown 文件.md", directory: false);
            string newFolder = store.CreateItem(box, "新建文件夹", directory: true);
            File.WriteAllText(newFile, "# New file");
            File.WriteAllText(Path.Combine(newFolder, "子文件.txt"), "directory content");
            store.RefreshStoredPaths();
            Check(store.RestoreAll().Count == 0 && File.ReadAllText(Path.Combine(desktops[0], Path.GetFileName(newFile))) == "# New file" &&
                File.Exists(Path.Combine(desktops[0], Path.GetFileName(newFolder), "子文件.txt")), "new files and folders restore with their contents");

            string outside = Path.Combine(root, "outside.txt"); File.WriteAllText(outside, "outside");
            bool rejected = false; try { store.Add(box, outside); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllText(outside) == "outside", "outside-desktop file not moved or collected");
            lines.Add("ALL PASSED");
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); Environment.ExitCode = 1; }
        File.WriteAllLines(Path.Combine(root, "test-results.txt"), lines);
    }
}
