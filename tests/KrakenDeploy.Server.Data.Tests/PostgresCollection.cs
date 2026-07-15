namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Serializes every test class carrying <c>[Collection("Postgres")]</c>. Those
/// classes use <see cref="PostgresFixture"/>, which clones a per-class database
/// from a shared, once-migrated template on a single container
/// (<see cref="SharedPostgres"/>). Running them one class at a time means the
/// per-class <c>CREATE DATABASE ... TEMPLATE</c> / <c>DROP DATABASE</c> never
/// contend, and keeps load on Docker Desktop's daemon/port-proxy low (the churn
/// that previously flaked the suite). Non-container test classes (no
/// <c>[Collection]</c>) still run in parallel.
/// <para>
/// Intentionally no <c>ICollectionFixture</c>: each class keeps its own
/// <c>IClassFixture&lt;PostgresFixture&gt;</c> (its own cloned database); the
/// shared container is a process-lifetime singleton reaped by Testcontainers'
/// Ryuk at exit.
/// </para>
/// </summary>
[CollectionDefinition("Postgres")]
public sealed class PostgresCollectionDefinition;
