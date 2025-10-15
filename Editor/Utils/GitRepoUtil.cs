using System;
using System.Linq;

namespace MapTiles.Editor.Git
{
    public static class GitRepoUtil
    {
        public enum RepoUpdateState { UpToDate, Behind, Ahead, Diverged, Unknown }

        public static bool HasLocalChanges(string repoDir)
        {
            if (!GitCommand.Run("status --porcelain", repoDir, out var output)) return true;
            return !string.IsNullOrWhiteSpace(FirstNonEmptyLine(output));
        }

        // Backwards-compatible overload: default to fetching before checking status
        public static RepoUpdateState GetRepoUpdateState(string repoDir, out string details)
        {
            return GetRepoUpdateState(repoDir, out details, fetchBeforeCheck: true);
        }

        // New overload allowing callers to opt-out of a costly fetch
        public static RepoUpdateState GetRepoUpdateState(string repoDir, out string details, bool fetchBeforeCheck)
        {
            details = null;
            if (fetchBeforeCheck)
            {
                GitCommand.Run("fetch --all --prune", repoDir, out _);
            }

            if (!GitCommand.Run("rev-parse HEAD", repoDir, out var headOut)) return RepoUpdateState.Unknown;
            var head = FirstNonEmptyLine(headOut);
            if (string.IsNullOrEmpty(head)) return RepoUpdateState.Unknown;

            string upstreamRef = null;
            if (GitCommand.Run("rev-parse --abbrev-ref --symbolic-full-name @{u}", repoDir, out var upRefOut))
                upstreamRef = FirstNonEmptyLine(upRefOut);
            if (string.IsNullOrEmpty(upstreamRef))
            {
                if (!GitCommand.Run("rev-parse --abbrev-ref HEAD", repoDir, out var brOut)) return RepoUpdateState.Unknown;
                var br = FirstNonEmptyLine(brOut);
                if (string.IsNullOrEmpty(br) || br == "HEAD") return RepoUpdateState.Unknown;
                upstreamRef = "origin/" + br;
            }

            if (!GitCommand.Run($"rev-parse {upstreamRef}", repoDir, out var upstreamOut)) return RepoUpdateState.Unknown;
            var upstream = FirstNonEmptyLine(upstreamOut);
            if (string.IsNullOrEmpty(upstream)) return RepoUpdateState.Unknown;

            if (string.Equals(head, upstream, StringComparison.OrdinalIgnoreCase))
                return RepoUpdateState.UpToDate;

            if (GitCommand.Run($"rev-list --left-right --count HEAD...{upstreamRef}", repoDir, out var countsOut))
            {
                var line = FirstNonEmptyLine(countsOut);
                int a = 0, b = 0;
                if (line != null)
                {
                    var parts = line.Split(new[] {'\t', ' '}, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        int.TryParse(parts[0], out a);
                        int.TryParse(parts[1], out b);
                    }
                }
                details = $"+{a}/-{b}";
                if (a > 0 && b > 0) return RepoUpdateState.Diverged;
                if (b > 0) return RepoUpdateState.Behind;
                if (a > 0) return RepoUpdateState.Ahead;
            }
            return RepoUpdateState.Unknown;
        }

        public static string[] GetLocalBranches(string repoDir, out string currentBranch)
        {
            currentBranch = null;
            if (!GitCommand.Run("symbolic-ref --short HEAD", repoDir, out var curOut))
                GitCommand.Run("rev-parse --abbrev-ref HEAD", repoDir, out curOut);
            currentBranch = FirstNonEmptyLine(curOut);

            if (!GitCommand.Run("for-each-ref --format=\"%(refname:short)\" refs/heads", repoDir, out var listOut))
                return Array.Empty<string>();
            var list = (listOut ?? "")
                .Split('\n')
                .Select(x => x.Trim().Trim('\r').Trim('"'))
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return list;
        }

        public static bool SwitchBranch(string repoDir, string branch)
        {
            if (HasLocalChanges(repoDir)) return false;
            return GitCommand.Run($"checkout \"{branch}\"", repoDir, out _);
        }

        public static string[] GetRemoteBranches(string repoDir)
        {
            // Do not fetch here to avoid UI stalls; caller may fetch explicitly.
            // Gather configured remotes to filter out stale namespaces (e.g., old 'origin' removed)
            var remotes = Array.Empty<string>();
            if (GitCommand.Run("remote", repoDir, out var remOut) && !string.IsNullOrWhiteSpace(remOut))
            {
                remotes = (remOut ?? "")
                    .Split('\n')
                    .Select(x => FirstNonEmptyLine(x)?.Trim())
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
            if (remotes.Length == 0) return Array.Empty<string>();

            if (!GitCommand.Run("for-each-ref --format=\"%(refname:short)\" refs/remotes", repoDir, out var outStr))
                return Array.Empty<string>();

            return (outStr ?? "")
                .Split('\n')
                .Select(x => x.Trim().Trim('\r').Trim('"'))
                .Where(x => !string.IsNullOrEmpty(x) && !x.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
                .Where(x => {
                    var idx = x.IndexOf('/');
                    if (idx <= 0) return false;
                    var remote = x.Substring(0, idx);
                    return remotes.Contains(remote, StringComparer.Ordinal);
                })
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static bool CreateTrackingBranch(string repoDir, string localBranch, string remoteBranch)
        {
            if (HasLocalChanges(repoDir)) return false;
            // Create local branch to track the given remote, then checkout
            if (!GitCommand.Run($"checkout -b \"{localBranch}\" --track \"{remoteBranch}\"", repoDir, out _))
                return false;
            return true;
        }

        public static bool DeleteLocalBranch(string repoDir, string branch, bool force = false)
        {
            // Can't delete current branch
            GetLocalBranches(repoDir, out var current);
            if (string.Equals(current, branch, StringComparison.Ordinal)) return false;
            // Use safe delete by default; force can be enabled if needed
            var opt = force ? "-D" : "-d";
            return GitCommand.Run($"branch {opt} \"{branch}\"", repoDir, out _);
        }

        public static bool DeleteRemoteBranch(string repoDir, string remoteBranch)
        {
            // Expect input like origin/feature-x; extract remote and name
            var parts = (remoteBranch ?? "").Split(new[] {'/'}, 2);
            if (parts.Length != 2) return false;
            var remote = parts[0];
            var name = parts[1];
            if (string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(name)) return false;
            return GitCommand.Run($"push {remote} --delete \"{name}\"", repoDir, out _);
        }

        private static string FirstNonEmptyLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            foreach (var ln in s.Split('\n'))
            {
                var t = ln.Trim();
                if (t.Length > 0) return t;
            }
            return null;
        }
    }
}
