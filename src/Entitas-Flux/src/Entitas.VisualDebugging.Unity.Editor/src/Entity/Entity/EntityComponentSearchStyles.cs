using UnityEngine;

namespace Entitas.VisualDebugging.Unity.Editor
{
    public static class EntityComponentSearchStyles
    {
        public static readonly GUIStyle ButtonStyle = new()
        {
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(10, 0, 0, 0),
            hover =
            {
                textColor = new Color(0.73f, 0.73f, 0.73f),
                background = EditorRectUtilities.DrawTexture(new Color(0.27f, 0.37f, 0.58f, 0.9f))
            },
            normal =
            {
                textColor = new Color(0.73f, 0.73f, 0.73f)
            }
        };
    }
}
