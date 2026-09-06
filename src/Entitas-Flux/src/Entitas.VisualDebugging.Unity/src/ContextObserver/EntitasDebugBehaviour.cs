using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Entitas.VisualDebugging.Unity
{
    [ExecuteInEditMode]
    public class EntitasDebugBehaviour : MonoBehaviour
    {
        public const string GameObjectName = "Entitas Debug";

        public static EntitasDebugBehaviour instance => _instance;

        static EntitasDebugBehaviour _instance;

        public IReadOnlyList<ObservedContext> observedContexts => _observedContexts;

        readonly List<ObservedContext> _observedContexts = new List<ObservedContext>();
        readonly StringBuilder _nameBuilder = new StringBuilder();
        string _cachedName;

        public static ObservedContext Observe(IContext context)
        {
            if (_instance == null)
            {
                var go = new GameObject(GameObjectName);
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<EntitasDebugBehaviour>();
            }

            var observedContext = new ObservedContext(context);
            _instance._observedContexts.Add(observedContext);
            _instance.Update();
            return observedContext;
        }

        public ObservedContext Find(IContext context)
        {
            for (var i = 0; i < _observedContexts.Count; i++)
                if (_observedContexts[i].context == context)
                    return _observedContexts[i];

            return null;
        }

        void Awake()
        {
            if (_instance == null)
                _instance = this;
        }

        void Update()
        {
            _nameBuilder.Length = 0;
            _nameBuilder.Append(GameObjectName).Append(" (");

            var entities = 0;
            for (var i = 0; i < _observedContexts.Count; i++)
                entities += _observedContexts[i].context.count;

            _nameBuilder
                .Append(_observedContexts.Count).Append(" contexts, ")
                .Append(entities).Append(" entities)");

            var newName = _nameBuilder.ToString();
            if (_cachedName != newName)
                name = _cachedName = newName;
        }

        void OnDestroy()
        {
            for (var i = 0; i < _observedContexts.Count; i++)
                _observedContexts[i].Deactivate();

            _observedContexts.Clear();

            if (_instance == this)
                _instance = null;
        }
    }
}
