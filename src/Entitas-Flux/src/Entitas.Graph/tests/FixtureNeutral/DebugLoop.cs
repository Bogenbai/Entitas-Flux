namespace Harbor
{
    /// <summary>
    /// A root in a file of its own registering a system Berthing registers too: the
    /// order-independence test needs two registrations of one system in different files.
    /// </summary>
    public sealed class DebugLoop : Feature
    {
        public DebugLoop(Contexts contexts)
        {
            Add(new LogOrdersSystem(contexts.port));
        }
    }
}
