using System.Linq;
using DesperateDevs.Unity.Editor;
using UnityEditor;
using UnityEngine;

namespace Entitas.VisualDebugging.Unity.Editor
{
    [CustomEditor(typeof(ContextObserverBehaviour))]
    public class ContextObserverInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var contextObserver = ((ContextObserverBehaviour)target).contextObserver;

            EditorLayout.BeginVerticalBox();
            {
                EditorGUILayout.LabelField(contextObserver.context.contextInfo.name, EditorStyles.boldLabel);
                ContextDrawer.DrawContextStats(contextObserver.context);
                ContextDrawer.DrawContextButtons(contextObserver.context, entity =>
                {
                    var entityBehaviour = Object.FindObjectsByType<EntityBehaviour>(FindObjectsSortMode.None)
                        .Single(eb => eb.entity == entity);

                    Selection.activeGameObject = entityBehaviour.gameObject;
                });
            }
            EditorLayout.EndVerticalBox();

            ContextDrawer.DrawGroups(contextObserver.groups);

            EditorUtility.SetDirty(target);
        }
    }
}
