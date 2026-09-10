using System;
using System.Collections.Generic;
using System.Linq;
using Entitas;
using FluentAssertions;
using Xunit;

public class GroupStorageTests
{
    readonly MyTestContext _context = new MyTestContext();
    readonly IMatcher<TestEntity> _matcherA = Matcher<TestEntity>.AllOf(CID.ComponentA);

    TestEntity CreateA()
    {
        var entity = _context.CreateEntity();
        entity.AddComponentA();
        return entity;
    }

    [Fact]
    public void RemovingFromTheMiddleKeepsEveryOtherEntity()
    {
        // The group swaps the last entity into the freed slot; getting that wrong loses
        // or duplicates an entity, and only shows up with three or more of them.
        var group = _context.GetGroup(_matcherA);
        var first = CreateA();
        var middle = CreateA();
        var last = CreateA();

        middle.RemoveComponentA();

        group.count.Should().Be(2);
        group.ContainsEntity(first).Should().BeTrue();
        group.ContainsEntity(middle).Should().BeFalse();
        group.ContainsEntity(last).Should().BeTrue();
        group.GetEntities().Should().BeEquivalentTo(new[] { first, last });
        group.AsEnumerable().Should().BeEquivalentTo(new[] { first, last });

        var enumerated = new List<TestEntity>();
        foreach (var entity in group)
            enumerated.Add(entity);

        enumerated.Should().BeEquivalentTo(new[] { first, last });
    }

    [Fact]
    public void ReusedEntitiesRejoinGroupsCorrectly()
    {
        // Entities are pooled: a destroyed entity comes back as the same object with the
        // same dense index. A stale slot would make the group think it is still a member.
        var group = _context.GetGroup(_matcherA);
        var entities = new[] { CreateA(), CreateA(), CreateA() };
        group.count.Should().Be(3);

        foreach (var entity in entities)
            entity.Destroy();

        group.count.Should().Be(0);
        _context.reusableEntitiesCount.Should().Be(3);

        var reused = new[] { CreateA(), CreateA(), CreateA() };
        group.count.Should().Be(3, "the same entity objects came back and match again");
        foreach (var entity in reused)
            group.ContainsEntity(entity).Should().BeTrue();
    }

    [Fact]
    public void HandlesMoreEntitiesThanTheInitialCapacity()
    {
        // Forces both backing arrays to grow past their initial size.
        var group = _context.GetGroup(_matcherA);
        var entities = Enumerable.Range(0, 300).Select(_ => CreateA()).ToArray();

        group.count.Should().Be(300);
        group.GetEntities().Should().BeEquivalentTo(entities);

        // Remove every other one; the survivors must all still be there.
        for (var i = 0; i < entities.Length; i += 2)
            entities[i].RemoveComponentA();

        group.count.Should().Be(150);
        foreach (var (entity, index) in entities.Select((e, i) => (e, i)))
            group.ContainsEntity(entity).Should().Be(index % 2 != 0);
    }

    [Fact]
    public void EntitiesOfTwoContextsDoNotCollide()
    {
        // Dense indices are handed out per context, so both contexts number their
        // entities from zero. A group must never see the other context's entity as its
        // own member.
        var other = new MyTestContext();
        var group = _context.GetGroup(_matcherA);
        var otherGroup = other.GetGroup(Matcher<TestEntity>.AllOf(CID.ComponentA));

        var mine = CreateA();
        var theirs = other.CreateEntity();
        theirs.AddComponentA();

        group.count.Should().Be(1);
        otherGroup.count.Should().Be(1);
        group.ContainsEntity(mine).Should().BeTrue();
        group.ContainsEntity(theirs).Should().BeFalse();
        otherGroup.ContainsEntity(theirs).Should().BeTrue();
        otherGroup.ContainsEntity(mine).Should().BeFalse();
    }

    [Fact]
    public void DestroyingEverythingReleasesEveryRetain()
    {
        // A group retains each entity it holds and releases it on removal. A missed
        // release surfaces later as ContextStillHasRetainedEntitiesException.
        _context.GetGroup(_matcherA);
        _context.GetGroup(Matcher<TestEntity>.AllOf(CID.ComponentB));

        for (var i = 0; i < 20; i++)
        {
            var entity = CreateA();
            if (i % 3 == 0)
                entity.AddComponentB();
        }

        _context.DestroyAllEntities();

        _context.count.Should().Be(0);
        _context.retainedEntitiesCount.Should().Be(0);
    }

    [Fact]
    public void SingleEntityStillWorksAfterChurn()
    {
        var group = _context.GetGroup(_matcherA);
        var first = CreateA();
        var second = CreateA();

        first.RemoveComponentA();

        group.GetSingleEntity().Should().BeSameAs(second);
    }

    [Fact]
    public void BufferFollowsMembershipChangesAcrossCalls()
    {
        // GetEntities(buffer) copies from a cached view of the storage. The view must be
        // dropped on every add and remove — including the resize of the dense array past
        // its initial 8 slots — or a reused buffer would keep receiving stale members.
        var group = _context.GetGroup(_matcherA);
        var buffer = new List<TestEntity>();
        var entities = new List<TestEntity>();

        for (var i = 0; i < 20; i++)
        {
            entities.Add(CreateA());
            AssertSameMembers(group.GetEntities(buffer), entities);
        }

        var removed = entities[5];
        removed.RemoveComponentA();
        entities.Remove(removed);
        AssertSameMembers(group.GetEntities(buffer), entities);

        entities.Add(CreateA());
        AssertSameMembers(group.GetEntities(buffer), entities);

        _context.DestroyAllEntities();
        group.GetEntities(buffer).Should().BeEmpty();
    }

    // By reference: BeEquivalentTo walks the entities' members, and the generated
    // component accessors throw on an entity that lacks the component.
    static void AssertSameMembers(List<TestEntity> actual, List<TestEntity> expected)
    {
        actual.Count.Should().Be(expected.Count);
        actual.Should().OnlyHaveUniqueItems();
        foreach (var entity in expected)
            actual.Should().Contain(e => ReferenceEquals(e, entity));
    }

    [Fact]
    public void BufferGrowsToTheGroupSizeInOneStep()
    {
        // A small buffer handed a large group should end up sized to the group, not to
        // the next power of two that repeated doubling would have reached.
        var group = _context.GetGroup(_matcherA);
        for (var i = 0; i < 100; i++)
            CreateA();

        var buffer = new List<TestEntity>(4);
        group.GetEntities(buffer);

        buffer.Count.Should().Be(100);
        buffer.Capacity.Should().Be(100);
    }

    [Fact]
    public void FillingTheBufferDoesNotAllocate()
    {
        // The point of the buffered overload. The second fill of an unchanged group must
        // not allocate: the boxed view of the storage is cached alongside the entity
        // array, and the buffer already has the capacity.
        var group = _context.GetGroup(_matcherA);
        for (var i = 0; i < 100; i++)
            CreateA();

        var buffer = new List<TestEntity>();
        group.GetEntities(buffer);

        var before = GC.GetAllocatedBytesForCurrentThread();
        group.GetEntities(buffer);
        GC.GetAllocatedBytesForCurrentThread().Should().Be(before);
    }
}
