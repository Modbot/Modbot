using System.Collections.Concurrent;
using Xunit.Sdk;
using Xunit.v3;

namespace Modbot.TestSupport;

/// <summary>
/// Put on a <c>[CollectionDefinition]</c> class: every test class that names the collection gets
/// its own copy of the collection's fixtures, instead of all of them sharing one.
/// </summary>
/// <remarks>
/// Only has an effect in an assembly that also says
/// <c>[assembly: CollectionBehavior(typeof(OneFixturePerClassCollectionFactory))]</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class OneFixturePerClassAttribute : Attribute;

/// <summary>
/// Decides which collection each test class belongs to, the way xUnit does by default, except
/// that a class in a collection marked <see cref="OneFixturePerClassAttribute"/> gets a collection
/// of its own that uses the same definition.
/// </summary>
/// <remarks>
/// <para>
/// xUnit runs the tests inside a collection one after another and different collections at the
/// same time, and creates a collection's fixtures once for each collection it runs. So a class
/// that names a shared collection is kept from running beside the others that name it, whether or
/// not it needs to be, and the database tests of an assembly with a single
/// <c>PostgresCollection</c> run strictly in a line. Giving every class its own collection, with
/// the shared collection's definition and therefore the same fixtures, lets them run side by side,
/// each with a fixture (a database) of its own, without touching the hundreds of
/// <c>[Collection]</c> attributes on the classes.
/// </para>
/// <para>
/// Everything else is left to xUnit: a class with no collection is in a collection of its own as
/// usual, and a collection whose definition is not marked is shared as usual.
/// </para>
/// </remarks>
public sealed class OneFixturePerClassCollectionFactory : IXunitTestCollectionFactory
{
    private readonly IXunitTestAssembly _testAssembly;
    private readonly CollectionPerClassTestCollectionFactory _standard;
    private readonly ConcurrentDictionary<Type, IXunitTestCollection> _own = new();

    public OneFixturePerClassCollectionFactory(IXunitTestAssembly testAssembly)
    {
        ArgumentNullException.ThrowIfNull(testAssembly);

        _testAssembly = testAssembly;
        _standard = new CollectionPerClassTestCollectionFactory(testAssembly);
    }

    public string DisplayName => "collection-per-class, with a fixture per class where the collection asks for it";

    public IXunitTestCollection Get(Type testClass)
    {
        ArgumentNullException.ThrowIfNull(testClass);

        var shared = _standard.Get(testClass);
        var definition = shared.CollectionDefinition;

        if (definition is null || !definition.IsDefined(typeof(OneFixturePerClassAttribute), inherit: false))
            return shared;

        // The collection's identity comes from its display name, so a name with the class in it is
        // a collection of its own; the definition, and so the fixtures and the settings, are the
        // shared collection's.
        return _own.GetOrAdd(testClass, type => new XunitTestCollection(
            _testAssembly,
            definition,
            shared.DisableParallelization,
            $"{shared.TestCollectionDisplayName} ({type.FullName})"));
    }
}
