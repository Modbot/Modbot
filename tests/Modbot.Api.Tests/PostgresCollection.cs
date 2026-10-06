using Modbot.TestSupport;

// Every test class gets its own database (see OneFixturePerClassAttribute), so classes run side by
// side. This is the only line in the assembly that says so.
[assembly: CollectionBehavior(typeof(OneFixturePerClassCollectionFactory))]

namespace Modbot.Api.Tests;

/// <summary>
/// The database collection. The definition is repeated per test assembly because xUnit resolves
/// collection definitions only within the assembly declaring the tests.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One database per test class.</strong> This collection used to be one container with one
/// database for the whole assembly, so about two thousand database tests ran strictly one after
/// another. Now each class that says <c>[Collection(nameof(PostgresCollection))]</c> gets a
/// database of its own, copied from the migrated template when its first test is about to run and
/// dropped when its last has finished. Different classes run at the same time (xUnit's default is
/// as many at once as the machine has processors), the tests inside one class still run one after
/// another, and every test still starts from what the class's earlier tests left, as it always
/// has -- which is why they reset what they depend on (<c>ApiTestHost.ResetDeploymentAsync</c>)
/// or use ids nobody else uses.
/// </para>
/// <para>
/// What this relies on: nothing outside the database is shared by two test classes. A static field,
/// an environment variable, a fixed port or a fixed file path would be, so none is used (the
/// temporary folders are named with a fresh GUID, the hosts run in memory, every clock is a
/// <c>FakeClock</c> made by the test). A new test must keep to that, or go in a collection of its
/// own with <c>[CollectionDefinition(..., DisableParallelization = true)]</c>.
/// </para>
/// </remarks>
[CollectionDefinition(nameof(PostgresCollection))]
[OneFixturePerClass]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
