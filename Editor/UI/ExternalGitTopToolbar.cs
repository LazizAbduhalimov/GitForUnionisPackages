using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Tools.Editor;

// Adds a tiny icon button to the main Editor toolbar
[InitializeOnLoad]
public static class ExternalGitTopToolbar
{
    private const string ButtonName = "ExternalGitTopToolbarButton";
    private static bool _attempted;

    static ExternalGitTopToolbar()
    {
        // Repeatedly try until the Toolbar exists, then inject our button
        EditorApplication.update += TryInstall;
    }

    private static void TryInstall()
    {
        if (_attempted) return;

        var toolbarType = typeof(Editor).Assembly.GetType("UnityEditor.Toolbar");
        if (toolbarType == null) return;

        var toolbars = Resources.FindObjectsOfTypeAll(toolbarType);
        if (toolbars == null || toolbars.Length == 0) return;

        // Use the first toolbar instance
        var toolbar = toolbars[0];
        var rootField = toolbarType.GetField("m_Root", BindingFlags.NonPublic | BindingFlags.Instance);
        var root = rootField?.GetValue(toolbar) as VisualElement;
        if (root == null) return;

        // Left-aligned zone (to appear with other tool buttons)
        var leftZone = root.Q("ToolbarZoneLeftAlign") ?? root.Q("ToolbarZoneLeftAlign", "ToolbarZone");
        // Fallbacks (names may vary across versions)
        leftZone ??= root.Q("LeftSide")
                   ?? root.Q("ToolbarZoneRightAlign")
                   ?? root.Q("ToolbarZoneRightAlign", "ToolbarZone");
        if (leftZone == null) return;

        // Avoid duplicates across domain reloads
        if (leftZone.Q(ButtonName) != null)
        {
            _attempted = true;
            EditorApplication.update -= TryInstall;
            return;
        }
        // Use a Button styled like native toolbar buttons
        var button = new Button(() => ExternalGitWindow.ShowWindow())
        {
            name = ButtonName,
            tooltip = "External Libraries",
        };
        // Let toolbar USS style it; keep spacing compact
        button.AddToClassList("unity-toolbar-button");
        button.style.marginLeft = 2;
        button.style.marginRight = 2;
        button.style.paddingLeft = 2;
        button.style.paddingRight = 2;
        button.style.minWidth = 18;
        button.style.minHeight = 18;

        // Draw glyph via IMGUI (lets us render Awesome glyph with FontUtils); Button handles clicks
        var imgui = new IMGUIContainer(() =>
        {
            var fa = FontUtils.LoadBrandsIcons();
            // Match the container size (24x18) so the glyph can be perfectly centered
            var rect = GUILayoutUtility.GetRect(24, 18, GUILayout.Width(24), GUILayout.Height(18));
            if (fa != null)
            {
                var style = new GUIStyle(GUI.skin.label)
                {
                    font = fa,
                    fontSize = 14,
                    alignment = TextAnchor.MiddleCenter,
                };
                style.normal.textColor = EditorGUIUtility.isProSkin
                    ? new Color(0.85f, 0.85f, 0.85f)
                    : new Color(0.15f, 0.15f, 0.15f);

                GUI.Label(rect, AwesomeIcons.Git, style);
            }
        });
    imgui.style.width = 24;
    imgui.style.height = 18;
        imgui.pickingMode = PickingMode.Ignore; // ensure the Button receives clicks
        button.Add(imgui);

        leftZone.Add(button);

        _attempted = true;
        EditorApplication.update -= TryInstall;
    }
}
