using Modbot.TestSupport;

namespace Modbot.Discord.Tests;

/// <summary>One PostgreSQL container for the suite; each test class that needs isolation makes its own database in it.</summary>
[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
