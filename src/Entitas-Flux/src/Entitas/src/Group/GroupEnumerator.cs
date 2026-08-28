namespace Entitas
{
    /// <summary>
    /// Allocation-free iteration over the entities in a group. A struct, and returned by
    /// value, so `foreach (var e in group)` binds to it by pattern and never boxes.
    /// </summary>
    public struct GroupEnumerator<TEntity> where TEntity : class, IEntity
    {
        readonly TEntity[] _entities;
        readonly int _count;
        int _index;

        public GroupEnumerator(TEntity[] entities, int count)
        {
            _entities = entities;
            _count = count;
            _index = -1;
        }

        public TEntity Current => _entities[_index];

        public bool MoveNext() => ++_index < _count;
    }
}
