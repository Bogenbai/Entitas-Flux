using System;
using System.Collections.Generic;
using System.Reflection;

namespace Entitas.VisualDebugging.Unity.Editor
{
    public static class VisualDebuggingEntitasExtension
    {
        static readonly Dictionary<Type, MethodInfo> _getEntitiesMethods = new Dictionary<Type, MethodInfo>();
        static readonly IEntity[] _noEntities = new IEntity[0];

        public static IEntity CreateEntity(this IContext context) =>
            (IEntity)context.GetType().GetMethod("CreateEntity").Invoke(context, null);

        public static IEntity[] GetAllEntities(this IContext context)
        {
            var contextType = context.GetType();
            if (!_getEntitiesMethods.TryGetValue(contextType, out var method))
            {
                method = contextType.GetMethod("GetEntities", Type.EmptyTypes);
                _getEntitiesMethods.Add(contextType, method);
            }

            return method == null
                ? _noEntities
                : (IEntity[])method.Invoke(context, null);
        }
    }
}
