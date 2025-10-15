using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using EasyGit.Editor.View;
using Debug = UnityEngine.Debug;

namespace EasyGit
{
    public class ExternalGitWindow : EditorWindow
    {
        private Vector2 _scroll;
        private string _gitUrl = "";
        private string _status = "";
        private bool _hasOutdatedDeps = false;
        private readonly Dictionary<string, DepInfo> _depInfoByUrl = new();
        private bool _isComputing; // background compute flag

        // Manifest required_gits.json
        [Serializable]
        private class GitRequirements
        {
            public List<string> urls = new();
        }

        private GitRequirements _requirements;

        public static void ShowWindow()
        {
            var wnd = GetWindow<ExternalGitWindow>("External Libraries");
            wnd.minSize = new Vector2(600, 300);
            wnd.RefreshStatus();
            wnd.Show();
        }

        private void OnEnable()
        {
            RefreshStatus();
        }

        private void OnFocus()
        {
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            var root = GetExistingRoot();
            if (string.IsNullOrEmpty(root))
            {
                _status = $"Папка {root} не найдена. Будет создана при первом импорте.";
            }
            else
            {
                // When everything is OK, don't show a noisy message
                _status = string.Empty;
            }

            Repaint();
            _requirements = LoadRequirements(); // загрузить/создать манифест
            // Defer computing repo states until the window has shown; do a lightweight pass first
            _isComputing = true;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    RecomputeDependenciesState(lightweight: true);
                }
                finally
                {
                    _isComputing = false;
                    Repaint();
                }
            };
        }

        private string GetExistingRoot() => GitPathsUtil.GetExistingLibRoot();

        private string GetPreferedRoot() => GitPathsUtil.GetPreferredLibRoot();


        private static bool IsGitRepo(string path) => Directory.Exists(Path.Combine(path, ".git"));

        private static bool IsDirectoryEmpty(string path) =>
            !Directory.EnumerateFileSystemEntries(path).Any();

        private void OnGUI()
        {
            using (new EditorGUILayout.VerticalScope())
            {
                if (!string.IsNullOrEmpty(_status))
                {
                    EditorGUILayout.HelpBox(_status, MessageType.Info);
                }

                if (_isComputing)
                {
                    EditorGUILayout.LabelField("Проверка зависимостей...", EditorStyles.miniLabel);
                }

                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorUtils.IconButton(() =>
                        {
                            UpdateBranches();
                            RunFullRecompute();
                        }, AwesomeIcons.Refresh, EditorUtils.IconStyle.Solid,
                        "Обновить ветки и статус зависимостей", 16, GUILayout.Width(50), GUILayout.Height(25));

                    EditorUtils.IconButton(() =>
                        {
                            var root = GetExistingRoot() ?? GetPreferedRoot();
                            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                            EditorUtility.RevealInFinder(root);
                        }, AwesomeIcons.Folder, EditorUtils.IconStyle.Solid, "Открыть в проводнике", 16,
                        GUILayout.Width(50), GUILayout.Height(25));
                    GUILayout.Space(10);
                    // Open Git server browser in a new docked tab next to this window
                    EditorUtils.IconButton(() =>
                        {
                            var wnd = GetWindow<GitServerBrowserWindow>("Git Server Browser", true,
                                typeof(ExternalGitWindow));
                            wnd.Show();
                        }, AwesomeIcons.Git, EditorUtils.IconStyle.Brand, "Открыть браузер Git-сервера", 16,
                        GUILayout.Width(50), GUILayout.Height(25));

                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Установить все зависимости", GUILayout.Width(220), GUILayout.Height(25)))
                    {
                        InstallAllDependencies();
                    }
                }

                // Секция зависимостей из required_gits.json (прокручиваемая)
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                DrawDependenciesSection();
                EditorGUILayout.EndScrollView();

                GUILayout.Space(8);
                EditorGUILayout.LabelField("Импорт нового репозитория", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Git URL:", GUILayout.Width(70));
                    _gitUrl = EditorGUILayout.TextField(_gitUrl);
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_gitUrl)))
                    {
                        if (GUILayout.Button("Клонировать", GUILayout.Width(140), GUILayout.Height(24)))
                        {
                            ImportNewRepo(_gitUrl);
                        }
                    }
                }
            }
        }

        private void UpdateBranches()
        {
            foreach (var kv in _depInfoByUrl.Values)
            {
                if (kv.ok)
                {
                    // Prune each remote to drop deleted remote branches
                    if (GitCommand.Run("remote", kv.targetPath, out var remOut) && !string.IsNullOrWhiteSpace(remOut))
                    {
                        foreach (var r in remOut.Split('\n'))
                        {
                            var remote = (r ?? string.Empty).Trim();
                            if (string.IsNullOrEmpty(remote)) continue;
                            GitCommand.Run($"fetch {remote} --prune", kv.targetPath, out _);
                        }
                    }
                    else
                    {
                        // Fallback: prune all
                        GitCommand.Run("fetch --all --prune", kv.targetPath, out _);
                    }
                }
            }
        }

        // removed legacy repo listing UI

        // ===== Dependencies (required_gits.json) =====

        private void DrawDependenciesSection()
        {
            var libsRoot = GetExistingRoot() ?? GetPreferedRoot();
            using (new EditorGUILayout.VerticalScope("box"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Зависимости (required_gits.json)", EditorStyles.boldLabel);
                }

                if (_requirements == null || _requirements.urls == null || _requirements.urls.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Зависимости не заданы. Они будут добавляться сюда автоматически после клонирования через это окно.",
                        MessageType.Info);
                    return;
                }

                foreach (var url in _requirements.urls.ToList())
                {
                    DrawDependencyRow(url, libsRoot);
                }

                if (_hasOutdatedDeps)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.HelpBox(
                        "Есть не обновлённые зависимости. Рекомендуется обновить их до последних коммитов.",
                        MessageType.Warning);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Обновить все устаревшие", GUILayout.Width(220)))
                        {
                            UpdateAllOutdated();
                        }
                    }
                }
            }
        }

        private void DrawDependencyRow(string url, string libsRoot)
        {
            _depInfoByUrl.TryGetValue(url, out var info);
            var hasCached = _depInfoByUrl.ContainsKey(url);
            if (!hasCached) info = new DepInfo();
            // fallback if cache is empty (не запускаем git-команды здесь)
            if (string.IsNullOrEmpty(info.targetPath))
            {
                var folder = GitRequirementsUtil.GuessFolderFromUrl(url);
                info.targetPath = Path.Combine(libsRoot, folder).Replace("\\", "/");
                info.exists = Directory.Exists(info.targetPath);
                info.ok = info.exists && IsGitRepo(info.targetPath);
                _depInfoByUrl[url] = info; // cache the basic info to avoid repeated work in GUI
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    // Clickable repo name from last path segment; tooltip shows absolute path and HTTPS URL
                    var displayName = Path.GetFileName(info.targetPath);
                    var tooltip = string.Join("\n",
                        new[] { info.targetPath, url }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    var nameContent = new GUIContent(string.IsNullOrEmpty(displayName) ? url : displayName, tooltip);
                    if (GUILayout.Button(nameContent, EditorStyles.linkLabel, GUILayout.Width(220)))
                    {
                        var openUrl = url;
                        if (!string.IsNullOrWhiteSpace(openUrl))
                        {
                            Application.OpenURL(openUrl);
                        }
                    }

                    GUILayout.FlexibleSpace();

                    if (!info.exists)
                    {
                        GUILayout.Label("Не установлено ⚠️", EditorStyles.miniLabel, GUILayout.Width(240));

                        EditorUtils.Button("Установить", () => { InstallDependency(url, libsRoot); }, height: 25,
                            width: 120);
                        return;
                    }

                    if (!info.ok)
                    {
                        GUILayout.Label("Папка без .git (можно добавить в Git UI)", EditorStyles.miniLabel,
                            GUILayout.Width(280));

                        if (GUILayout.Button("Установить", GUILayout.Width(120), GUILayout.Height(25)))
                        {
                            InstallDependency(url, libsRoot);
                        }

                        return;
                    }

                    // Repo installed: use cached update state
                    switch (info.state)
                    {
                        case GitRepoUtil.RepoUpdateState.UpToDate:
                            GUILayout.Label("актуально", GUILayout.Width(160));
                            break;
                        case GitRepoUtil.RepoUpdateState.Behind:
                            _hasOutdatedDeps = true;
                            GUILayout.Label(
                                string.IsNullOrEmpty(info.details)
                                    ? "Есть обновления"
                                    : $"Есть обновления ({info.details})", EditorStyles.miniLabel,
                                GUILayout.Width(220));
                            if (GUILayout.Button("Обновить", GUILayout.Width(120)))
                            {
                                UpdateDependency(info.targetPath);
                            }

                            break;
                        case GitRepoUtil.RepoUpdateState.Ahead:
                            GUILayout.Label(
                                string.IsNullOrEmpty(info.details)
                                    ? "Локальные изменения"
                                    : $"Локальные изменения ({info.details})", EditorStyles.miniLabel,
                                GUILayout.Width(240));
                            break;
                        case GitRepoUtil.RepoUpdateState.Diverged:
                            _hasOutdatedDeps = true;
                            GUILayout.Label("Расхождение с удалённой веткой", EditorStyles.miniLabel,
                                GUILayout.Width(240));
                            if (GUILayout.Button("Обновить", GUILayout.Width(120)))
                            {
                                UpdateDependency(info.targetPath);
                            }

                            break;
                        default:
                            GUILayout.Label("Не удалось определить состояние", EditorStyles.miniLabel,
                                GUILayout.Width(240));
                            break;
                    }

                    // Open in external Git UI; right-click opens client selection
                    EditorUtils.IconButton(() =>
                        {
                            // If right-click, open client menu and consume event
                            if (Event.current != null &&
                                (Event.current.button == 1 || Event.current.type == EventType.ContextClick))
                            {
                                ShowExternalGitUiMenu(info.targetPath);
                                Event.current.Use();
                                return;
                            }

                            // Left-click: open default client; if fails, show menu
                            if (!LocalGitUiOpenDefault(info.targetPath))
                            {
                                ShowExternalGitUiMenu(info.targetPath);
                            }
                        }, GetExternalClientIcon(), EditorUtils.IconStyle.Brand,
                        "Открыть в Git UI (ПКМ — выбрать клиент)",
                        16, GUILayout.Width(50), GUILayout.Height(25));

                    EditorUtils.IconButton(() =>
                        {
                            if (!string.IsNullOrEmpty(info.targetPath)) GitPathsUtil.PingPath(info.targetPath);
                        }, AwesomeIcons.Eye, EditorUtils.IconStyle.Solid, "Пинговать путь", 16, GUILayout.Width(50),
                        GUILayout.Height(25));

                    if (GUILayout.Button("Удалить", GUILayout.Width(100), GUILayout.Height(25)))
                    {
                        TryDeleteDependency(url, info);
                        return;
                    }
                }

                if (info.ok)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(12);
                        GUILayout.Label("Ветка:", GUILayout.Width(46));
                        // Combine local and remote branches; remote entries shown as origin/xxx
                        var local = info.branches ?? Array.Empty<string>();
                        var remote = info.remoteBranches ?? Array.Empty<string>();
                        var combined = new List<string>(local);
                        if (local.Length > 0 && remote.Length > 0) combined.Add("— remotes —");
                        combined.AddRange(remote);

                        var cur = info.currentBranch ?? string.Empty;
                        // Default selection: current local branch
                        var idx = Mathf.Max(0, combined.IndexOf(cur));
                        using (new EditorGUI.DisabledScope(info.hasLocalChanges || combined.Count == 0))
                        {
                            var newIdx = EditorGUILayout.Popup(idx, combined.ToArray(), GUILayout.Width(220));
                            if (newIdx != idx && newIdx >= 0 && newIdx < combined.Count)
                            {
                                var selection = combined[newIdx];
                                if (selection == "— remotes —") return;

                                if (!info.hasLocalChanges)
                                {
                                    if (selection.StartsWith("origin/", StringComparison.Ordinal))
                                    {
                                        // Create a local tracking branch from remote and switch to it
                                        var localName = selection.Substring("origin/".Length);
                                        if (GitRepoUtil.CreateTrackingBranch(info.targetPath, localName, selection))
                                        {
                                            Debug.Log(
                                                $"[Git] Created tracking branch {localName} for {selection} in {info.targetPath}");
                                            AssetDatabase.Refresh();
                                            RunFullRecompute();
                                        }
                                        else
                                        {
                                            EditorUtility.DisplayDialog("Смена ветки",
                                                "Не удалось создать локальную ветку для удалённой.", "OK");
                                        }
                                    }
                                    else
                                    {
                                        if (GitRepoUtil.SwitchBranch(info.targetPath, selection))
                                        {
                                            Debug.Log($"[Git] Switched {info.targetPath} to {selection}");
                                            AssetDatabase.Refresh();
                                            RunFullRecompute();
                                        }
                                        else
                                        {
                                            EditorUtility.DisplayDialog("Смена ветки",
                                                "Не удалось переключить ветку. Убедитесь, что нет локальных изменений.",
                                                "OK");
                                        }
                                    }
                                }
                                else
                                {
                                    EditorUtility.DisplayDialog("Смена ветки",
                                        "Нельзя переключить ветку: есть локальные изменения.", "OK");
                                }
                            }
                        }

                        // Delete branch UI was removed for safety. Use "Обновить ветки" (fetch/prune) to hide branches deleted remotely.
                        if (info.hasLocalChanges)
                        {
                            GUILayout.Label("Есть локальные изменения", EditorStyles.miniLabel, GUILayout.Width(180));
                        }
                    }
                }
            }
        }

        private void ShowExternalGitUiMenu(string repoPath)
        {
            var menu = new GenericMenu();

            void Add(LocalExternalGitClient client, string title)
            {
                menu.AddItem(new GUIContent(title), LocalGitUiGetDefaultClient() == client, () =>
                {
                    // Only set default on selection; do not open automatically
                    LocalGitUiSetDefaultClient(client);
                });
            }

            Add(LocalExternalGitClient.GitHubDesktop, "GitHub Desktop");
            Add(LocalExternalGitClient.SourceTree, "SourceTree");
            Add(LocalExternalGitClient.VSCode, "VS Code");
            menu.ShowAsContext();
        }

        // ===== Minimal local implementation of external Git UI launcher =====
        private enum LocalExternalGitClient
        {
            GitHubDesktop,
            SourceTree,
            VSCode
        }

        private const string PrefDefaultClient = "ExternalGitUI.DefaultClient";

        private LocalExternalGitClient LocalGitUiGetDefaultClient()
        {
            var name = EditorPrefs.GetString(PrefDefaultClient, LocalExternalGitClient.GitHubDesktop.ToString());
            if (Enum.TryParse(name, out LocalExternalGitClient parsed)) return parsed;
            return LocalExternalGitClient.GitHubDesktop;
        }

        private void LocalGitUiSetDefaultClient(LocalExternalGitClient client)
        {
            EditorPrefs.SetString(PrefDefaultClient, client.ToString());
        }

        // Custom command support removed per requirements

        private bool LocalGitUiOpenDefault(string repoPath)
        {
            return LocalGitUiOpen(LocalGitUiGetDefaultClient(), repoPath);
        }

        private bool LocalGitUiOpen(LocalExternalGitClient client, string repoPath)
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
                    case LocalExternalGitClient.GitHubDesktop:
                        // Open cmd, cd to repo path and run 'github'
                        return LocalTryStartCmd("cd /d \"" + repoPath + "\" && github");

                    case LocalExternalGitClient.SourceTree:
                    {
                        // Prefer local app data installation, then Program Files variants, then PATH
                        var exeCandidates = new List<string>
                        {
                            Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceTree",
                                "SourceTree.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                "Atlassian", "SourceTree", "SourceTree.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                "Atlassian", "SourceTree", "SourceTree.exe")
                        };
                        foreach (var exe in exeCandidates)
                        {
                            if (File.Exists(exe))
                            {
                                if (LocalTryStartExe(exe, $"-f \"{repoPath}\"")) return true;
                            }
                        }

                        // Try by name on PATH
                        if (LocalTryStartExe("SourceTree.exe", $"-f \"{repoPath}\"")) return true;
                        // Last resort: open the app without args via URI
                        return LocalLaunchUri("sourcetree://");
                    }

                    case LocalExternalGitClient.VSCode:
                    {
                        // Prefer launching Code.exe with -n to force a new window even if another project is open
                        var exeCandidates = new List<string>
                        {
                            Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs",
                                "Microsoft VS Code", "Code.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                "Microsoft VS Code", "Code.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                "Microsoft VS Code", "Code.exe")
                        };
                        foreach (var exe in exeCandidates)
                        {
                            if (File.Exists(exe) && LocalTryStartExe(exe, $"-n \"{repoPath}\"")) return true;
                        }

                        if (LocalTryStartExe("Code.exe", $"-n \"{repoPath}\"")) return true; // PATH fallback

                        // URI fallbacks
                        var pathForUri = repoPath.Replace("\\", "/");
                        return LocalLaunchUri($"vscode://open?folder={Uri.EscapeDataString(repoPath)}")
                               || LocalLaunchUri($"vscode://file/{pathForUri}")
                               || LocalTryStartCmd("start \"\" code -n \"" + repoPath + "\"");
                    }



                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GitUI] Ошибка открытия Git UI: {ex.Message}");
                return false;
            }
        }

        private bool LocalLaunchUri(string uri)
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
            catch
            {
                return false;
            }
        }

        private bool LocalTryStartExe(string exe, string args)
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
            catch
            {
                return false;
            }
        }

        private bool LocalTryStartCmd(string cmd)
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
            catch
            {
                return false;
            }
        }

        // Removed: LocalRunCustomTemplate (custom launch templates are no longer supported)

        private string GetExternalClientIcon()
        {
            switch (LocalGitUiGetDefaultClient())
            {
                case LocalExternalGitClient.GitHubDesktop: return AwesomeIcons.GitHubDesktop;
                case LocalExternalGitClient.SourceTree: return AwesomeIcons.SourceTree;
                case LocalExternalGitClient.VSCode: return AwesomeIcons.Git; // As requested, use Git icon for VS Code
                default: return AwesomeIcons.Git;
            }
        }

        private bool IsExternalClientIconSolid()
        {
            // Use solid Git icon for VS Code to keep it visually distinct
            return LocalGitUiGetDefaultClient() == LocalExternalGitClient.VSCode;
        }

        private void RecomputeDependenciesState(bool lightweight)
        {
            _depInfoByUrl.Clear();
            _hasOutdatedDeps = false;
            var libsRoot = GetExistingRoot() ?? GetPreferedRoot();
            if (_requirements?.urls == null) return;
            foreach (var url in _requirements.urls)
            {
                var folder = GitRequirementsUtil.GuessFolderFromUrl(url);
                var target = Path.Combine(libsRoot, folder).Replace("\\", "/");
                var info = new DepInfo
                {
                    targetPath = target,
                    exists = Directory.Exists(target)
                };
                info.ok = info.exists && IsGitRepo(target);
                if (info.ok)
                {
                    // lightweight: skip fetch to avoid network cost; full: include fetch
                    info.state = lightweight
                        ? GitRepoUtil.GetRepoUpdateState(target, out info.details, fetchBeforeCheck: false)
                        : GitRepoUtil.GetRepoUpdateState(target, out info.details, fetchBeforeCheck: true);
                    // branches & local changes (local-only ops, safe in lightweight)
                    info.branches = GitRepoUtil.GetLocalBranches(target, out info.currentBranch);
                    info.hasLocalChanges = GitRepoUtil.HasLocalChanges(target);
                    info.remoteBranches = GitRepoUtil.GetRemoteBranches(target);
                    if (info.state == GitRepoUtil.RepoUpdateState.Behind ||
                        info.state == GitRepoUtil.RepoUpdateState.Diverged)
                        _hasOutdatedDeps = true;
                }
                else
                {
                    info.state = GitRepoUtil.RepoUpdateState.Unknown;
                }

                _depInfoByUrl[url] = info;
            }
        }
        // removed: GetRepoUpdateState and FirstNonEmptyLine — centralized in GitRepoUtil

        private void UpdateDependency(string repoDir)
        {
            // fast-forward only to avoid unintended merges
            if (GitCommand.Run("pull --ff-only", repoDir, out var output))
            {
                Debug.Log($"[Git] Updated dependency: {repoDir}\n{output}");
            }
            else
            {
                Debug.LogError($"[Git] Failed to update dependency: {repoDir}\n{output}");
            }

            AssetDatabase.Refresh();
            RefreshStatus();
        }

        private void InstallAllDependencies()
        {
            var libsRoot = GetExistingRoot() ?? GetPreferedRoot();
            if (!Directory.Exists(libsRoot)) Directory.CreateDirectory(libsRoot);

            int installed = 0, skipped = 0, errors = 0;
            foreach (var url in _requirements.urls)
            {
                var folder = GitRequirementsUtil.GuessFolderFromUrl(url);
                var target = Path.Combine(libsRoot, folder).Replace("\\", "/");

                try
                {
                    if (Directory.Exists(target))
                    {
                        if (IsGitRepo(target))
                        {
                            skipped++;
                            continue;
                        }

                        if (!IsDirectoryEmpty(target))
                        {
                            Debug.LogWarning($"[Git] Пропуск: {target} существует и не пуст, но не git.");
                            errors++;
                            continue;
                        }

                        // exists but empty -> clone into
                        if (GitCommand.Run("clone " + url + " .", target, out var out1))
                        {
                            Debug.Log($"[Git] clone into existing empty:\n{out1}");
                            installed++;
                        }
                        else
                        {
                            Debug.LogError($"[Git] Ошибка клонирования в {target}");
                            errors++;
                        }
                    }
                    else
                    {
                        var workDir = Directory.GetParent(target)?.FullName.Replace("\\", "/");
                        if (GitCommand.Run($"clone {url} \"{target}\"", workDir, out var out2))
                        {
                            Debug.Log($"[Git] clone into new folder:\n{out2}");
                            installed++;
                        }
                        else
                        {
                            Debug.LogError($"[Git] Ошибка клонирования в {target}");
                            errors++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[Git] Ошибка: " + ex.Message);
                    errors++;
                }
            }

            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Зависимости",
                $"Установлено: {installed}\nПропущено: {skipped}\nОшибок: {errors}", "OK");
            RefreshStatus();
        }

        private void UpdateAllOutdated()
        {
            // Iterate cached entries; update those marked Behind or Diverged
            var toUpdate = _depInfoByUrl.Values
                .Where(d => d.ok && (d.state == GitRepoUtil.RepoUpdateState.Behind ||
                                     d.state == GitRepoUtil.RepoUpdateState.Diverged))
                .Select(d => d.targetPath)
                .Distinct()
                .ToArray();

            int ok = 0, fail = 0;
            foreach (var repoDir in toUpdate)
            {
                if (GitCommand.Run("pull --ff-only", repoDir, out var output))
                {
                    Debug.Log($"[Git] Updated dependency: {repoDir}\n{output}");
                    ok++;
                }
                else
                {
                    Debug.LogWarning($"[Git] Failed to update dependency: {repoDir}\n{output}");
                    fail++;
                }
            }

            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Обновление зависимостей", $"Обновлено: {ok}\nС ошибками: {fail}", "OK");
            // After updates, do a full recompute to reflect latest state
            RunFullRecompute();
        }

        private void RunFullRecompute()
        {
            if (_isComputing) return;
            _isComputing = true;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    RecomputeDependenciesState(lightweight: false);
                }
                finally
                {
                    _isComputing = false;
                    Repaint();
                }
            };
        }

        private void InstallDependency(string url, string libsRoot)
        {
            var folder = GitRequirementsUtil.GuessFolderFromUrl(url);
            var target = Path.Combine(libsRoot, folder).Replace("\\", "/");
            try
            {
                Directory.CreateDirectory(target);
                if (IsDirectoryEmpty(target))
                {
                    if (GitCommand.Run("clone " + url + " .", target, out var output))
                    {
                        Debug.Log("[Git] " + output);
                        AssetDatabase.Refresh();
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Импорт", "Ошибка при клонировании. См. Console.", "OK");
                    }
                }
                else
                {
                    EditorUtility.DisplayDialog("Импорт", "Папка не пуста, клонирование невозможно.", "OK");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Git] Ошибка установки зависимости: " + ex.Message);
            }

            RefreshStatus();
        }

        private void TryDeleteDependency(string url, DepInfo info)
        {
            var path = info.targetPath;
            var warning = info.hasLocalChanges
                ? "В этой зависимости есть локальные изменения. Удалить всё равно?"
                : "Вы уверены, что хотите удалить эту зависимость?";
            if (!EditorUtility.DisplayDialog("Удаление зависимости", warning, "Удалить", "Отмена")) return;

            // Remove folder
            var assetPath = GitPathsUtil.AbsoluteToAssetPathIfPossible(path);
            bool ok = false;
            try
            {
                if (!string.IsNullOrEmpty(assetPath))
                {
                    ok = AssetDatabase.DeleteAsset(assetPath);
                }

                if (!ok)
                {
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                    var meta = path + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                    ok = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Git] Ошибка удаления: " + ex.Message);
            }

            if (ok)
            {
                // Remove from manifest
                var urls = GitRequirementsUtil.LoadUrls();
                urls.Remove(url);
                GitRequirementsUtil.SaveUrls(urls);
                AssetDatabase.Refresh();
                RefreshStatus();
            }
            else
            {
                EditorUtility.DisplayDialog("Удаление зависимости", "Не удалось удалить зависимость.", "OK");
            }
        }

        private GitRequirements LoadRequirements()
        {
            var urls = GitRequirementsUtil.LoadUrls();
            return new GitRequirements { urls = urls };
        }

        private void AddRequiredUrl(string url)
        {
            GitRequirementsUtil.AddUrl(url);
            _requirements ??= new GitRequirements();
            _requirements.urls ??= new List<string>();
            if (!_requirements.urls.Contains(url)) _requirements.urls.Add(url);
        }

        // removed: GuessFolderFromUrl — use GitRequirementsUtil.GuessFolderFromUrl

        private void ImportNewRepo(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                Debug.LogWarning("[Git] URL пуст.");
                return;
            }

            var folderName = GitRequirementsUtil.GuessFolderFromUrl(url);

            var root = GetExistingRoot() ?? GetPreferedRoot();
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);

            var target = Path.Combine(root, folderName).Replace("\\", "/");
            if (Directory.Exists(target) && !IsDirectoryEmpty(target))
            {
                EditorUtility.DisplayDialog("Импорт", $"Целевая папка уже существует и не пуста:\n{target}", "OK");
                EditorUtility.RevealInFinder(target);
                return;
            }

            // Если папка существует, но пуста — клонируем внутрь через "git clone URL ."
            bool success;
            string output;
            if (Directory.Exists(target))
            {
                success = GitCommand.Run("clone " + url + " .", target, out output);
            }
            else
            {
                // Клонируем в новую папку
                var workDir = Directory.GetParent(target)?.FullName.Replace("\\", "/");
                success = GitCommand.Run($"clone {url} \"{target}\"", workDir, out output);
            }

            Debug.Log("[Git] " + output);
            if (success)
            {
                // добавляем в required_gits.json
                AddRequiredUrl(url);
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Импорт", "Репозиторий успешно клонирован:\n" + target, "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("Импорт", "Произошла ошибка при клонировании. См. Console.", "OK");
            }
        }
    }
}

