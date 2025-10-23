using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Entitas.VisualDebugging.Unity.Editor
{
	public class EntityComponentSearchWindow : EditorWindow
	{
		private static EntityComponentSearchWindow _popup;

		private string _searchQuery = "";
		private string[] _componentNames;

		private readonly List<string> _componentsByQuery = new();
		private Action<int> _onComponentSelected;

		private Vector2 _scrollPosition = Vector2.zero;

		private const int SearchbarHeight = 25;
		private const int ElementHeight = 25;
		private const int MaxHeight = 275;

		public static void Open(string[] componentNames, Action<int> onComponentSelected)
		{
			if (_popup != null)
			{
				_popup.ForceClose();
			}

			_popup = CreateInstance<EntityComponentSearchWindow>();
			_popup._componentNames = componentNames;
			_popup._onComponentSelected = onComponentSelected;

			_popup.Init();
		}

		private void Init()
		{
			SetSize(new Vector2(250, 275));

			Rect dynamicPosition = position;
			// Position the popup at the mouse cursor. Adjust centering if needed.
			dynamicPosition.x = GUIUtility.GUIToScreenPoint(Event.current.mousePosition).x;
			dynamicPosition.y = GUIUtility.GUIToScreenPoint(Event.current.mousePosition).y;
			position = dynamicPosition;

			UpdateSize();
			ShowPopup();
			Focus();
		}

		private void OnGUI()
		{
			if (_popup == null)
			{
				Close();
				return;
			}

			EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), new Color(0.1f, 0.1f, 0.1f, 0.9f));
			EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height).Expand(-1), new Color(0.19f, 0.19f, 0.19f, 0.9f));

			DrawSearchbar();
			DrawContent();
			Repaint();
		}

		private void DrawSearchbar()
		{
			var rect = new Rect(1, 1, position.width - 2, SearchbarHeight);
			EditorGUI.DrawRect(rect, new Color(0.24f, 0.24f, 0.24f));

			GUI.SetNextControlName("SearchField");
			_searchQuery = GUI.TextField(new Rect(3, 4, position.width - 6, SearchbarHeight), _searchQuery, EditorStyles.toolbarSearchField);
			EditorGUI.FocusTextInControl("SearchField");

			rect.y += rect.height;
			rect.height = 1;
			EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f));
		}

		private List<string> GetComponentsByQuery(string query)
		{
			if (string.IsNullOrWhiteSpace(query))
				return _componentNames.ToList();

			_componentsByQuery.Clear();

			string[] queryParts = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

			foreach (string componentName in _componentNames)
			{
				string nameLower = componentName.ToLower();
				int lastIndex = -1;
				bool match = true;

				foreach (string part in queryParts)
				{
					int index = nameLower.IndexOf(part, lastIndex + 1, StringComparison.Ordinal);
					if (index == -1)
					{
						match = false;
						break;
					}

					lastIndex = index;
				}

				if (match)
				{
					_componentsByQuery.Add(componentName);
				}
			}

			return _componentsByQuery;
		}

		private void DrawContent()
		{
			List<string> components = GetComponentsByQuery(_searchQuery);

			_scrollPosition = GUI.BeginScrollView(
				new Rect(0, SearchbarHeight,
					position.width,
					position.height - SearchbarHeight),
				_scrollPosition,
				new Rect(0, 0, position.width - 20, ElementHeight * components.Count));

			var buttonRect = new Rect(1, 0, position.width - 2, ElementHeight);

			foreach (string componentName in components)
			{
				DrawHorizontalLine(buttonRect);

				if (GUI.Button(buttonRect, new GUIContent($"{componentName}"), EntityComponentSearchStyles.ButtonStyle))
				{
					_onComponentSelected(Array.IndexOf(_componentNames, componentName));
					ForceClose();
				}

				buttonRect.y += 25f;
			}

			GUI.EndScrollView();
		}

		private void UpdateSize()
		{
			Rect dynamicPosition = position;
			dynamicPosition.height = Mathf.Clamp(SearchbarHeight + (ElementHeight * _componentNames.Length), 0, MaxHeight);
			position = dynamicPosition;
		}

		private void SetSize(Vector2 size)
		{
			minSize = size;
			maxSize = size;

			Rect targetPosition = position;
			targetPosition.width = size.x;
			targetPosition.height = size.y;
			position = targetPosition;
		}

		private void OnLostFocus()
		{
			if (_popup != null)
			{
				ForceClose();
			}
		}

		private void ForceClose()
		{
			Close();
			DestroyImmediate(_popup);
			_popup = null;
		}

		private void DrawHorizontalLine(Rect rect)
		{
			rect.y += rect.height - 1;
			rect.height = 1;
			rect.x += 5;
			rect.width -= 10;

			EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f));
			EditorGUI.DrawRect(rect.AddY(1), new Color(0.24f, 0.24f, 0.24f));
		}
	}
}