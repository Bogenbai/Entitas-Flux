using Entitas.CodeGeneration.Attributes;

// [Watched] used to be broken in two ways that only show up here:
//  - with the plain (default) entity API nothing ever set the marker;
//  - inside a namespace the generated marker class and the name the data model used
//    for it disagreed, so the output did not compile at all.
[Game, Watched]
public class WatchedFlagComponent : Entitas.IComponent
{
}

[Game, Watched]
public class WatchedValueComponent : Entitas.IComponent
{
    public int value;
}

namespace MyNamespace
{
    [Game, Watched]
    public class WatchedInNamespaceComponent : Entitas.IComponent
    {
        public int value;
    }
}
