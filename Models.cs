using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Drawing;
using System.Text.RegularExpressions;

namespace SwordBox;

public sealed class Box
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "桌面收纳";
    public double X { get; set; } = 80;
    public double Y { get; set; } = 80;
    public double Width { get; set; } = 970;
    public double Height { get; set; } = 390;
    public List<string> Paths { get; set; } = new();
    public string Sort { get; set; } = "Name";
    public bool Descending { get; set; }
    public bool GroupByType { get; set; } = true;
    public bool ShowModified { get; set; } = true;
    public bool ShowCreated { get; set; } = true;
    public bool ShowType { get; set; } = true;
}

public sealed class Configuration
{
    public const int MinimumOpacity = 40;
    public int Schema { get; set; } = 1;
    public int BoxOpacity { get; set; } = 80;
    public List<Box> Boxes { get; set; } = new();
    public List<Box> ClosedBoxes { get; set; } = new();
    public BoxLayout? NewBoxLayout { get; set; }
}

public sealed record BoxLayout(double Width, double Height, string Sort, bool Descending,
    bool ShowModified, bool ShowCreated, bool ShowType, bool GroupByType = true)
{
    public static BoxLayout FromBox(Box box) => new(box.Width, box.Height, box.Sort, box.Descending,
        box.ShowModified, box.ShowCreated, box.ShowType, box.GroupByType);

    public Box CreateBox() => new()
    {
        Width = Width, Height = Height, Sort = Sort, Descending = Descending,
        ShowModified = ShowModified, ShowCreated = ShowCreated, ShowType = ShowType, GroupByType = GroupByType
    };
}

public sealed class RecoveryItem
{
    public string Path { get; set; } = "";
    public int OriginalBits { get; set; }
    public string Identity { get; set; } = "";
    public string? StoredPath { get; set; }
    public string? ReturnPath { get; set; }
}

public sealed class DesktopFile
{
    public string Path { get; }
    public string Name { get; }
    public bool IsFolder { get; }
    public DateTime Modified { get; }
    public DateTime Created { get; }
    public string Type { get; }
    public Image? Icon { get; }
    public DesktopFile(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        IsFolder = Directory.Exists(path);
        FileSystemInfo info = IsFolder ? new DirectoryInfo(path) : new FileInfo(path);
        Modified = info.LastWriteTime;
        Created = info.CreationTime;
        (Type, Icon) = Native.FilePresentation(path, IsFolder);
    }
}

public sealed class Storage
{
    public const FileAttributes Mask = FileAttributes.Hidden | FileAttributes.System;
    public string Root { get; }
    public string[] Desktops { get; }
    public Configuration Config { get; }
    public List<RecoveryItem> Journal { get; private set; }
    public event Action<string>? Renamed;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Storage(string root, string[] desktops)
    {
        Root = root;
        Desktops = desktops;
        Directory.CreateDirectory(root);
        Config = Read<Configuration>("settings.json") ?? new Configuration();
        Journal = Read<List<RecoveryItem>>("recovery.json") ?? new();
    }

    private T? Read<T>(string name)
    {
        string path = System.IO.Path.Combine(Root, name);
        if (!File.Exists(path)) return default;
        // Never replace an unreadable recovery journal with an empty one.
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
    }

    public void Write<T>(string name, T value)
    {
        string path = System.IO.Path.Combine(Root, name);
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(true);
        }
        File.Move(tmp, path, true);
    }

    public void Save() => Write("settings.json", Config);
    public Box CreateBox() => Config.NewBoxLayout?.CreateBox() ??
        (Config.Boxes.LastOrDefault() is Box previous ? BoxLayout.FromBox(previous).CreateBox() : new Box());

    public void SaveEditedBox(Box box)
    {
        var previous = Config.NewBoxLayout;
        Config.NewBoxLayout = BoxLayout.FromBox(box);
        try { Save(); }
        catch { Config.NewBoxLayout = previous; throw; }
    }
    private void SaveJournal() => Write("recovery.json", Journal);
    private RecoveryItem? FindEntry(string path) =>
        Journal.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
    public string ItemPath(string path) => FindEntry(path)?.StoredPath ?? path;
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public bool IsDesktop(string path) => Desktops.Any(d => string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)), d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    public bool Collected(string path) => Config.Boxes.Any(b => b.Paths.Contains(path, StringComparer.OrdinalIgnoreCase));
    public bool Tracks(string path) => Collected(path) ||
        Config.ClosedBoxes.Any(b => b.Paths.Contains(path, StringComparer.OrdinalIgnoreCase));

    public List<string> CloseBox(Box box)
    {
        MoveBox(box, Config.Boxes, Config.ClosedBoxes);
        var errors = new List<string>();
        foreach (string path in box.Paths.ToArray())
        {
            try { Restore(path); }
            catch (Exception ex) { errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
        }
        return errors;
    }

    public void ReopenBox(Box box)
    {
        if (!Config.ClosedBoxes.Contains(box)) return;
        var conflict = box.Paths.FirstOrDefault(Collected);
        if (conflict != null)
            throw new IOException(Path.GetFileName(conflict) + " 已在另一个收纳盒中，请先从那里移出。");

        MoveBox(box, Config.ClosedBoxes, Config.Boxes);
    }

    private void MoveBox(Box box, List<Box> source, List<Box> target)
    {
        int index = source.IndexOf(box);
        if (index < 0) throw new InvalidOperationException("未找到收纳盒。");
        source.RemoveAt(index);
        target.Add(box);
        try { Save(); }
        catch
        {
            target.Remove(box);
            source.Insert(index, box);
            throw;
        }
    }

    public IEnumerable<string> Available()
    {
        foreach (string desktop in Desktops.Where(Directory.Exists))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(desktop))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); } catch { continue; }
                if ((attributes & Mask) != 0 || Collected(path)) continue;
                yield return path;
            }
        }
    }

    private static void MoveItem(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private void ForgetEntry(RecoveryItem item)
    {
        Journal.Remove(item);
        try { SaveJournal(); }
        catch { Journal.Add(item); throw; }
    }

    private static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') ||
            Regex.IsMatch(name.Split('.', 2)[0].TrimEnd(), @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new IOException("请输入有效的文件或文件夹名称。");
        return name;
    }

    public string RenameItem(string path, string name)
    {
        string target = Path.Combine(Path.GetDirectoryName(path)!, ValidateName(name));
        if (target == path) return path;
        if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase) && (Exists(target) || Tracks(target)))
            throw new IOException("已有同名项目。");
        MoveItem(path, target);
        TrackRename(path, target);
        return target;
    }

    public string CreateItem(Box box, string name, bool directory)
    {
        string path = Path.Combine(Desktops[0], ValidateName(name));
        if (Tracks(path) || FindEntry(path) != null || Exists(path))
            throw new IOException("桌面或收纳盒中已有同名项目。");
        if (directory) Directory.CreateDirectory(path);
        else using (File.Open(path, FileMode.CreateNew, FileAccess.Write)) { }
        AddMembership(box, path);
        return path;
    }

    public void Add(Box box, string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!IsDesktop(path)) throw new IOException("只能收纳直接位于桌面的文件或文件夹。请先将文件放到桌面。");
        if (Collected(path)) return;
        if ((File.GetAttributes(path) & Mask) != 0)
            throw new IOException("该项目原本已隐藏或标记为系统项目，未收纳。");
        AddMembership(box, path);
    }

    private void AddMembership(Box box, string path)
    {
        box.Paths.Add(path);
        try { SaveEditedBox(box); }
        catch { box.Paths.Remove(path); throw; }
    }

    public void Remove(Box box, string path)
    {
        path = box.Paths.FirstOrDefault(item => string.Equals(ItemPath(item), path, StringComparison.OrdinalIgnoreCase)) ?? path;
        int index = box.Paths.IndexOf(path);
        if (index < 0) return;
        path = Restore(path);
        index = box.Paths.IndexOf(path);
        if (index < 0) return;
        box.Paths.RemoveAt(index);
        try { SaveEditedBox(box); }
        catch
        {
            box.Paths.Insert(index, path);
            throw;
        }
    }

    public void Transfer(Box source, Box target, IEnumerable<string> paths)
    {
        var selected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = source.Paths.Where(path => selected.Contains(ItemPath(path))).ToArray();
        if (moved.Length == 0) return;
        var original = source.Paths.ToArray();
        int targetCount = target.Paths.Count;
        source.Paths.RemoveAll(moved.ToHashSet().Contains);
        target.Paths.AddRange(moved);
        try { SaveEditedBox(target); }
        catch
        {
            source.Paths.Clear();
            source.Paths.AddRange(original);
            target.Paths.RemoveRange(targetCount, moved.Length);
            throw;
        }
    }

    public void TrackRename(string oldPath, string newPath)
    {
        var entry = FindEntry(oldPath);
        if (entry?.StoredPath != null) return; // This desktop path is reserved for recovery.
        if (entry != null) { entry.Path = newPath; SaveJournal(); }
        UpdateMembershipPaths(oldPath, newPath);
        Renamed?.Invoke(newPath);
    }

    private void UpdateMembershipPaths(string oldPath, string newPath)
    {
        if (oldPath == newPath) return;
        var changes = new List<(Box Box, int Index, string Path)>();
        foreach (var box in Config.Boxes.Concat(Config.ClosedBoxes))
            for (int i = 0; i < box.Paths.Count; i++)
                if (string.Equals(box.Paths[i], oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add((box, i, box.Paths[i]));
                    box.Paths[i] = newPath;
                }
        if (changes.Count == 0) return;
        try { Save(); }
        catch
        {
            foreach (var change in changes) change.Box.Paths[change.Index] = change.Path;
            throw;
        }
    }

    private string? Resolve(RecoveryItem item)
    {
        if ((File.Exists(item.Path) || Directory.Exists(item.Path)) &&
            (item.Identity.Length == 0 || Native.Identity(item.Path) == item.Identity)) return item.Path;
        if (item.Identity.Length == 0) return null;
        foreach (string desktop in Desktops.Where(Directory.Exists))
            foreach (string path in Directory.EnumerateFileSystemEntries(desktop))
                if (Native.Identity(path) == item.Identity) return path;
        return null;
    }

    public string Restore(string path)
    {
        var item = FindEntry(path);
        if (item == null) return path;
        if (item.StoredPath != null)
        {
            string? actual = ResolveStored(item);
            string returned = item.ReturnPath ?? path;
            if (actual != null)
            {
                returned = ReturnDestination(path, actual);
                item.ReturnPath = returned;
                SaveJournal();
                MoveItem(actual, returned);
            }
            else if (!Exists(returned))
            {
                // Keep the record if the user moved the item outside the managed folder.
                return path;
            }
            UpdateMembershipPaths(path, returned);
            ForgetEntry(item);
            return returned;
        }
        string? resolved = Resolve(item);
        if (resolved != null)
        {
            var attributes = File.GetAttributes(resolved);
            File.SetAttributes(resolved, (attributes & ~Mask) | (FileAttributes)item.OriginalBits);
            Native.NotifyDesktopVisibility(resolved, (attributes & FileAttributes.Directory) != 0, visible: item.OriginalBits == 0);
            if (!string.Equals(resolved, path, StringComparison.OrdinalIgnoreCase)) TrackRename(path, resolved);
        }
        else
        {
            // Retain the recovery record if a file was moved outside the watched desktop.
            return path;
        }
        ForgetEntry(item);
        return resolved;
    }

    private static string? ResolveStored(RecoveryItem item)
    {
        string stored = item.StoredPath!;
        if (Exists(stored)) return stored; // Editors may replace a file when saving it.
        string folder = Path.GetDirectoryName(stored)!;
        if (item.Identity.Length == 0 || !Directory.Exists(folder)) return null;
        return Directory.EnumerateFileSystemEntries(folder).FirstOrDefault(path => Native.Identity(path) == item.Identity);
    }

    private string ReturnDestination(string original, string actual)
    {
        string parent = Path.GetDirectoryName(original)!;
        string name = Path.GetFileName(actual);
        string path = Path.Combine(parent, name);
        string extension = Directory.Exists(actual) ? "" : Path.GetExtension(name);
        string stem = name[..(name.Length - extension.Length)];
        for (int suffix = 2; Exists(path) || (Tracks(path) && !string.Equals(path, original, StringComparison.OrdinalIgnoreCase)); suffix++)
            path = Path.Combine(parent, $"{stem} ({suffix}){extension}");
        return path;
    }

    public List<string> RestoreAll()
    {
        var errors = new List<string>();
        foreach (var item in Journal.ToArray())
        {
            try { Restore(item.Path); }
            catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(item.Path) + "：" + ex.Message); }
        }
        return errors;
    }
}
