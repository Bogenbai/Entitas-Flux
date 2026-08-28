using System;
﻿using System.Collections.Generic;

namespace Entitas
{
    /// Use context.GetGroup(matcher) to get a group of entities which match
    /// the specified matcher. Calling context.GetGroup(matcher) with the
    /// same matcher will always return the same instance of the group.
    /// The created group is managed by the context and will always be up to date.
    /// It will automatically add entities that match the matcher or
    /// remove entities as soon as they don't match the matcher anymore.
    public class Group<TEntity> : IGroup<TEntity> where TEntity : class, IEntity
    {
        /// Occurs when an entity gets added.
        public event GroupChanged<TEntity> OnEntityAdded;

        /// Occurs when an entity gets removed.
        public event GroupChanged<TEntity> OnEntityRemoved;

        /// Occurs when a component of an entity in the group gets replaced.
        public event GroupUpdated<TEntity> OnEntityUpdated;

        /// Returns the number of entities in the group.
        public int count => _count;

        /// Returns the matcher which was used to create this group.
        public IMatcher<TEntity> matcher => _matcher;

        readonly IMatcher<TEntity> _matcher;

        // A sparse set instead of a HashSet: membership changes on every component
        // add/remove of every entity the group watches, and hashing an entity, probing
        // buckets and rehashing on removal was ~27% of the time spent changing a
        // component. Here the entity's dense index addresses a flat array directly.
        TEntity[] _dense = new TEntity[8];
        int _count;
        int[] _slots = createSlots(8);

        static int[] createSlots(int size)
        {
            var slots = new int[size];
            for (var i = 0; i < size; i++)
                slots[i] = -1;

            return slots;
        }

        static int denseIndexOf(TEntity entity) => entity is Entity concrete ? concrete.denseIndex : -1;

        void ensureSlots(int denseIndex)
        {
            if (denseIndex < _slots.Length)
                return;

            var size = _slots.Length;
            while (size <= denseIndex)
                size <<= 1;

            var grown = createSlots(size);
            Array.Copy(_slots, grown, _slots.Length);
            _slots = grown;
        }

        bool addToSet(TEntity entity)
        {
            var denseIndex = denseIndexOf(entity);
            if (denseIndex < 0)
                throw new NotSupportedException(
                    $"{entity} does not derive from Entitas.Entity. Groups address entities by their dense index.");

            ensureSlots(denseIndex);
            var occupied = _slots[denseIndex];
            if (occupied >= 0)
            {
                if (ReferenceEquals(_dense[occupied], entity))
                    return false;

                // Dense indices are unique per context, so this means two contexts'
                // entities met inside one group. Entitas never does that — a group
                // belongs to exactly one context — and continuing would silently corrupt
                // membership, so say so instead.
                throw new NotSupportedException(
                    $"{entity} belongs to a different context than the entities already in {this}.");
            }

            if (_count == _dense.Length)
                Array.Resize(ref _dense, _count << 1);

            _dense[_count] = entity;
            _slots[denseIndex] = _count++;
            return true;
        }

        bool removeFromSet(TEntity entity)
        {
            var denseIndex = denseIndexOf(entity);
            if (denseIndex < 0 || denseIndex >= _slots.Length)
                return false;

            var position = _slots[denseIndex];
            // The identity check matters: an entity of another context can share a dense
            // index with one of ours, and must not be mistaken for a member.
            if (position < 0 || !ReferenceEquals(_dense[position], entity))
                return false;

            // Swap the last entity into the freed position; order was never guaranteed.
            var last = --_count;
            var moved = _dense[last];
            _dense[position] = moved;
            _slots[denseIndexOf(moved)] = position;
            _dense[last] = null;
            _slots[denseIndex] = -1;
            return true;
        }

        bool setContains(TEntity entity)
        {
            var denseIndex = denseIndexOf(entity);
            if (denseIndex < 0 || denseIndex >= _slots.Length)
                return false;

            var position = _slots[denseIndex];
            return position >= 0 && ReferenceEquals(_dense[position], entity);
        }

        TEntity[] _entitiesCache;
        TEntity _singleEntityCache;
        string _toStringCache;

        /// Use context.GetGroup(matcher) to get a group of entities which match
        /// the specified matcher.
        public Group(IMatcher<TEntity> matcher)
        {
            _matcher = matcher;
        }

        /// This is used by the context to manage the group.
        public void HandleEntitySilently(TEntity entity)
        {
            if (_matcher.Matches(entity))
                addEntitySilently(entity);
            else
                removeEntitySilently(entity);
        }

        /// This is used by the context to manage the group.
        public void HandleEntity(TEntity entity, int index, IComponent component)
        {
            if (!entity.isEnabled)
            {
                removeEntity(entity, index, component);
                return;
            }

            if (_matcher.Matches(entity))
                addEntity(entity, index, component);
            else
                removeEntity(entity, index, component);
        }

        /// This is used by the context to manage the group.
        public void UpdateEntity(TEntity entity, int index, IComponent previousComponent, IComponent newComponent)
        {
            if (setContains(entity))
            {
                OnEntityRemoved?.Invoke(this, entity, index, previousComponent);
                OnEntityAdded?.Invoke(this, entity, index, newComponent);
                OnEntityUpdated?.Invoke(this, entity, index, previousComponent, newComponent);
            }
        }

        /// Removes all event handlers from this group.
        /// Keep in mind that this will break reactive systems and
        /// entity indices which rely on this group.
        public void RemoveAllEventHandlers()
        {
            OnEntityAdded = null;
            OnEntityRemoved = null;
            OnEntityUpdated = null;
        }

        public GroupChanged<TEntity> HandleEntity(TEntity entity)
        {
            if (!entity.isEnabled)
                return removeEntitySilently(entity) ? OnEntityRemoved : null;

            return _matcher.Matches(entity)
                ? (addEntitySilently(entity) ? OnEntityAdded : null)
                : (removeEntitySilently(entity) ? OnEntityRemoved : null);
        }

        bool addEntitySilently(TEntity entity)
        {
            if (entity.isEnabled)
            {
                var added = addToSet(entity);
                if (added)
                {
                    _entitiesCache = null;
                    _singleEntityCache = null;
                    entity.Retain(this);
                }

                return added;
            }

            return false;
        }

        void addEntity(TEntity entity, int index, IComponent component)
        {
            if (addEntitySilently(entity))
                OnEntityAdded?.Invoke(this, entity, index, component);
        }

        bool removeEntitySilently(TEntity entity)
        {
            var removed = removeFromSet(entity);
            if (removed)
            {
                _entitiesCache = null;
                _singleEntityCache = null;
                entity.Release(this);
            }

            return removed;
        }

        void removeEntity(TEntity entity, int index, IComponent component)
        {
            var removed = removeFromSet(entity);
            if (removed)
            {
                _entitiesCache = null;
                _singleEntityCache = null;
                OnEntityRemoved?.Invoke(this, entity, index, component);
                entity.Release(this);
            }
        }

        /// Determines whether this group has the specified entity.
        public bool ContainsEntity(TEntity entity) => setContains(entity);

        /// Returns all entities which are currently in this group.
        public TEntity[] GetEntities()
        {
            if (_entitiesCache == null)
            {
                _entitiesCache = new TEntity[_count];
                Array.Copy(_dense, _entitiesCache, _count);
            }

            return _entitiesCache;
        }

        /// Fills the buffer with all entities which are currently in this group.
        public List<TEntity> GetEntities(List<TEntity> buffer)
        {
            buffer.Clear();
            for (var i = 0; i < _count; i++)
                buffer.Add(_dense[i]);
            return buffer;
        }

        // Returns the cached snapshot rather than an iterator: a yield-based version
        // allocates an enumerator on every call, and this used to allocate nothing (it
        // handed back the HashSet itself).
        public IEnumerable<TEntity> AsEnumerable() => GetEntities();

        public GroupEnumerator<TEntity> GetEnumerator() => new GroupEnumerator<TEntity>(_dense, _count);

        /// Returns the only entity in this group. It will return null
        /// if the group is empty. It will throw an exception if the group
        /// has more than one entity.
        public TEntity GetSingleEntity()
        {
            if (_singleEntityCache == null)
            {
                var c = _count;
                if (c == 1)
                {
                    _singleEntityCache = _dense[0];
                }
                else if (c == 0)
                {
                    return null;
                }
                else
                {
                    throw new GroupSingleEntityException<TEntity>(this);
                }
            }

            return _singleEntityCache;
        }

        public override string ToString()
        {
            if (_toStringCache == null) 
                _toStringCache = $"Group({_matcher})";

            return _toStringCache;
        }
    }
}
