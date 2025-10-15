using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace EasyGit
{
    public static class GitRequirementsUtil
{
    [Serializable]
    private class GitRequirements { public List<string> urls = new(); }

    public static List<string> LoadUrls()
    {
        try
        {
            var path = GetManifestPathAbs();
            if (!File.Exists(path)) return new List<string>();
            var json = File.ReadAllText(path, Encoding.UTF8);
            var data = string.IsNullOrWhiteSpace(json) ? null : JsonUtility.FromJson<GitRequirements>(json);
            return data?.urls ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void SaveUrls(IEnumerable<string> urls)
    {
        try
        {
            var list = new GitRequirements { urls = urls?.Distinct().ToList() ?? new List<string>() };
            var path = GetManifestPathAbs();
            var dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonUtility.ToJson(list, true);
            File.WriteAllText(path, json, Encoding.UTF8);
            AssetDatabase.Refresh();
        }
        catch { }
    }

    public static void AddUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var urls = LoadUrls();
        if (!urls.Contains(url))
        {
            urls.Add(url);
            SaveUrls(urls);
        }
    }

    public static string GetManifestPathAbs()
    {
        var proj = GitPathsUtil.GetProjectRoot();
        var dir = Path.Combine(proj, GitPathsUtil.ExternalLibRootCorrect).Replace("\\", "/");
        return Path.Combine(dir, GitPathsUtil.RequirementsFileName).Replace("\\", "/");
    }

    public static string GuessFolderFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            var last = url.TrimEnd('/').Split('/').Last();
            if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                last = last[..^4];
            return last;
        }
        catch { return string.Empty; }
    }
}
}
