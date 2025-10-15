using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using MapTiles.Editor.Git;
using Tools.Editor;

public class GitServerBrowserWindow : EditorWindow
{
    [Serializable]
    private class Project
    {
        public int id;
        public string name;
        public string path_with_namespace;
        public string http_url_to_repo;
        public string web_url;
        public string last_activity_at;
    }

    [Serializable]
    private class ProjectList
    {
        public Project[] items;
    }

    // GitHub API models
    [Serializable]
    private class GithubUser
    {
        public string login;
        public string type; // "User" | "Organization"
    }

    [Serializable]
    private class GithubOrg
    {
        public string login;
    }

    [Serializable]
    private class GithubRepo
    {
        public int id;
        public string name;
        public string full_name;
        public string clone_url;
        public string html_url;
        public string updated_at;
        public GithubUser owner;
    }

    [Serializable]
    private class GithubRepoList
    {
        public GithubRepo[] items;
    }

    [Serializable]
    private class GithubSearchResult
    {
        public int total_count;
        public bool incomplete_results;
        public GithubRepo[] items;
    }

    private const string PrefBaseUrl = "GitBrowser.BaseUrl";
    private const string SessTokenKey = "GitBrowser.SessionToken";
    private const string SessRememberKey = "GitBrowser.SessionRemember";
    private const string PrefOrgsOnly = "GitBrowser.OrgsOnly";
    private const string PrefSelectedOrg = "GitBrowser.SelectedOrg";

    private string _baseUrl = "https://api.github.com";
    private string _token = string.Empty;
    private bool _rememberInSession = false;
    private string _search = string.Empty;
    private string _status = string.Empty;
    private bool _isLoading = false;
    private int _page = 1;
    private int _totalPages = 1;
    private int _perPage = 25;
    private Vector2 _scroll;
    private List<Project> _projects = new List<Project>();
    // Debounce state for search
    private double _debounceAt = 0; // timeSinceStartup when to fire debounced search; 0 = none
    private const double DebounceDelay = 0.5; // seconds
    private string _lastQuerySearched = string.Empty;
    private bool _orgsOnly = false;
    private List<GithubOrg> _orgs = new List<GithubOrg>();
    private string _selectedOrg = ""; // empty = all

    private void OnEnable()
    {
        LoadPrefs();
        // load orgs list in background (best-effort)
        LoadOrgsAsync();
    }

    private void LoadPrefs()
    {
        _baseUrl = EditorPrefs.GetString(PrefBaseUrl, _baseUrl);
        _rememberInSession = SessionState.GetBool(SessRememberKey, false);
        _token = _rememberInSession ? SessionState.GetString(SessTokenKey, string.Empty) : string.Empty;
        _orgsOnly = EditorPrefs.GetBool(PrefOrgsOnly, _orgsOnly);
        _selectedOrg = EditorPrefs.GetString(PrefSelectedOrg, _selectedOrg);
    }

    private void SavePrefs()
    {
        EditorPrefs.SetString(PrefBaseUrl, _baseUrl ?? string.Empty);
        EditorPrefs.SetBool(PrefOrgsOnly, _orgsOnly);
        EditorPrefs.SetString(PrefSelectedOrg, _selectedOrg ?? string.Empty);
        // Token is not persisted by design; session-only handled via SessionState
        SessionState.SetBool(SessRememberKey, _rememberInSession);
        if (_rememberInSession)
            SessionState.SetString(SessTokenKey, _token ?? string.Empty);
        else
            SessionState.EraseString(SessTokenKey);
    }

    private async void LoadOrgsAsync()
    {
        if (string.IsNullOrWhiteSpace(_token) || string.IsNullOrWhiteSpace(_baseUrl)) return;
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Unity-MapTiles", "1.0"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

                var url = (_baseUrl?.TrimEnd('/') ?? "https://api.github.com") + "/user/orgs?per_page=100";
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) return;
                // body is array
                var wrapped = "{\"items\":" + body + "}";
                var data = JsonUtility.FromJson<GithubOrgList>(wrapped);
                _orgs = data?.items?.ToList() ?? new List<GithubOrg>();
                Repaint();
            }
        }
        catch { /* ignore org load errors */ }
    }

    [Serializable]
    private class GithubOrgList { public GithubOrg[] items; }


    private string BuildApiUrl()
    {
        var root = _baseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(root)) return string.Empty;
        if (!string.IsNullOrWhiteSpace(_search))
        {
            // GitHub search API; if org selected, scope to org
            var qRaw = (string.IsNullOrWhiteSpace(_selectedOrg) ? string.Empty : $"org:{_selectedOrg} ") + _search;
            var q = Uri.EscapeDataString(qRaw);
            return root + $"/search/repositories?q={q}&per_page={_perPage}&page={_page}&sort=updated&order=desc";
        }
        else
        {
            // List repos for selected org, otherwise list user's repos
            if (!string.IsNullOrWhiteSpace(_selectedOrg))
            {
                return root + $"/orgs/{_selectedOrg}/repos?per_page={_perPage}&page={_page}&sort=updated&direction=desc&type=all";
            }
            else
            {
                var affiliation = _orgsOnly ? "organization_member" : "owner,collaborator,organization_member";
                return root + $"/user/repos?per_page={_perPage}&page={_page}&sort=updated&direction=desc&visibility=all&affiliation={affiliation}";
            }
        }
    }

    private async void LoadProjectsAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseUrl)) { _status = "Укажите Base URL"; Repaint(); return; }
        if (string.IsNullOrWhiteSpace(_token)) { _status = "Укажите Token"; Repaint(); return; }

        _isLoading = true;
        _status = "Загрузка...";
        Repaint();

        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                // GitHub auth header and required User-Agent
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Unity-MapTiles", "1.0"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", _token);
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

                var url = BuildApiUrl();
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();

                _projects.Clear();

                if (!resp.IsSuccessStatusCode)
                {
                    _status = $"Ошибка {(int)resp.StatusCode}: {resp.ReasonPhrase}";
                }
                else
                {
                    // Parse GitHub response
                    List<Project> mapped = null;
                    bool isSearch = url.Contains("/search/repositories", StringComparison.OrdinalIgnoreCase) || body.TrimStart().StartsWith("{\"total_count\"", StringComparison.Ordinal);
                    if (isSearch)
                    {
                        var searchData = JsonUtility.FromJson<GithubSearchResult>(body);
                        var repos = searchData?.items ?? Array.Empty<GithubRepo>();
                        mapped = repos.Select(r => new Project
                        {
                            id = r.id,
                            name = r.name,
                            path_with_namespace = r.full_name,
                            http_url_to_repo = r.clone_url,
                            web_url = r.html_url,
                            last_activity_at = r.updated_at
                        }).ToList();
                        // Compute total pages from total_count when possible (GitHub caps at 1000 results)
                        var total = Math.Max(0, searchData?.total_count ?? 0);
                        var maxPages = (int)Math.Ceiling(Math.Min(total, 1000) / (double)_perPage);
                        _totalPages = Math.Max(1, maxPages);
                        // Page remains as requested
                    }
                    else
                    {
                        // Body is an array of repos
                        var wrappedRepos = "{\"items\":" + body + "}";
                        var repoList = JsonUtility.FromJson<GithubRepoList>(wrappedRepos);
                        var repos = repoList?.items ?? Array.Empty<GithubRepo>();
                        mapped = repos.Select(r => new Project
                        {
                            id = r.id,
                            name = r.name,
                            path_with_namespace = r.full_name,
                            http_url_to_repo = r.clone_url,
                            web_url = r.html_url,
                            last_activity_at = r.updated_at
                        }).ToList();
                        // Infer pages from Link header if present
                        _totalPages = 1;
                        _page = Math.Max(1, _page);
                        if (resp.Headers.TryGetValues("Link", out var linkVals))
                        {
                            var link = linkVals.FirstOrDefault() ?? string.Empty;
                            // Find rel="last" and extract page number
                            var parts = link.Split(',');
                            foreach (var part in parts)
                            {
                                if (part.Contains("rel=\"last\"", StringComparison.Ordinal))
                                {
                                    var idx = part.IndexOf("page=", StringComparison.OrdinalIgnoreCase);
                                    if (idx >= 0)
                                    {
                                        idx += 5;
                                        int end = idx;
                                        while (end < part.Length && char.IsDigit(part[end])) end++;
                                        if (int.TryParse(part.Substring(idx, end - idx), out var lastPage))
                                        {
                                            _totalPages = Math.Max(1, lastPage);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    _projects = mapped ?? new List<Project>();
                    _status = $"Найдено: {_projects.Count} (страница {_page}/{_totalPages})";
                }
            }
        }
        catch (Exception ex)
        {
            _status = "Ошибка: " + ex.Message;
        }
        finally
        {
            _isLoading = false;
            Repaint();
        }
    }

    private void OnGUI()
    {
        using (new EditorGUILayout.VerticalScope())
        {
            EditorGUILayout.LabelField("GitHub сервер", EditorStyles.boldLabel);

            // Row 1: Base URL, Token, Clear, Check
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Base URL:", GUILayout.Width(70));
                _baseUrl = EditorGUILayout.TextField(_baseUrl);
                EditorGUILayout.LabelField("Token:", GUILayout.Width(50));
                _token = EditorGUILayout.PasswordField(_token);
                if (GUILayout.Button("Очистить", GUILayout.Width(90)))
                {
                    _token = string.Empty;
                    SessionState.EraseString(SessTokenKey);
                }
                using (new EditorGUI.DisabledScope(_isLoading))
                {
                    if (GUILayout.Button("Проверить", GUILayout.Width(100)))
                    {
                        _page = 1;
                        if (_rememberInSession)
                            SessionState.SetString(SessTokenKey, _token ?? string.Empty);
                        SavePrefs();
                        LoadProjectsAsync();
                    }
                }
            }

            // Row 2: Remember in session toggle with wrapped label
            using (new EditorGUILayout.HorizontalScope())
            {
                bool newRemember = EditorGUILayout.Toggle(_rememberInSession, GUILayout.Width(18));
                var rememberLabel = new GUIContent(
                    "Запоминать токен на сессию (до перезапуска Unity)",
                    "Токен не сохраняется на диск. Включите 'Запоминать токен на сессию', чтобы не вводить его снова до перезапуска Unity.");
                var wrapStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };
                EditorGUILayout.LabelField(rememberLabel, wrapStyle, GUILayout.ExpandWidth(true));
                if (newRemember != _rememberInSession)
                {
                    _rememberInSession = newRemember;
                    SessionState.SetBool(SessRememberKey, _rememberInSession);
                    if (!_rememberInSession) SessionState.EraseString(SessTokenKey);
                    else SessionState.SetString(SessTokenKey, _token ?? string.Empty);
                    SavePrefs();
                }
            }

            // Row 3: Search, org filter, Enter-to-search, pagination
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Поиск:", GUILayout.Width(50));
                GUI.SetNextControlName("SearchField");
                var newSearch = EditorGUILayout.TextField(_search);
                if (!string.Equals(newSearch, _search, StringComparison.Ordinal))
                {
                    _search = newSearch;
                    // schedule debounced search
                    _debounceAt = EditorApplication.timeSinceStartup + DebounceDelay;
                }
                // Organizations dropdown (includes "Все")
                var options = new List<string> { "Все" };
                options.AddRange(_orgs.Select(o => o.login).Where(s => !string.IsNullOrWhiteSpace(s)));
                var curIdx = 0;
                if (!string.IsNullOrWhiteSpace(_selectedOrg))
                {
                    var idx = options.FindIndex(s => string.Equals(s, _selectedOrg, StringComparison.OrdinalIgnoreCase));
                    curIdx = idx >= 0 ? idx : 0;
                }
                var newIdx = EditorGUILayout.Popup(curIdx, options.ToArray(), GUILayout.Width(180));
                if (newIdx != curIdx)
                {
                    _selectedOrg = newIdx <= 0 ? string.Empty : options[newIdx];
                    SavePrefs();
                    _page = 1;
                    LoadProjectsAsync();
                }
                // Refresh orgs button
                EditorUtils.IconButton(() => LoadOrgsAsync(), AwesomeIcons.Refresh, Tools.Editor.EditorUtils.IconStyle.Solid, "Обновить список организаций", 14);

                // Trigger search on Enter while focus is in the search field
                var e = Event.current;
                if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
                {
                    if (GUI.GetNameOfFocusedControl() == "SearchField")
                    {
                        _page = 1;
                        // cancel debounce and remember this query
                        _debounceAt = 0;
                        _lastQuerySearched = _search;
                        LoadProjectsAsync();
                        e.Use();
                    }
                }

                using (new EditorGUI.DisabledScope(_isLoading))
                {
                    // Single search/refresh icon button
                    EditorUtils.IconButton(() => { _debounceAt = 0; _lastQuerySearched = _search; _page = 1; LoadProjectsAsync(); }, AwesomeIcons.Search, Tools.Editor.EditorUtils.IconStyle.Solid, "Искать/Обновить", 14);
                }
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_isLoading || _page <= 1))
                {
                    if (GUILayout.Button("<", GUILayout.Width(30)))
                    {
                        _page = Mathf.Max(1, _page - 1);
                        LoadProjectsAsync();
                    }
                }
                EditorGUILayout.LabelField($"Стр: {_page}/{_totalPages}", GUILayout.Width(100));
                using (new EditorGUI.DisabledScope(_isLoading || _page >= _totalPages))
                {
                    if (GUILayout.Button(">", GUILayout.Width(30)))
                    {
                        _page = Mathf.Min(_totalPages, _page + 1);
                        LoadProjectsAsync();
                    }
                }
            }

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.HelpBox(_status, _isLoading ? MessageType.Info : MessageType.None);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_projects.Count == 0 && !_isLoading)
            {
                EditorGUILayout.HelpBox("Нет данных. Нажмите 'Проверить' или значок поиска.", MessageType.Info);
            }
            else
            {
                foreach (var p in _projects)
                {
                    DrawProjectRow(p);
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }

    // Polling update to trigger debounced searches without user interaction
    private void Update()
    {
        if (_debounceAt > 0 && EditorApplication.timeSinceStartup >= _debounceAt)
        {
            // only run if not already loading and query changed since last search
            if (!_isLoading && !string.Equals(_search, _lastQuerySearched, StringComparison.Ordinal))
            {
                _page = 1;
                _lastQuerySearched = _search;
                _debounceAt = 0;
                LoadProjectsAsync();
            }
        }
    }

    private void DrawProjectRow(Project p)
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var title = p.path_with_namespace ?? p.name ?? ("#" + p.id);
                // Build tooltip without duplicates: normalize trailing '/' and '.git'
                string NormalizeUrl(string s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    var t = s.Trim();
                    if (t.EndsWith("/", StringComparison.Ordinal)) t = t.TrimEnd('/');
                    if (t.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4);
                    return t;
                }
                var web = NormalizeUrl(p.web_url);
                var http = NormalizeUrl(p.http_url_to_repo);
                var tips = new List<string>();
                if (!string.IsNullOrEmpty(web)) tips.Add(web);
                if (!string.IsNullOrEmpty(http) && !tips.Any(x => string.Equals(x, http, StringComparison.OrdinalIgnoreCase))) tips.Add(http);
                var tooltip = string.Join("\n", tips);
                if (GUILayout.Button(new GUIContent(title, tooltip), EditorStyles.linkLabel, GUILayout.Width(280)))
                {
                    if (!string.IsNullOrEmpty(p.web_url)) Application.OpenURL(p.web_url);
                }
                EditorGUILayout.LabelField(p.http_url_to_repo ?? "", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
                GUILayout.FlexibleSpace();

                // Determine if repo already exists locally to hide clone button
                var libsRoot = GitPathsUtil.GetExistingLibRoot() ?? GitPathsUtil.GetPreferredLibRoot();
                var folder = GitRequirementsUtil.GuessFolderFromUrl(p.http_url_to_repo ?? string.Empty);
                var target = string.IsNullOrEmpty(libsRoot) || string.IsNullOrEmpty(folder)
                    ? null
                    : System.IO.Path.Combine(libsRoot, folder).Replace("\\", "/");
                bool installed = false;
                try
                {
                    if (!string.IsNullOrEmpty(target) && System.IO.Directory.Exists(target))
                    {
                        // consider installed if non-empty directory
                        installed = System.IO.Directory.GetFileSystemEntries(target).Length > 0;
                    }
                }
                catch { /* ignore IO errors */ }

                bool canClone = !string.IsNullOrEmpty(p.http_url_to_repo) && !installed;
                using (new EditorGUI.DisabledScope(!canClone))
                {
                    if (canClone)
                    {
                        EditorUtils.IconButton(() => CloneProject(p.http_url_to_repo), AwesomeIcons.Download, Tools.Editor.EditorUtils.IconStyle.Solid, "Клонировать", 16);
                    }
                }
                if (installed)
                {
                    GUILayout.Label("Установлено", EditorStyles.miniLabel, GUILayout.Width(100));
                }
                EditorUtils.IconButton(() =>
                {
                    if (!string.IsNullOrEmpty(p.web_url)) Application.OpenURL(p.web_url);
                }, AwesomeIcons.Link, Tools.Editor.EditorUtils.IconStyle.Solid, "Копировать URL в буфер обмена", 14);
            }
            if (!string.IsNullOrEmpty(p.last_activity_at))
            {
                EditorGUILayout.LabelField("activity: " + p.last_activity_at, EditorStyles.miniLabel);
            }
        }
    }

    private void CloneProject(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var libsRoot = GitPathsUtil.GetExistingLibRoot() ?? GitPathsUtil.GetPreferredLibRoot();
        if (!System.IO.Directory.Exists(libsRoot)) System.IO.Directory.CreateDirectory(libsRoot);

        var folder = GitRequirementsUtil.GuessFolderFromUrl(url);
        var target = System.IO.Path.Combine(libsRoot, folder).Replace("\\", "/");
        if (System.IO.Directory.Exists(target) && System.IO.Directory.GetFileSystemEntries(target).Length > 0)
        {
            EditorUtility.DisplayDialog("Клонирование", "Целевая папка уже существует и не пуста:\n" + target, "OK");
            EditorUtility.RevealInFinder(target);
            return;
        }

        bool success;
        string output;
        if (System.IO.Directory.Exists(target))
        {
            success = GitCommand.Run("clone " + url + " .", target, out output);
        }
        else
        {
            var workDir = System.IO.Directory.GetParent(target)?.FullName.Replace("\\", "/");
            success = GitCommand.Run($"clone {url} \"{target}\"", workDir, out output);
        }

        Debug.Log("[Git] " + output);
        if (success)
        {
            GitRequirementsUtil.AddUrl(url);
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Клонирование", "Репозиторий успешно клонирован:\n" + target, "OK");
        }
        else
        {
            EditorUtility.DisplayDialog("Клонирование", "Ошибка при клонировании. См. Console.", "OK");
        }
    }
}
