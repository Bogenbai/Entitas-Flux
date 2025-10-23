using UnityEditor;
using UnityEngine;

namespace Entitas.VisualDebugging.Unity.Editor
{
    public static class EditorRectUtilities
    {
        public static Rect SetWidth(this Rect r, float w)
        {
            r.width = w;
            return r;
        }

        public static Rect SetWidthHeight(this Rect r, Vector2 v)
        {
            r.width = v.x;
            r.height = v.y;
            return r;
        }

        public static Rect SetWidthHeight(this Rect r, float w, float h)
        {
            r.width = w;
            r.height = h;
            return r;
        }

        public static Rect AddWidth(this Rect r, float w)
        {
            r.width += w;
            return r;
        }

        public static Rect AddHeight(this Rect r, float h)
        {
            r.height += h;
            return r;
        }

        public static Rect SetHeight(this Rect r, float h)
        {
            r.height = h;
            return r;
        }

        public static Rect AddXY(this Rect r, Vector2 xy)
        {
            r.x += xy.x;
            r.y += xy.y;
            return r;
        }

        public static Rect AddXY(this Rect r, float x, float y)
        {
            r.x += x;
            r.y += y;
            return r;
        }

        public static Rect AddX(this Rect r, float x)
        {
            r.x += x;
            return r;
        }

        public static Rect AddY(this Rect r, float y)
        {
            r.y += y;
            return r;
        }

        public static Rect SetY(this Rect r, float y)
        {
            r.y = y;
            return r;
        }

        public static Rect SetX(this Rect r, float x)
        {
            r.x = x;
            return r;
        }

        public static Rect SetXMin(this Rect r, float x)
        {
            r.xMin = x;
            return r;
        }

        public static Rect SetXMax(this Rect r, float x)
        {
            r.xMax = x;
            return r;
        }

        public static Rect SetYMin(this Rect r, float y)
        {
            r.yMin = y;
            return r;
        }

        public static Rect SetYMax(this Rect r, float y)
        {
            r.yMax = y;
            return r;
        }

        public static Rect AddXMin(this Rect r, float x)
        {
            r.xMin += x;
            return r;
        }

        public static Rect AddXMax(this Rect r, float x)
        {
            r.xMax += x;
            return r;
        }

        public static Rect AddYMin(this Rect r, float y)
        {
            r.yMin += y;
            return r;
        }

        public static Rect AddYMax(this Rect r, float y)
        {
            r.yMax += y;
            return r;
        }

        public static Rect Adjust(this Rect r, float x, float y, float w, float h)
        {
            r.x += x;
            r.y += y;
            r.width += w;
            r.height += h;
            return r;
        }

        public static Rect ToRect(this Vector2 v, float w, float h)
        {
            return new Rect(v.x, v.y, w, h);
        }

        public static Rect ZeroXY(this Rect r)
        {
            return new Rect(0, 0, r.width, r.height);
        }

        public static Vector2 ToVector2(this Rect r)
        {
            return new Vector2(r.width, r.height);
        }

        public static Rect Expand(this Rect rect, float value)
        {
            return new Rect(rect.x - value, rect.y - value, rect.width + value * 2, rect.height + value * 2);
        }

        public static Rect Center(this Rect rect, Vector2 size)
        {
            Rect output = rect;
            output.width = size.x;
            output.height = size.y;

            output.x = rect.width / 2f - output.width / 2f;
            output.y = rect.height / 2f - output.height / 2f;

            return output;
        }

        #if UNITY_EDITOR
        public static Rect AddLine(this Rect r, int count = 1)
        {
            return AddY(r, count * (UnityEditor.EditorGUIUtility.singleLineHeight + UnityEditor.EditorGUIUtility.standardVerticalSpacing));
        }

        public static Rect SetLineHeight(this Rect r)
        {
            return SetHeight(r, UnityEditor.EditorGUIUtility.singleLineHeight);
        }
        #endif

        // Cache 1x1 textures per color to avoid redundant allocations
        private static readonly System.Collections.Generic.Dictionary<Color, Texture2D> _colorTextures = new System.Collections.Generic.Dictionary<Color, Texture2D>();

        public static Texture2D DrawTexture(Color color)
        {
            if (_colorTextures.TryGetValue(color, out var cachedTexture))
            {
                return cachedTexture;
            }

            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            _colorTextures[color] = texture;
            return texture;
        }

        public static GUIContent DrawIcon(string text, string iconId, string toolTip = null)
        {
            var icon = new GUIContent();
            if (string.IsNullOrEmpty(iconId) == false)
            {
                icon = new GUIContent(EditorGUIUtility.IconContent($"{iconId}"));
            }

            return new GUIContent($"{text}", icon.image, $"{toolTip}");
        }
    }
}
