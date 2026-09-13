using Modbot.TestSupport;

namespace Modbot.Evidence.Tests;

/// <summary>
/// One container for the whole assembly. The definition is repeated per test assembly because
/// xUnit resolves collection definitions only within the assembly declaring the tests.
/// </summary>
[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
