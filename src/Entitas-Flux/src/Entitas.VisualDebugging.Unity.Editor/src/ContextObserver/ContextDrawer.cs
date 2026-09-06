using System;
using System.Collections.Generic;
using System.Linq;
using DesperateDevs.Unity.Editor;
using UnityEditor;
using UnityEngine;

namespace Entitas.VisualDebugging.Unity.Editor
{
    public static class ContextDrawer
    {
        public static void DrawContextStats(IContext context)
        {
            EditorGUILayout.LabelField("Entities", context.count.ToString());
            EditorGUILayout.LabelField("Reusable entities", context.reusableEntitiesCount.ToString());

            var retainedEntitiesCount = context.retainedEntitiesCount;
            if (retainedEntitiesCount != 0)
            {
                var c = GUI.color;
                GUI.color = Color.red;
                EditorGUILayout.LabelField("Retained entities", retainedEntitiesCount.ToString());
                GUI.color = c;
                EditorGUILayout.HelpBox("WARNING: There are retained entities.\nDid you call entity.Retain(owner) and forgot to call entity.Release(owner)?", MessageType.Warning);
            }
        }

        public static void DrawContextButtons(IContext context, Action<IEntity> onEntityCreated)
        {
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("Create Entity"))
                    onEntityCreated(context.CreateEntity());

                var bgColor = GUI.backgroundColor;
                GUI.backgroundColor = Color.red;
                if (GUILayout.Button("Destroy All Entities"))
                    context.DestroyAllEntities();

                GUI.backgroundColor = bgColor;
            }
            EditorGUILayout.EndHorizontal();
        }

        public static void DrawGroups(IReadOnlyList<IGroup> groups)
        {
            if (groups.Count == 0)
                return;

            EditorLayout.BeginVerticalBox();
            {
                EditorGUILayout.LabelField($"Groups ({groups.Count})", EditorStyles.boldLabel);
                DrawGroupRows(groups);
            }
            EditorLayout.EndVerticalBox();
        }

        public static void DrawGroupRows(IReadOnlyList<IGroup> groups)
        {
            foreach (var group in groups.OrderByDescending(g => g.count))
            {
                EditorGUILayout.BeginHorizontal();
                {
                    EditorGUILayout.LabelField(group.ToString());
                    EditorGUILayout.LabelField(group.count.ToString(), GUILayout.Width(48));
                }
                EditorGUILayout.EndHorizontal();
            }
        }
    }
}
