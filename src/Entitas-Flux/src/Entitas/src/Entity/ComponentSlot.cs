namespace Entitas
{
    /// <summary>
    /// One component slot on an entity. Exists so the backing store is an array of
    /// structs instead of an array of IComponent: storing a reference into a covariant
    /// reference array costs a runtime type check (the JIT's StelemRef helper) on every
    /// single component add, remove and replace.
    /// </summary>
    struct ComponentSlot
    {
        public IComponent Value;
    }
}
