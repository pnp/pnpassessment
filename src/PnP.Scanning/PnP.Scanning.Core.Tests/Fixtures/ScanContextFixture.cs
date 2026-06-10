using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Tests.Fixtures
{
    /// <summary>
    /// Test infrastructure that hosts a <see cref="ScanContext"/> on an in-memory SQLite
    /// database. The underlying connection is opened once and kept open for the lifetime of
    /// the fixture: a SQLite <c>:memory:</c> database only exists while at least one connection
    /// to it is open, so closing it would discard the schema and data. Every context handed out
    /// via <see cref="CreateContext"/> shares that single connection and therefore the same
    /// database, while still being an independent (disposable) unit of work.
    /// </summary>
    public sealed class ScanContextFixture : IDisposable
    {
        private readonly SqliteConnection connection;

        public ScanContextFixture()
        {
            connection = new SqliteConnection("Filename=:memory:");
            connection.Open();

            // Apply the product's EF migrations once to materialize the schema on the shared
            // connection. Proves the real migration path works against SQLite end-to-end.
            using var context = CreateContext();
            context.Database.Migrate();
        }

        /// <summary>
        /// Creates a fresh <see cref="ScanContext"/> bound to the shared in-memory connection.
        /// The product's migrations assembly is supplied explicitly because the externally
        /// configured options bypass <c>ScanContext.OnConfiguring</c>.
        /// </summary>
        internal ScanContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ScanContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ScanContext).Assembly.FullName))
                .Options;

            return new ScanContext(options);
        }

        public void Dispose()
        {
            connection.Dispose();
        }
    }
}
