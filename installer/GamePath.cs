using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ShanZhuWaGua
{
    /// <summary>
    /// 找游戏目录：先看上次记住的，再查 Steam 注册表 + 库配置，最后按盘符扫常见位置。
    /// </summary>
    internal static class GamePath
    {
        private const string FolderName = "Meow My Crop!";

        public static bool IsGame(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                return File.Exists(Path.Combine(dir, "MeowMyCrop_Data", "Managed", "Assembly-CSharp.dll"));
            }
            catch
            {
                return false;
            }
        }

        private static string StoreFile
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShanZhuWaGua", "gamepath.txt");
            }
        }

        public static string LoadSaved()
        {
            try
            {
                if (!File.Exists(StoreFile)) return null;
                string saved = File.ReadAllText(StoreFile).Trim();
                return IsGame(saved) ? saved : null;
            }
            catch
            {
                return null;
            }
        }

        public static void Remember(string dir)
        {
            try
            {
                if (!IsGame(dir)) return;
                string folder = Path.GetDirectoryName(StoreFile);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(StoreFile, dir);
            }
            catch
            {
                // 记不住就算了，不影响本次注入
            }
        }

        /// <summary>把路径还原成磁盘上的真实大小写，只影响显示。</summary>
        public static string RealCase(string dir)
        {
            try
            {
                string full = Path.GetFullPath(dir);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return dir;

                string rest = full.Substring(root.Length);
                string[] parts = rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                string cur = root;

                foreach (string part in parts)
                {
                    string next = Path.Combine(cur, part);
                    string match = null;
                    try
                    {
                        foreach (string d in Directory.GetDirectories(cur))
                        {
                            if (string.Equals(Path.GetFileName(d), part, StringComparison.OrdinalIgnoreCase))
                            {
                                match = d;
                                break;
                            }
                        }
                    }
                    catch { }
                    cur = match ?? next;
                }
                return cur.Length >= 2 && cur[1] == ':'
                    ? char.ToUpperInvariant(cur[0]) + cur.Substring(1)
                    : cur;
            }
            catch
            {
                return dir;
            }
        }

        public static string Find()
        {
            foreach (string dir in Candidates())
                if (IsGame(dir)) return dir;
            return null;
        }

        private static IEnumerable<string> Candidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string saved = LoadSaved();
            if (saved != null && seen.Add(saved)) yield return saved;

            foreach (string root in SteamRoots())
                foreach (string dir in UnderCommon(Path.Combine(root, "steamapps", "common")))
                    if (seen.Add(dir)) yield return dir;

            foreach (string dir in DriveScan())
                if (seen.Add(dir)) yield return dir;
        }

        private static IEnumerable<string> UnderCommon(string common)
        {
            bool exists;
            try { exists = Directory.Exists(common); }
            catch { exists = false; }
            if (!exists) yield break;

            yield return Path.Combine(common, FolderName);

            string[] subs;
            try { subs = Directory.GetDirectories(common); }
            catch { yield break; }

            foreach (string sub in subs)
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith("meow", StringComparison.OrdinalIgnoreCase)) yield return sub;
            }
        }

        private static readonly string[] DriveSubPaths =
        {
            @"steamapps\common",
            @"Steam\steamapps\common",
            @"SteamLibrary\steamapps\common",
            @"Games\Steam\steamapps\common",
            @"Program Files (x86)\Steam\steamapps\common",
            @"Program Files\Steam\steamapps\common",
        };

        private static IEnumerable<string> DriveScan()
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { yield break; }

            foreach (DriveInfo drive in drives)
            {
                string root;
                try
                {
                    if (!drive.IsReady) continue;
                    if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                    root = drive.RootDirectory.FullName;
                }
                catch { continue; }

                foreach (string rel in DriveSubPaths)
                    foreach (string dir in UnderCommon(Path.Combine(root, rel)))
                        yield return dir;
            }
        }

        private static List<string> SteamRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new List<string>();

            Action<string> add = path =>
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    path = path.Replace('/', '\\').TrimEnd('\\');
                }
                catch { return; }
                if (path.Length == 0) return;
                if (seen.Add(path)) roots.Add(path);
            };

            add(ReadReg(@"Software\Valve\Steam", "SteamPath"));
            add(ReadReg(@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
            add(ReadReg(@"SOFTWARE\Valve\Steam", "InstallPath"));

            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(pf86)) add(Path.Combine(pf86, "Steam"));
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(pf)) add(Path.Combine(pf, "Steam"));

            // 每个 Steam 根目录的库配置里列出了其它盘上的库
            foreach (string root in roots.ToArray())
            {
                string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                try
                {
                    if (!File.Exists(vdf)) continue;
                    string text = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                        add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch
                {
                    // 单个库配置读不了就跳过
                }
            }

            return roots;
        }

        private static string ReadReg(string subKey, string value)
        {
            RegistryView[] views = { RegistryView.Registry64, RegistryView.Registry32 };
            RegistryHive[] hives = { RegistryHive.CurrentUser, RegistryHive.LocalMachine };

            foreach (RegistryHive hive in hives)
            {
                foreach (RegistryView view in views)
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey key = baseKey.OpenSubKey(subKey))
                        {
                            string v = key == null ? null : key.GetValue(value) as string;
                            if (!string.IsNullOrEmpty(v)) return v;
                        }
                    }
                    catch
                    {
                        // 权限/视图不支持都直接跳
                    }
                }
            }
            return null;
        }
    }
}
