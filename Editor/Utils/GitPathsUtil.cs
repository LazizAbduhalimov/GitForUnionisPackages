using System.IO;
using UnityEditor;
using UnityEngine;

namespace EasyGit
{
    public static class GitPathsUtil
    {
        // Centralized path constants
        public const string ExternalLibRootCorrect = "Assets/External/Lib";
        public const string RequirementsFileName = "required_gits.json";
        public const string GitDirName = ".git";

        public static string GetProjectRoot()
        {
            var assets = Application.dataPath;
            var root = Directory.GetParent(assets)?.FullName;
            return root?.Replace("\\", "/");
        }

        public static string GetExistingLibRoot()
        {
            var proj = GetProjectRoot();
            var abs = Path.Combine(proj, ExternalLibRootCorrect).Replace("\\", "/");
            return Directory.Exists(abs) ? abs : null;
        }

        public static string GetPreferredLibRoot()
        {
            var proj = GetProjectRoot();
            return Path.Combine(proj, ExternalLibRootCorrect).Replace("\\", "/");
        }

        public static string AbsoluteToAssetPathIfPossible(string absolutePath)
        {
            var assets = Application.dataPath.Replace("\\", "/");
            absolutePath = absolutePath.Replace("\\", "/");
            if (!absolutePath.StartsWith(assets)) return null;
            return "Assets" + absolutePath[assets.Length..];
        }

        public static void PingPath(string absDir)
        {
            var rel = AbsoluteToAssetPathIfPossible(absDir);
            if (rel != null)
            {
                var obj = AssetDatabase.LoadAssetAtPath<Object>(rel);
                if (obj != null)
                {
                    EditorUtility.FocusProjectWindow();
                    Selection.activeObject = obj;
                    EditorGUIUtility.PingObject(obj);
                    return;
                }
            }
            EditorUtility.RevealInFinder(absDir);
        }
    }
}
