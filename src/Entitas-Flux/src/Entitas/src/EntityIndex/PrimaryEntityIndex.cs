using System;
using System.Collections.Generic;

namespace Entitas
{
    public class PrimaryEntityIndex<TEntity, TKey> : AbstractEntityIndex<TEntity, TKey> where TEntity : class, IEntity
    {
        readonly Dictionary<TKey, TEntity> _index;
        readonly Dictionary<TEntity, int> _entityRefCount = new Dictionary<TEntity, int>(EntityEqualityComparer<TEntity>.comparer);

        public PrimaryEntityIndex(string name, IGroup<TEntity> group, Func<TEntity, IComponent, TKey> getKey) : base(name, group, getKey)
        {
            _index = new Dictionary<TKey, TEntity>();
            Activate();
        }

        public PrimaryEntityIndex(string name, IGroup<TEntity> group, Func<TEntity, IComponent, TKey[]> getKeys) : base(name, group, getKeys)
        {
            _index = new Dictionary<TKey, TEntity>();
            Activate();
        }

        public PrimaryEntityIndex(string name, IGroup<TEntity> group, Func<TEntity, IComponent, TKey> getKey, IEqualityComparer<TKey> comparer) : base(name, group, getKey)
        {
            _index = new Dictionary<TKey, TEntity>(comparer);
            Activate();
        }

        public PrimaryEntityIndex(string name, IGroup<TEntity> group, Func<TEntity, IComponent, TKey[]> getKeys, IEqualityComparer<TKey> comparer) : base(name, group, getKeys)
        {
            _index = new Dictionary<TKey, TEntity>(comparer);
            Activate();
        }

        public override void Activate()
        {
            base.Activate();
            indexEntities(_group);
        }

        public TEntity GetEntity(TKey key)
        {
            _index.TryGetValue(key, out var entity);
            return entity;
        }

        public override string ToString() => $"PrimaryEntityIndex({name})";

        protected override void clear()
        {
            foreach (var entity in _entityRefCount.Keys)
                entity.Release(this);

            _entityRefCount.Clear();
            _index.Clear();
        }

        protected override void addEntity(TKey key, TEntity entity)
        {
            if (!_index.TryAdd(key, entity))
                throw new EntityIndexException(
                    $"Entity for key '{key}' already exists!",
                    "Only one entity for a primary key is allowed.");

            if (_entityRefCount.TryGetValue(entity, out var count))
            {
                _entityRefCount[entity] = count + 1;
            }
            else
            {
                _entityRefCount[entity] = 1;
                entity.Retain(this);
            }
        }

        protected override void removeEntity(TKey key, TEntity entity)
        {
            _index.Remove(key);

            var count = _entityRefCount[entity];
            if (count == 1)
            {
                _entityRefCount.Remove(entity);
                entity.Release(this);
            }
            else
            {
                _entityRefCount[entity] = count - 1;
            }
        }
    }
}
