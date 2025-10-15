using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace EasyGit.Editor.View
{
    public class EditorUtils
    {
        public enum IconStyle
        {
            Regular,
            Solid,
            Brand
        }

        public static void DrawSeparator(float thickness = 1f, float padding = 6f, float spaceBeforeAndAfter = 10)
        {
            GUILayout.Space(spaceBeforeAndAfter);
            var rect = EditorGUILayout.GetControlRect(false, thickness + padding);
            rect.height = thickness;
            rect.y += padding * 0.5f;
            rect.xMin = 0;
            rect.xMax += 4; // чуть шире
            var c = EditorGUIUtility.isProSkin ? new Color(0.3f, 0.3f, 0.3f, 1f) : new Color(0.55f, 0.55f, 0.55f, 1f);
            EditorGUI.DrawRect(rect, c);
            GUILayout.Space(spaceBeforeAndAfter);
        }

        public static void Button(string label, Action action, int height = 30, int? width = null, int fontSize = 12)
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                fontSize = fontSize
            };
            var options = new List<GUILayoutOption> {
                GUILayout.Height(height),
            };
            if (width.HasValue) options.Add(GUILayout.Width(width.Value));

            if (GUILayout.Button(label, style, options.ToArray())) action?.Invoke();
        }

        public static void Button(GUIContent content, Action action, int height = 30)
        {
            if (GUILayout.Button(content, GUILayout.Height(height)))
                action?.Invoke();
        }

        public static bool GetMouseButtonDown(int button)
        {
            return Event.current.type == EventType.MouseDown && Event.current.button == button;
        }

        public static bool GetMouseButtonUp(int button)
        {
            return Event.current.type == EventType.MouseUp && Event.current.button == button;
        }

        public static bool GetMouseButtonDrag(int button)
        {
            return Event.current.type == EventType.MouseDrag && Event.current.button == button;
        }

        // Unified IconButton API with style enum and optional tooltip
        public static void IconButton(Action action, string iconText, IconStyle style = IconStyle.Regular, string tooltip = null, int fontSize = 20, params GUILayoutOption[] options)
        {
            GUIStyle s = style switch
            {
                IconStyle.Solid => FontUtils.GetSolidIconsStyle(),
                IconStyle.Brand => FontUtils.GetBrandsIconsStyle(),
                _ => FontUtils.GetRegularIconsStyle(),
            };
            s.fontSize = fontSize;

            if (!string.IsNullOrEmpty(tooltip))
            {
                var content = new GUIContent(iconText, tooltip);
                if (GUILayout.Button(content, s, options)) action?.Invoke();
            }
            else
            {
                if (GUILayout.Button(iconText, s, options)) action?.Invoke();
            }
        }

        // (Legacy overloads removed) — use the enum-based IconButton above.
    }
}