namespace Sample
{
    /// <summary>
    /// A root in a file of its own that registers a system FurnitureFeature registers too. Which
    /// registration comes first used to follow whichever file a thread finished first; the
    /// order-independence test needs two of them in different files to see that.
    /// </summary>
    public sealed class DebugFeature : Feature
    {
        public DebugFeature(ISystemFactory systems)
        {
            Add(systems.Create<UnusedSystem>());
        }
    }
}
