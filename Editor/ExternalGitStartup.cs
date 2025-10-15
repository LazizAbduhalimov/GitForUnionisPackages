using System.IO;
using System.Linq;
using UnityEditor;

[InitializeOnLoad]
public static class ExternalGitStartup
{
    private const string MenuPath = "Tools/Git/External Libraries...";
    private const string ShowGitWindowIfAnyMissingsKey = "ShowGitWindowIfAnyMissingsKey";

    [MenuItem(MenuPath)]
    private static void OpenWindow()
    {
        ExternalGitWindow.ShowWindow();
    }

    static ExternalGitStartup()
    {
        EditorApplication.update += CheckForMissingGits;
    }

    private static void CheckForMissingGits()
    {
        // Run only once on startup
        EditorApplication.update -= CheckForMissingGits;

        try
        {
            if (!SessionState.GetBool(ShowGitWindowIfAnyMissingsKey, true)) return;

            var urls = GitRequirementsUtil.LoadUrls();
            if (urls == null || urls.Count == 0) return;

            var proj = GitPathsUtil.GetProjectRoot();
            if (string.IsNullOrEmpty(proj)) return;

            var root = Path.Combine(proj, GitPathsUtil.ExternalLibRootCorrect).Replace("\\", "/");

            static bool IsGitRepo(string p) =>
                Directory.Exists(Path.Combine(p, GitPathsUtil.GitDirName));

            bool anyMissing = false;
            foreach (var url in urls)
            {
                var folder = GitRequirementsUtil.GuessFolderFromUrl(url);
                if (string.IsNullOrEmpty(folder)) continue;
                var target = Path.Combine(root, folder).Replace("\\", "/");
                bool ok = Directory.Exists(target) && IsGitRepo(target);
                if (!ok) { anyMissing = true; break; }
            }

            if (anyMissing)
            {
                SessionState.SetBool(ShowGitWindowIfAnyMissingsKey, false);
                ExternalGitWindow.ShowWindow();
            }
        }
        catch { /* ignore on startup */ }
    }
}
