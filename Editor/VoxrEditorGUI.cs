// ============================================================================
// Purpose:  Shared IMGUI drawing helpers used by editor windows
// Layer:    Editor
// Owns:     VoxrEditorGUI (internal static class)
// Depends:  (none)
// ============================================================================
using UnityEditor;
using UnityEngine;

namespace VoXR.Editor
{
    internal static class VoxrEditorGUI
    {
        internal static void DrawSectionHeader(string title)
        {
            EditorGUILayout.Space(2);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
        }

        internal static void DrawHorizontalSeparator()
        {
            EditorGUILayout.Space(2);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));
            EditorGUILayout.Space(2);
        }
    }
}
