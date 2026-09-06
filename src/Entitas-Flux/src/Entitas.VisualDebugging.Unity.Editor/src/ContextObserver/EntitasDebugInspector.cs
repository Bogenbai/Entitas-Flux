using System;
using System.Collections.Generic;
using DesperateDevs.Unity.Editor;
using UnityEditor;
using UnityEngine;

namespace Entitas.VisualDebugging.Unity.Editor
{
    [CustomEditor(typeof(EntitasDebugBehaviour))]
    public class EntitasDebugInspector : UnityEditor.Editor
    {
        const int EntitiesPerPage = 30;
        const double RefreshInterval = 0.5;

        public static void Focus(IEntity entity)
        {
            var behaviour = EntitasDebugBehaviour.instance;
            if (behaviour == null)
                return;

            _entityToFocus = entity;
            Selection.activeGameObject = behaviour.gameObject;
        }

        class ContextState
        {
            public bool unfolded = true;
            public bool groupsUnfolded;
            public bool live = true;
            public double lastRefresh = double.NegativeInfinity;
            public string search = string.Empty;
            public int page;
            public IEntity entityToDestroy;
            public readonly List<IEntity> snapshot = new List<IEntity>();
            public readonly List<IEntity> filtered = new List<IEntity>();
            public readonly List<IEntity> selection = new List<IEntity>();
        }

        static IEntity _entityToFocus;

        static readonly Comparison<IEntity> _byCreationIndex =
            (a, b) => a.creationIndex.CompareTo(b.creationIndex);

        readonly Dictionary<IContext, ContextState> _states = new Dictionary<IContext, ContextState>();
        readonly List<string> _searchTokens = new List<string>();

        GUIStyle _rowStyle;
        GUIStyle _selectedRowStyle;

        GUIStyle rowStyle => _rowStyle ??= new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft };

        GUIStyle selectedRowStyle => _selectedRowStyle ??= new GUIStyle(rowStyle) { fontStyle = FontStyle.Bold };

        public override bool RequiresConstantRepaint() => true;

        public override void OnInspectorGUI()
        {
            var observedContexts = ((EntitasDebugBehaviour)target).observedContexts;
            if (observedContexts.Count == 0)
            {
                EditorGUILayout.HelpBox("No contexts are observed.", MessageType.Info);
                return;
            }

            for (var i = 0; i < observedContexts.Count; i++)
                drawObservedContext(observedContexts[i]);
        }

        void drawObservedContext(ObservedContext observedContext)
        {
            var context = observedContext.context;
            var state = getState(context);

            EditorLayout.BeginVerticalBox();
            {
                state.unfolded = EditorLayout.Foldout(state.unfolded, observedContext.ToString(), EntityDrawer.foldoutStyle);
                if (state.unfolded)
                {
                    ContextDrawer.DrawContextStats(context);
                    ContextDrawer.DrawContextButtons(context, entity => focus(state, context, entity));
                    drawEntities(state, context);
                    drawGroups(state, observedContext.groups);
                }
            }
            EditorLayout.EndVerticalBox();
        }

        void drawGroups(ContextState state, IReadOnlyList<IGroup> groups)
        {
            if (groups.Count == 0)
                return;

            EditorLayout.BeginVerticalBox();
            {
                state.groupsUnfolded = EditorLayout.Foldout(
                    state.groupsUnfolded, $"Groups ({groups.Count})", EntityDrawer.foldoutStyle);

                if (state.groupsUnfolded)
                    ContextDrawer.DrawGroupRows(groups);
            }
            EditorLayout.EndVerticalBox();
        }

        void drawEntities(ContextState state, IContext context)
        {
            refreshSnapshot(state, context, false);
            consumePendingFocus(state, context);
            filter(state);

            var pageCount = Mathf.Max(1, Mathf.CeilToInt(state.filtered.Count / (float)EntitiesPerPage));
            state.page = Mathf.Clamp(state.page, 0, pageCount - 1);
            var first = state.page * EntitiesPerPage;
            var last = Mathf.Min(first + EntitiesPerPage, state.filtered.Count);

            EditorLayout.BeginVerticalBox();
            {
                EditorGUILayout.BeginHorizontal();
                {
                    EditorGUILayout.LabelField($"Entities ({state.filtered.Count})", EditorStyles.boldLabel);
                    state.live = GUILayout.Toggle(state.live, "Live", EditorStyles.miniButtonLeft, GUILayout.Width(44f));
                    if (GUILayout.Button("Refresh", EditorStyles.miniButtonRight, GUILayout.Width(60f)))
                        refreshSnapshot(state, context, true);
                }
                EditorGUILayout.EndHorizontal();

                state.search = EditorLayout.SearchTextField(state.search);

                EditorGUILayout.BeginHorizontal();
                {
                    if (EditorLayout.MiniButtonLeft("Select page"))
                        for (var i = first; i < last; i++)
                            setSelected(state, state.filtered[i], true);

                    if (EditorLayout.MiniButtonRight("Deselect all"))
                        state.selection.Clear();

                    GUILayout.FlexibleSpace();

                    if (pageCount > 1)
                    {
                        if (EditorLayout.MiniButtonLeft("◀"))
                            state.page = Mathf.Max(0, state.page - 1);

                        GUILayout.Label($"{state.page + 1} / {pageCount}", EditorStyles.miniLabel);

                        if (EditorLayout.MiniButtonRight("▶"))
                            state.page = Mathf.Min(pageCount - 1, state.page + 1);
                    }
                }
                EditorGUILayout.EndHorizontal();

                for (var i = first; i < last; i++)
                    drawEntityRow(state, state.filtered[i]);
            }
            EditorLayout.EndVerticalBox();

            if (state.entityToDestroy != null)
            {
                var entity = state.entityToDestroy;
                state.entityToDestroy = null;
                state.selection.Remove(entity);
                state.snapshot.Remove(entity);
                entity.Destroy();
                return;
            }

            drawSelection(state);
        }

        void drawEntityRow(ContextState state, IEntity entity)
        {
            var isSelected = state.selection.Contains(entity);
            var isAlive = entity.isEnabled;

            EditorGUILayout.BeginHorizontal();
            {
                var toggled = GUILayout.Toggle(isSelected, GUIContent.none, GUILayout.Width(16f));
                if (toggled != isSelected)
                    setSelected(state, entity, toggled);

                EditorGUI.BeginDisabledGroup(!isAlive);
                {
                    if (GUILayout.Button(entity.ToString(), isSelected ? selectedRowStyle : rowStyle))
                    {
                        var e = Event.current;
                        if (e.control || e.command || e.shift)
                        {
                            setSelected(state, entity, !isSelected);
                        }
                        else
                        {
                            state.selection.Clear();
                            state.selection.Add(entity);
                        }
                    }

                    var bgColor = GUI.backgroundColor;
                    GUI.backgroundColor = Color.red;
                    if (EditorLayout.MiniButton("-"))
                        state.entityToDestroy = entity;

                    GUI.backgroundColor = bgColor;
                }
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.EndHorizontal();
        }

        void drawSelection(ContextState state)
        {
            for (var i = state.selection.Count - 1; i >= 0; i--)
                if (!state.selection[i].isEnabled)
                    state.selection.RemoveAt(i);

            if (state.selection.Count == 0)
                return;

            EditorGUILayout.Space();

            if (state.selection.Count == 1)
                EntityDrawer.DrawEntity(state.selection[0]);
            else
                EntityDrawer.DrawMultipleEntities(state.selection.ToArray());
        }

        void refreshSnapshot(ContextState state, IContext context, bool force)
        {
            var now = EditorApplication.timeSinceStartup;
            if (!force && (!state.live || now - state.lastRefresh < RefreshInterval))
                return;

            state.lastRefresh = now;
            state.snapshot.Clear();
            state.snapshot.AddRange(context.GetAllEntities());
            state.snapshot.Sort(_byCreationIndex);
        }

        void filter(ContextState state)
        {
            state.filtered.Clear();
            _searchTokens.Clear();

            foreach (var token in state.search.Split(' '))
                if (token.Length != 0)
                    _searchTokens.Add(token);

            for (var i = 0; i < state.snapshot.Count; i++)
                if (matchesSearch(state.snapshot[i].ToString()))
                    state.filtered.Add(state.snapshot[i]);
        }

        bool matchesSearch(string entityName)
        {
            for (var i = 0; i < _searchTokens.Count; i++)
                if (entityName.IndexOf(_searchTokens[i], StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

            return true;
        }

        void consumePendingFocus(ContextState state, IContext context)
        {
            if (_entityToFocus == null || Array.IndexOf(context.GetAllEntities(), _entityToFocus) < 0)
                return;

            var entity = _entityToFocus;
            _entityToFocus = null;
            focus(state, context, entity);
        }

        void focus(ContextState state, IContext context, IEntity entity)
        {
            state.unfolded = true;
            state.search = string.Empty;
            state.selection.Clear();
            state.selection.Add(entity);

            refreshSnapshot(state, context, true);

            var index = state.snapshot.IndexOf(entity);
            if (index >= 0)
                state.page = index / EntitiesPerPage;
        }

        static void setSelected(ContextState state, IEntity entity, bool selected)
        {
            if (selected)
            {
                if (!state.selection.Contains(entity))
                    state.selection.Add(entity);
            }
            else
            {
                state.selection.Remove(entity);
            }
        }

        ContextState getState(IContext context)
        {
            if (!_states.TryGetValue(context, out var state))
            {
                state = new ContextState();
                _states.Add(context, state);
            }

            return state;
        }
    }
}
