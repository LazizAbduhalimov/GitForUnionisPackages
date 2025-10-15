using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;

namespace EasyGit
{
    public enum ExternalGitClient
    {
        GitHubDesktop,
        SourceTree,
        VSCode
    }

    public static class GitExternalUi
    {
    private const string PrefDefaultClient = "ExternalGitUI.DefaultClient";

        public static ExternalGitClient GetDefaultClient()
        {
            var name = EditorPrefs.GetString(PrefDefaultClient, ExternalGitClient.GitHubDesktop.ToString());
            return Enum.TryParse(name, out ExternalGitClient parsed) ? parsed : ExternalGitClient.GitHubDesktop;
        }

        public static void SetDefaultClient(ExternalGitClient client)
        {
            EditorPrefs.SetString(PrefDefaultClient, client.ToString());
        }

        // Custom client removed per requirements

        public static bool OpenDefault(string repoPath)
        {
            return Open(GetDefaultClient(), repoPath);
        }

        public static bool Open(ExternalGitClient client, string repoPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
                {
                    EditorUtility.DisplayDialog("Git UI", "Путь к репозиторию не найден.", "OK");
                    return false;
                }
                bool isRepo = Directory.Exists(Path.Combine(repoPath, ".git"));

                switch (client)
                {
                    case ExternalGitClient.GitHubDesktop:
                        // Open cmd, cd to repo path and run 'github', then close the console
                        return TryStartCmd("cd /d \"" + repoPath + "\" && github");

                    case ExternalGitClient.SourceTree:
                        {
                            // Launch SourceTree directly with -f "repoPath"
                            var exeCandidates = new []
                            {
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceTree", "SourceTree.exe"),
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Atlassian", "SourceTree", "SourceTree.exe"),
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Atlassian", "SourceTree", "SourceTree.exe")
                            };
                            foreach (var exe in exeCandidates)
                            {
                                if (File.Exists(exe) && TryStartExe(exe, $"-f \"{repoPath}\"")) return true;
                            }
                            if (TryStartExe("SourceTree.exe", $"-f \"{repoPath}\"")) return true; // PATH
                            return LaunchUri("sourcetree://");
                        }

                    case ExternalGitClient.VSCode:
                        // Prefer Code.exe with -n to open a new window even if another project is open; then URI/command fallbacks
                        {
                            var exeCandidates = new []
                            {
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe")
                            };
                            foreach (var exe in exeCandidates)
                            {
                                if (File.Exists(exe) && TryStartExe(exe, $"-n \"{repoPath}\"")) return true;
                            }
                            if (TryStartExe("Code.exe", $"-n \"{repoPath}\"")) return true; // PATH fallback

                            var pathForUri = repoPath.Replace("\\", "/");
                            return LaunchUri($"vscode://open?folder={Uri.EscapeDataString(repoPath)}")
                                   || LaunchUri($"vscode://file/{pathForUri}")
                                   || TryStartCmd("start \"\" code -n \"" + repoPath + "\"");
                        }

                    // Rider support removed per request

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[GitUI] Ошибка открытия Git UI: {ex.Message}");
                return false;
            }
        }

        private static bool LaunchUri(string uri)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true
                };
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        private static bool TryStartExe(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args ?? string.Empty,
                    UseShellExecute = true
                };
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        private static bool TryStartCmd(string cmd)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/C " + cmd,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        private static bool RunCustomTemplate(string template, string repoPath)
        {
            // Template examples:
            //   "C:\\Path\\MyGitUI.exe" "{path}"
            //   start "" "sourcetree://openRepo?path={path}"
            var cmd = template.Replace("{path}", repoPath);
            return TryStartCmd(cmd);
        }
    }
}
