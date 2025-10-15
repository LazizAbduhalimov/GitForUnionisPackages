using MapTiles.Editor.Git;

public struct DepInfo
{
    public bool exists;
    public bool ok;
    public GitRepoUtil.RepoUpdateState state;
    public string details;
    public string targetPath;
    public string currentBranch;
    public string[] branches;
    public bool hasLocalChanges;
    public string[] remoteBranches;
}