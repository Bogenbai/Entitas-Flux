using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Entitas.Graph.Analysis
{
    /// <summary>What a component is as a message, by structure: its kind and who consumes or carries it.</summary>
    sealed class MessageInfo
    {
        public string Kind = string.Empty;

        /// <summary>For a request, the ids of the systems consuming it; for an event, the ids of the markers carrying it.</summary>
        public List<string> By = new List<string>();
    }

    /// <summary>
    /// Requests and events recognised by what happens to their entities, whatever they are
    /// called. A project that names them `*Order` and `*Docked`, or nothing in particular, has
    /// them all the same: an entity made to be acted on once and destroyed, or one that rides on
    /// a generic marker until a sweeper takes it away. Names only say which is which when the
    /// project follows the convention, and then they win (<see cref="GraphNode.MessageRole"/>).
    ///
    /// The facts, all from loops that see every entity of their group whenever their system runs
    /// (a foreach in the system's Execute or Cleanup, not under a condition or in another loop:
    /// TreeWalker.IsSteady), over a group with an AllOf and no AnyOf or XChanged:
    /// - a sweep destroys every entity it visits, and has no NoneOf: whatever holds its AllOf dies;
    /// - an arm sets components on every entity it visits, unconditionally (not `isX = c`), with
    ///   no NoneOf but for what it sets: whatever holds its AllOf gets them (MarkEventsReadySystem
    ///   and Ready; a handler marking its request Destructed).
    /// A set of components is doomed when arming it, as often as arms apply, makes it hold all a
    /// sweep requires. Then:
    /// - consumed: holding the component alone dooms the entity. PlaceFurnitureRequest is, through
    ///   its handler's mark and the Destructed sweep; Event is, through Ready and the event sweep;
    ///   Ready is not — a Ready entity lives on unless it also holds Event.
    /// - a marker is consumed, and is put on entities to doom them or carry them: set on existing
    ///   entities that hold nothing like it (Destructed on furniture), or created with an event
    ///   (Event, Pulse). A marker is plumbing, no message.
    /// - event-like: not consumed itself, created only on entities a consumed component is created
    ///   with (or written on entities already holding it), and collected by a group of some system
    ///   that does not require that carrier — so it is read for itself, not as a qualifier of a
    ///   request (FloatingTextOrigin on a ChangeCurrencyRequest, collected with the request).
    /// - request-like: consumed and no marker; its consumers are the systems whose loops over a
    ///   group requiring it destroy or mark every entity.
    ///
    /// Not a message, on purpose: a component taken off rather than destroyed with its entity
    /// (transient, GraphBuilder.TransientComponents); an entity destroyed only sometimes — under a
    /// condition, after a continue, in a nested loop, behind a guard on anything but its own group
    /// (`if (!_pathfinding.IsInitialized) return;`: requests wait, or hints live until a window
    /// opens; the structure cannot tell which) — or from outside a loop over its group (a
    /// furniture entity sold); a sweep that spares some entities with NoneOf. Nor what a loop
    /// does by passing the entity to a helper (`Expire(offer)`): InferDestroysFromMarking follows
    /// that one level, but whether the helper does it to every entity is not known there. A
    /// request nobody reads is never consumed, so only its name can say what it is.
    /// </summary>
    static class Messages
    {
        public const string Request = "request", Event = "event";

        sealed class Loop
        {
            public TypeInfo System = null!;
            public GroupShape Shape = null!;
            public readonly HashSet<string> Sets = new HashSet<string>(StringComparer.Ordinal);
            public bool Destroys;

            public List<string> Requires => Shape.AllOf;

            /// <summary>
            /// What every entity of the group holds once the loop ran: all it sets, or — when its
            /// NoneOf skips entities holding one of those — only that one, which they hold either way.
            /// </summary>
            public IEnumerable<string> Gives => Shape.NoneOf.Count == 0 ? Sets : Shape.NoneOf.Count == 1 ? Shape.NoneOf : Enumerable.Empty<string>();

            public bool IsSweep => Destroys && Shape.NoneOf.Count == 0;
            public bool IsArm => !Destroys && Sets.Count > 0 && Shape.NoneOf.All(Sets.Contains) && Gives.Any();
        }

        public static Dictionary<string, MessageInfo> Classify(
            BuildContext context, List<Access> accesses, List<DestroySite> destroys, Dictionary<ISymbol, GroupShape> groups)
        {
            var loops = new SortedDictionary<string, Loop>(StringComparer.Ordinal);
            Loop? LoopOf(string? key, ISymbol? group, TypeInfo system)
            {
                if (key == null || group == null || system.Kind != NodeKind.System || !groups.TryGetValue(group, out var shape) ||
                    shape.AllOf.Count == 0 || shape.AnyOf.Count > 0 || shape.Changes.Count > 0)
                    return null;
                if (!loops.TryGetValue(key, out var loop))
                    loops[key] = loop = new Loop { System = system, Shape = shape };
                return loop;
            }

            foreach (var access in accesses)
            {
                if (access.Relation == Relation.Creates && access.Steady && access.EveryEntity && access.Guard == null && !access.MayClear &&
                    IsOwn(access.Component) && LoopOf(access.Loop, access.Group, access.Accessor) is { } loop)
                    loop.Sets.Add(access.Component.Name);
            }
            // EveryEntity already rules out every condition inside the loop, and lets a destroy
            // after `Destroy(); continue;` branches count, whose guards are its condition.
            foreach (var destroy in destroys)
            {
                if (destroy.Steady && destroy.EveryEntity && LoopOf(destroy.Loop, destroy.Group, destroy.Accessor) is { } loop)
                    loop.Destroys = true;
            }

            var sweeps = loops.Values.Where(l => l.IsSweep).ToList();
            var arms = loops.Values.Where(l => l.IsArm).ToList();

            HashSet<string> Closure(IEnumerable<string> start)
            {
                var held = new HashSet<string>(start, StringComparer.Ordinal);
                for (var changed = true; changed;)
                {
                    changed = false;
                    foreach (var arm in arms)
                        if (arm.Requires.All(held.Contains) && !arm.Gives.All(held.Contains))
                        {
                            held.UnionWith(arm.Gives);
                            changed = true;
                        }
                }
                return held;
            }

            // Consumed components and the systems doing it: the sweeps reached that require the
            // component, and the arms on the way that give a sweep what it requires.
            var components = context.Components.Where(IsOwn).ToList();
            var consumedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var component in components)
            {
                var held = Closure(new[] { component.Name });
                var reached = sweeps.Where(s => s.Requires.All(held.Contains)).ToList();
                if (reached.Count == 0)
                    continue;

                var needed = new HashSet<string>(reached.SelectMany(s => s.Requires), StringComparer.Ordinal);
                consumedBy[component.Name] = reached.Where(s => s.Requires.Contains(component.Name))
                    .Concat(arms.Where(a => a.Requires.Contains(component.Name) && a.Requires.All(held.Contains) && a.Gives.Any(needed.Contains)))
                    .Select(l => l.System.Id)
                    .Distinct()
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
            }

            var creates = accesses.Where(a => a.Relation == Relation.Creates && IsOwn(a.Component)).ToList();

            // A marker dooms other entities: set where the entity is neither being created nor
            // known to hold it already.
            var marking = new HashSet<string>(creates
                .Where(a => consumedBy.ContainsKey(a.Component.Name) && a.Site == null &&
                            !(a.Group != null && groups.TryGetValue(a.Group, out var shape) && shape.AllOf.Contains(a.Component.Name)))
                .Select(a => a.Component.Name), StringComparer.Ordinal);

            // What a creation site certainly puts on the entity it creates, with the write: what the
            // factory it starts from adds without a condition, and what the site adds under no
            // condition the write itself is not under.
            var factoryAdds = creates
                .Where(a => a.Method != null && a.Guard == null)
                .GroupBy<Access, IMethodSymbol>(a => a.Method!, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, Access>, IMethodSymbol, HashSet<string>>(
                    g => g.Key, g => g.Select(a => a.Component.Name).ToHashSet(StringComparer.Ordinal), SymbolEqualityComparer.Default);
            var sites = creates.Where(a => a.Site != null).GroupBy(a => a.Site!).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            IEnumerable<string> CarriersAt(Access write)
            {
                var site = sites[write.Site!];
                var with = site.Where(a => a.Component != write.Component && (a.Condition == null || write.Condition != null && a.Condition.IsWithin(write.Condition)))
                    .Select(a => a.Component.Name)
                    .ToList();
                foreach (var factory in site.Select(a => a.RootFactory).OfType<IMethodSymbol>().Distinct<IMethodSymbol>(SymbolEqualityComparer.Default))
                    if (factoryAdds.TryGetValue(factory, out var added))
                        with.AddRange(added);
                return with.Where(c => c != write.Component.Name && consumedBy.ContainsKey(c));
            }

            // Groups collecting each component, by the components they require.
            var collectedBy = accesses
                .Where(a => a.Relation == Relation.Reads && a.MatcherGroup != null && a.Clause is "AllOf" or "AnyOf" or "Matcher" or "Trigger" &&
                            !a.Accessor.Generated && a.Accessor.Kind is NodeKind.System or NodeKind.Type && groups.ContainsKey(a.MatcherGroup))
                .GroupBy(a => a.Component.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(a => groups[a.MatcherGroup!]).Distinct().ToList(), StringComparer.Ordinal);

            var carriedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var group in creates.GroupBy(a => a.Component.Name, StringComparer.Ordinal))
            {
                if (consumedBy.ContainsKey(group.Key) || !collectedBy.TryGetValue(group.Key, out var collecting))
                    continue;

                // Every write either creates it with a carrier, or replaces it where it already is.
                var carriers = new HashSet<string>(StringComparer.Ordinal);
                var carried = true;
                foreach (var write in group)
                {
                    if (write.Site != null)
                    {
                        var found = CarriersAt(write).ToList();
                        carriers.UnionWith(found);
                        carried &= found.Count > 0;
                    }
                    else
                    {
                        carried &= write.Group != null && groups.TryGetValue(write.Group, out var shape) && shape.AllOf.Contains(group.Key);
                    }
                }

                if (carried && carriers.Count > 0 && collecting.Any(shape => !shape.AllOf.Any(carriers.Contains)))
                    carriedBy[group.Key] = carriers.OrderBy(c => c, StringComparer.Ordinal).ToList();
            }

            var carrying = new HashSet<string>(carriedBy.Values.SelectMany(c => c), StringComparer.Ordinal);
            var messages = new Dictionary<string, MessageInfo>(StringComparer.Ordinal);
            foreach (var pair in consumedBy.Where(p => !marking.Contains(p.Key) && !carrying.Contains(p.Key)))
                messages[pair.Key] = new MessageInfo { Kind = Request, By = pair.Value };
            foreach (var pair in carriedBy)
                messages[pair.Key] = new MessageInfo
                {
                    Kind = Event,
                    By = pair.Value.Select(name => context.ComponentByName(name)!.Id).ToList()
                };
            return messages;
        }

        /// <summary>A component of the project's own, not a generated companion (XChanged, a listener).</summary>
        static bool IsOwn(ComponentInfo component) => !component.Generated && component.ChangeOf == null && component.ListenerOf == null;
    }
}
