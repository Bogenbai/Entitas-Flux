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

        /// <summary>A request its consumers take off a live entity rather than destroy with it.</summary>
        public bool Removed;
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
    /// - request-like by removal: a request put on a live entity (a ship asked to repaint itself)
    ///   is taken off, not destroyed with it. It is consumed by a loop over a group requiring it
    ///   that removes it from every entity it visits, unconditionally, and does something else
    ///   there too — writes another component, or makes or destroys an entity, in the loop or in
    ///   a method it calls — when someone other than that system sets it. The work is what tells
    ///   a handler from a loop that only takes a marker off (ForgetFreshSystem, a JustCreated
    ///   remover); generated [Cleanup] systems never count. What destruction says comes first: a
    ///   component it consumes is a request or plumbing already. The loop may skip entities with
    ///   NoneOf (a suspended cat's leave request waits): unlike a sweep's, its entities live on
    ///   either way, and the request is still the handler's when it gets to it.
    ///
    /// Not a message, on purpose: a component destroyed only by sweeps in the cleanup phase that
    /// do nothing else — it was up the whole frame for whoever reads it, a one-frame signal (a
    /// tick made every frame), not something handed to a handler, so a request swept by a
    /// [Cleanup(DestroyEntity)] system alone needs its name; a component taken off by a loop
    /// that does nothing else (transient, GraphBuilder.TransientComponents); an entity
    /// destroyed, or a request removed,
    /// only sometimes — under a condition, after a continue, in a nested loop, behind a guard on
    /// anything but its own group (`if (!_pathfinding.IsInitialized) return;`: requests wait, or
    /// hints live until a window opens; the structure cannot tell which, so their names have to)
    /// — or from outside a loop over its group (a furniture entity sold); a sweep that spares
    /// some entities with NoneOf. Nor what a loop does by passing the entity to a helper
    /// (`Expire(offer)`): InferDestroysFromMarking follows that one level, but whether the helper
    /// does it to every entity is not known there. A request nobody reads is never consumed, so
    /// only its name can say what it is.
    /// </summary>
    static class Messages
    {
        public const string Request = "request", Event = "event";

        sealed class Loop
        {
            public string Key = null!;
            public TypeInfo System = null!;
            public GroupShape Shape = null!;
            public readonly HashSet<string> Sets = new HashSet<string>(StringComparer.Ordinal);
            public bool Destroys;

            /// <summary>It destroys in the cleanup phase, after every Execute() of the frame.</summary>
            public bool AtFrameEnd;

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
            BuildContext context, List<Access> accesses, List<DestroySite> destroys, List<CallSite> calls, Dictionary<ISymbol, GroupShape> groups)
        {
            var loops = new SortedDictionary<string, Loop>(StringComparer.Ordinal);
            Loop? LoopOf(string? key, ISymbol? group, TypeInfo system)
            {
                if (key == null || group == null || system.Kind != NodeKind.System || !groups.TryGetValue(group, out var shape) ||
                    shape.AllOf.Count == 0 || shape.AnyOf.Count > 0 || shape.Changes.Count > 0)
                    return null;
                if (!loops.TryGetValue(key, out var loop))
                    loops[key] = loop = new Loop { Key = key, System = system, Shape = shape };
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
                {
                    loop.Destroys = true;
                    loop.AtFrameEnd |= destroy.Method != null && context.RunsInCleanup(destroy.Method);
                }
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

            // Swept at the end of the frame and nothing else done there: it was up the whole frame
            // for whoever reads it, a one-frame signal (a tick), not something handed to a handler.
            var busy = new HashSet<string>(accesses.Where(a => a.Relation == Relation.Creates).SelectMany(a => new[] { a.Within, a.Loop })
                .Concat(calls.Select(c => c.Within)).OfType<string>(), StringComparer.Ordinal);
            bool OnlySweptAtFrameEnd(string component, List<string> by) => by.All(id =>
            {
                var own = sweeps.Where(s => s.System.Id == id && s.Requires.Contains(component)).ToList();
                return own.Count > 0 && own.All(s => s.AtFrameEnd && !busy.Contains(s.Key));
            });

            var carrying = new HashSet<string>(carriedBy.Values.SelectMany(c => c), StringComparer.Ordinal);
            var messages = new Dictionary<string, MessageInfo>(StringComparer.Ordinal);
            foreach (var pair in consumedBy.Where(p => !marking.Contains(p.Key) && !carrying.Contains(p.Key) && !OnlySweptAtFrameEnd(p.Key, p.Value)))
                messages[pair.Key] = new MessageInfo { Kind = Request, By = pair.Value };
            foreach (var pair in carriedBy)
                messages[pair.Key] = new MessageInfo
                {
                    Kind = Event,
                    By = pair.Value.Select(name => context.ComponentByName(name)!.Id).ToList()
                };

            // Taken off by its handler: a request, even where its creation looked like an event's.
            foreach (var pair in ConsumedByRemoval(context, accesses, destroys, calls, groups))
                if (!consumedBy.ContainsKey(pair.Key))
                    messages[pair.Key] = new MessageInfo { Kind = Request, By = pair.Value, Removed = true };
            return messages;
        }

        /// <summary>
        /// Requests on live entities, by the handler's loop that takes them off: steady, over a
        /// group requiring the component (no AnyOf or XChanged, whose entities need not hold what
        /// the loop is looking at), removing it from every entity it visits, never setting it
        /// again, and doing some work of its own — while someone else sets it. Returns component
        /// name → those handlers' ids.
        ///
        /// Three shapes that pass all that are no handlers, and are left out:
        /// - a remover in the cleanup phase (<see cref="BuildContext.RunsInCleanup"/>): what it
        ///   takes off was up for the whole frame, for its readers — a one-frame flag (IntervalUp
        ///   on a timer, whose cleanup also rewinds the timer), not a request handed to the remover;
        /// - a "done" flag the group skips with NoneOf is how a loop sees each entity once, no
        ///   work (JustCreated taken off, CreationProcessed set);
        /// - a NoneOf naming what the component's setter requires: the component follows that
        ///   one (an indicator's id attached while the stand is cooking, detached once it stops),
        ///   so it is state, taken off because something else went. Any other NoneOf only makes a
        ///   request wait (a suspended cat leaves once it is resumed).
        /// </summary>
        static Dictionary<string, List<string>> ConsumedByRemoval(
            BuildContext context, List<Access> accesses, List<DestroySite> destroys, List<CallSite> calls, Dictionary<ISymbol, GroupShape> groups)
        {
            var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var removals = accesses
                .Where(a => a.Relation == Relation.Removes && a.Steady && a.EveryEntity && a.Guard == null && !a.MayClear &&
                            a.Accessor.Kind == NodeKind.System && !a.Accessor.Generated && IsOwn(a.Component) && a.Loop != null &&
                            a.Group != null && groups.TryGetValue(a.Group, out var shape) &&
                            shape.AllOf.Contains(a.Component.Name) && shape.AnyOf.Count == 0 && shape.Changes.Count == 0 &&
                            a.Method != null && !context.RunsInCleanup(a.Method))
                .ToList();
            if (removals.Count == 0)
                return found;

            // What each of those loops writes, on any entity: in its body, or on its entities in a
            // helper it passes them to (Access.Loop). The calls in it are followed below, only for
            // loops that write nothing else themselves.
            var loops = new HashSet<string>(removals.Select(r => r.Loop!), StringComparer.Ordinal);
            var written = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var write in accesses.Where(a => a.Relation == Relation.Creates))
                foreach (var loop in new[] { write.Within, write.Loop }.Distinct())
                    if (loop != null && loops.Contains(loop))
                    {
                        if (!written.TryGetValue(loop, out var names))
                            written[loop] = names = new HashSet<string>(StringComparer.Ordinal);
                        names.Add(write.Component.Name);
                    }
            var destroying = new HashSet<string>(destroys.SelectMany(d => new[] { d.Within, d.Loop }).OfType<string>().Where(loops.Contains), StringComparer.Ordinal);
            var calling = calls.Where(c => c.Within != null && loops.Contains(c.Within))
                .GroupBy(c => c.Within!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Callee).ToList(), StringComparer.Ordinal);

            var removed = new HashSet<string>(removals.Select(r => r.Component.Name), StringComparer.Ordinal);
            var setters = accesses.Where(a => a.Relation == Relation.Creates && removed.Contains(a.Component.Name))
                .GroupBy(a => a.Component.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            Dictionary<IMethodSymbol, HashSet<string>>? writesByMethod = null;
            HashSet<IMethodSymbol>? destroyingMethods = null;
            Dictionary<IMethodSymbol, List<IMethodSymbol>>? calleesByMethod = null;

            // Whether a call made in the loop gets, within the depth Propagate follows, to a write
            // of something other than the request itself, or to a Destroy().
            bool CallsWork(IEnumerable<IMethodSymbol> callees, string request)
            {
                writesByMethod ??= accesses.Where(a => a.Relation == Relation.Creates && a.Method != null)
                    .GroupBy<Access, IMethodSymbol>(a => a.Method!, SymbolEqualityComparer.Default)
                    .ToDictionary<IGrouping<IMethodSymbol, Access>, IMethodSymbol, HashSet<string>>(
                        g => g.Key, g => g.Select(a => a.Component.Name).ToHashSet(StringComparer.Ordinal), SymbolEqualityComparer.Default);
                destroyingMethods ??= new HashSet<IMethodSymbol>(destroys.Select(d => d.Method).OfType<IMethodSymbol>(), SymbolEqualityComparer.Default);
                calleesByMethod ??= calls.GroupBy<CallSite, IMethodSymbol>(c => c.Caller, SymbolEqualityComparer.Default)
                    .ToDictionary<IGrouping<IMethodSymbol, CallSite>, IMethodSymbol, List<IMethodSymbol>>(
                        g => g.Key, g => g.Select(c => c.Callee).ToList(), SymbolEqualityComparer.Default);

                var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                var frontier = context.Dispatch(callees).ToList();
                for (var depth = 0; depth < context.Options.MaxCallDepth && frontier.Count > 0; depth++)
                {
                    var next = new List<IMethodSymbol>();
                    foreach (var method in frontier.Where(visited.Add))
                    {
                        if (destroyingMethods.Contains(method) || writesByMethod.TryGetValue(method, out var names) && names.Any(n => n != request))
                            return true;
                        if (calleesByMethod.TryGetValue(method, out var further))
                            next.AddRange(context.Dispatch(further));
                    }
                    frontier = next;
                }
                return false;
            }

            foreach (var removal in removals)
            {
                var name = removal.Component.Name;
                var loop = removal.Loop!;
                var shape = groups[removal.Group!];
                var writes = written.TryGetValue(loop, out var names) ? names : new HashSet<string>(StringComparer.Ordinal);

                // Set again in the same loop, it stays on some entities; set only by the handler, it
                // is the handler's own bookkeeping, not something handed to it.
                if (writes.Contains(name) || !setters.TryGetValue(name, out var sets) || sets.All(s => s.Accessor.Id == removal.Accessor.Id))
                    continue;

                // Put on while the entity holds what this loop skips, taken off once it does not.
                if (sets.Any(s => s.Group != null && groups.TryGetValue(s.Group, out var setter) && setter.AllOf.Any(shape.NoneOf.Contains)))
                    continue;

                // Nothing done but the removal, and the "done" flag the group skips.
                if (!writes.Any(w => !shape.NoneOf.Contains(w)) && !destroying.Contains(loop) &&
                    !(calling.TryGetValue(loop, out var callees) && CallsWork(callees, name)))
                    continue;

                if (!found.TryGetValue(name, out var handlers))
                    found[name] = handlers = new List<string>();
                if (!handlers.Contains(removal.Accessor.Id))
                    handlers.Add(removal.Accessor.Id);
            }

            foreach (var handlers in found.Values)
                handlers.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>A component of the project's own, not a generated companion (XChanged, a listener).</summary>
        static bool IsOwn(ComponentInfo component) => !component.Generated && component.ChangeOf == null && component.ListenerOf == null;
    }
}
