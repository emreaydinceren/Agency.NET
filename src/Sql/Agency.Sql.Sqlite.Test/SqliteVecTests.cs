using Microsoft.Data.Sqlite;

namespace Agency.Sql.Sqlite.Test;

/// <summary>
/// Integration tests for vector operations against an in-memory SQLite database.
/// Vector distance functions (L2, cosine, dot product) are implemented as SQLite UDFs
/// registered via <see cref="SqliteConnection.CreateFunction{T1,T2,TResult}(string,Func{T1,T2,TResult},bool)"/> — no native extension required.
///
/// Test vectors are 3-dimensional unit vectors so expected distances are
/// analytically exact and assertions can be made with tight tolerances.
///
///   "apple"   → [1, 0, 0]
///   "banana"  → [0, 1, 0]
///   "cherry"  → [0, 0, 1]
///   "apricot" → [0.707, 0.707, 0]   ≈ normalised midpoint of apple + banana
///
/// Vectors are stored as TEXT in JSON-array format "[f1,f2,f3]".
/// </summary>
public sealed class SqliteVecTests : IClassFixture<SqliteVecTests.VectorFixture>
{
    private const double Tolerance = 1e-4;

    private readonly VectorFixture _fx;

    /// <summary>
    /// Creates the test class with its shared vector fixture.
    /// </summary>
    public SqliteVecTests(VectorFixture fx)
    {
        this._fx = fx;
    }

    // ── UDF availability ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the vector distance UDFs are registered and callable.
    /// </summary>
    [Fact]
    public async Task VectorUdfs_AreRegisteredAndCallable()
    {
        var ds = await this._fx.Runner.QueryAsync("""
            SELECT vec_distance_L2('[1,0,0]', '[1,0,0]') AS dist
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(ds.Rows);
        Assert.Equal(0.0, Convert.ToDouble(ds["dist", 0]), Tolerance);
    }

    // ── DDL ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the vector table is created and visible in sqlite_master.
    /// </summary>
    [Fact]
    public async Task VectorTable_IsCreatedAndVisibleInSchema()
    {
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = '{this._fx.Table}'
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(ds.Rows);
        Assert.Equal(this._fx.Table, ds["name", 0]);
    }

    // ── Round-trip ───────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that vector values round-trip through text serialization.
    /// </summary>
    [Fact]
    public async Task VectorColumn_RoundTrip_PreservesComponents()
    {
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT embedding
            FROM {this._fx.Table}
            WHERE name = 'apple'
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(ds.Rows);

        string raw = (string)ds["embedding", 0]!;
        float[] components = ParseVector(raw);

        Assert.Equal(3, components.Length);
        Assert.Equal(1f, components[0], precision: 4);
        Assert.Equal(0f, components[1], precision: 4);
        Assert.Equal(0f, components[2], precision: 4);
    }

    // ── L2 distance ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the same vector has zero L2 distance.
    /// </summary>
    [Fact]
    public async Task L2Distance_SameVector_IsZero()
    {
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT vec_distance_L2(embedding, '[1,0,0]') AS dist
            FROM {this._fx.Table}
            WHERE name = 'apple'
            """, cancellationToken: TestContext.Current.CancellationToken);

        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(0.0, dist, Tolerance);
    }

    /// <summary>
    /// Verifies that orthogonal unit vectors have L2 distance of square root two.
    /// </summary>
    [Fact]
    public async Task L2Distance_OrthogonalUnitVectors_IsSqrtTwo()
    {
        // ||[1,0,0] - [0,1,0]||₂  =  √(1+1+0)  =  √2
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT vec_distance_L2(embedding, '[1,0,0]') AS dist
            FROM {this._fx.Table}
            WHERE name = 'banana'
            """, cancellationToken: TestContext.Current.CancellationToken);

        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(Math.Sqrt(2), dist, Tolerance);
    }

    // ── Cosine distance ──────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that cosine distance for the same vector is zero.
    /// </summary>
    [Fact]
    public async Task CosineDistance_SameVector_IsZero()
    {
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT vec_distance_cosine(embedding, '[1,0,0]') AS dist
            FROM {this._fx.Table}
            WHERE name = 'apple'
            """, cancellationToken: TestContext.Current.CancellationToken);

        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(0.0, dist, Tolerance);
    }

    /// <summary>
    /// Verifies that orthogonal vectors have cosine distance of one.
    /// </summary>
    [Fact]
    public async Task CosineDistance_OrthogonalVectors_IsOne()
    {
        // cos([1,0,0], [0,1,0]) = 0  →  cosine distance = 1 - 0 = 1
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT vec_distance_cosine(embedding, '[1,0,0]') AS dist
            FROM {this._fx.Table}
            WHERE name = 'banana'
            """, cancellationToken: TestContext.Current.CancellationToken);

        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(1.0, dist, Tolerance);
    }

    /// <summary>
    /// Verifies the expected cosine distance for the diagonal vector.
    /// </summary>
    [Fact]
    public async Task CosineDistance_DiagonalVector_IsExpectedValue()
    {
        // apricot ≈ [0.707, 0.707, 0]
        // cos([1,0,0], [0.707,0.707,0]) = 0.707  →  distance = 1 - 0.707 ≈ 0.293
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT vec_distance_cosine(embedding, '[1,0,0]') AS dist
            FROM {this._fx.Table}
            WHERE name = 'apricot'
            """, cancellationToken: TestContext.Current.CancellationToken);

        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(1.0 - (1.0 / Math.Sqrt(2)), dist, Tolerance);
    }

    // ── Dot product ──────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the dot product of a unit vector with itself is 1.
    /// Note: unlike pgvector's &lt;#&gt; operator (which returns the negative inner product),
    /// vec_dot_product returns the real dot product value.
    /// </summary>
    [Fact]
    public async Task DotProduct_SameUnitVector_IsOne()
    {
        // [1,0,0] · [1,0,0] = 1
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT vec_dot_product(embedding, '[1,0,0]') AS dot
            FROM {this._fx.Table}
            WHERE name = 'apple'
            """, cancellationToken: TestContext.Current.CancellationToken);

        double dot = Convert.ToDouble(ds["dot", 0]);
        Assert.Equal(1.0, dot, Tolerance);
    }

    /// <summary>
    /// Verifies that orthogonal vectors have a zero dot product.
    /// </summary>
    [Fact]
    public async Task DotProduct_OrthogonalVectors_IsZero()
    {
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT vec_dot_product(embedding, '[1,0,0]') AS dot
            FROM {this._fx.Table}
            WHERE name = 'banana'
            """, cancellationToken: TestContext.Current.CancellationToken);

        double dot = Convert.ToDouble(ds["dot", 0]);
        Assert.Equal(0.0, dot, Tolerance);
    }

    // ── Nearest-neighbour ORDER BY ────────────────────────────────────────────

    /// <summary>
    /// Verifies that a nearest-neighbour query returns the exact match.
    /// </summary>
    [Fact]
    public async Task NearestNeighbour_CosineTopOne_ReturnsExactMatch()
    {
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT name
            FROM {this._fx.Table}
            ORDER BY vec_distance_cosine(embedding, '[1,0,0]')
            LIMIT 1
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(ds.Rows);
        Assert.Equal("apple", ds["name", 0]);
    }

    /// <summary>
    /// Verifies that the two nearest neighbours are returned in the expected order.
    /// </summary>
    [Fact]
    public async Task NearestNeighbour_L2TopTwo_ReturnsAppleAndApricot()
    {
        // apple  distance = 0
        // apricot distance ≈ 0.765  (< √2 ≈ 1.414 for banana/cherry)
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT name, vec_distance_L2(embedding, '[1,0,0]') AS dist
            FROM {this._fx.Table}
            ORDER BY dist
            LIMIT 2
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, ds.Rows.Count);
        Assert.Equal("apple", ds["name", 0]);
        Assert.Equal("apricot", ds["name", 1]);
    }

    /// <summary>
    /// Verifies that a distance threshold excludes distant rows.
    /// </summary>
    [Fact]
    public async Task NearestNeighbour_WithDistanceThreshold_ExcludesDistantRows()
    {
        // Only apple (dist 0) and apricot (dist ~0.765) are within 0.9 of [1,0,0]
        var ds = await this._fx.Runner.QueryAsync($"""
            SELECT name
            FROM {this._fx.Table}
            WHERE vec_distance_L2(embedding, '[1,0,0]') < 0.9
            ORDER BY vec_distance_L2(embedding, '[1,0,0]')
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, ds.Rows.Count);
        Assert.Equal("apple", ds["name", 0]);
        Assert.Equal("apricot", ds["name", 1]);
    }

    // ── Parameterised ExecuteAsync ────────────────────────────────────────────

    /// <summary>
    /// Verifies that parameterized inserts add a new row.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithParameters_InsertsNewRow()
    {
        try
        {
            int affected = await this._fx.Runner.ExecuteAsync(
                $"INSERT INTO {this._fx.Table} (name, embedding) VALUES (@name, @vec)",
                new Dictionary<string, object?> { ["name"] = "mango", ["vec"] = "[0,0,1]" },
                TestContext.Current.CancellationToken);

            Assert.Equal(1, affected);

            var ds = await this._fx.Runner.QueryAsync(
                $"SELECT name FROM {this._fx.Table} WHERE name = @name",
                new Dictionary<string, object?> { ["name"] = "mango" },
                TestContext.Current.CancellationToken);

            Assert.Single(ds.Rows);
            Assert.Equal("mango", ds["name", 0]);
        }
        finally
        {
            await this._fx.Runner.ExecuteAsync(
                $"DELETE FROM {this._fx.Table} WHERE name = @name",
                new Dictionary<string, object?> { ["name"] = "mango" },
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Verifies that parameterized updates modify the stored embedding.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithParameters_UpdatesEmbedding()
    {
        await this._fx.Runner.ExecuteAsync(
            $"INSERT INTO {this._fx.Table} (name, embedding) VALUES (@name, @vec)",
            new Dictionary<string, object?> { ["name"] = "temp_update", ["vec"] = "[0,1,0]" },
            TestContext.Current.CancellationToken);

        try
        {
            int affected = await this._fx.Runner.ExecuteAsync(
                $"UPDATE {this._fx.Table} SET embedding = @vec WHERE name = @name",
                new Dictionary<string, object?> { ["name"] = "temp_update", ["vec"] = "[1,0,0]" },
                TestContext.Current.CancellationToken);

            Assert.Equal(1, affected);

            var ds = await this._fx.Runner.QueryAsync(
                $"SELECT embedding FROM {this._fx.Table} WHERE name = @name",
                new Dictionary<string, object?> { ["name"] = "temp_update" },
                TestContext.Current.CancellationToken);

            float[] components = ParseVector((string)ds["embedding", 0]!);
            Assert.Equal(1f, components[0], precision: 4);
            Assert.Equal(0f, components[1], precision: 4);
            Assert.Equal(0f, components[2], precision: 4);
        }
        finally
        {
            await this._fx.Runner.ExecuteAsync(
                $"DELETE FROM {this._fx.Table} WHERE name = @name",
                new Dictionary<string, object?> { ["name"] = "temp_update" },
                TestContext.Current.CancellationToken);
        }
    }

    // ── Parameterised QueryAsync ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that a named parameter returns the expected vector.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WithNameParameter_ReturnsMatchingVector()
    {
        var ds = await this._fx.Runner.QueryAsync(
            $"SELECT embedding FROM {this._fx.Table} WHERE name = @name",
            new Dictionary<string, object?> { ["name"] = "cherry" },
            TestContext.Current.CancellationToken);

        Assert.Single(ds.Rows);
        float[] components = ParseVector((string)ds["embedding", 0]!);
        Assert.Equal(0f, components[0], precision: 4);
        Assert.Equal(0f, components[1], precision: 4);
        Assert.Equal(1f, components[2], precision: 4);
    }

    /// <summary>
    /// Verifies that a vector parameter computes the expected L2 distance.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WithVectorParameter_ComputesL2Distance()
    {
        // Query vector matches banana exactly → distance should be 0.
        var ds = await this._fx.Runner.QueryAsync(
            $"SELECT vec_distance_L2(embedding, @vec) AS dist FROM {this._fx.Table} WHERE name = @name",
            new Dictionary<string, object?> { ["name"] = "banana", ["vec"] = "[0,1,0]" },
            TestContext.Current.CancellationToken);

        Assert.Single(ds.Rows);
        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(0.0, dist, Tolerance);
    }

    /// <summary>
    /// Verifies that a distance threshold parameter filters rows correctly.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WithDistanceThresholdParameter_FiltersRows()
    {
        // Only apple (dist 0) and apricot (dist ~0.765) are within threshold 0.9 of [1,0,0].
        var ds = await this._fx.Runner.QueryAsync(
            $"SELECT name FROM {this._fx.Table} WHERE vec_distance_L2(embedding, '[1,0,0]') < @threshold ORDER BY vec_distance_L2(embedding, '[1,0,0]')",
            new Dictionary<string, object?> { ["threshold"] = 0.9 },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, ds.Rows.Count);
        Assert.Equal("apple", ds["name", 0]);
        Assert.Equal("apricot", ds["name", 1]);
    }

    /// <summary>
    /// Verifies that vector and limit parameters return the expected nearest neighbours.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WithVectorAndLimitParameters_ReturnsTopKNeighbours()
    {
        var ds = await this._fx.Runner.QueryAsync(
            $"SELECT name, vec_distance_L2(embedding, @vec) AS dist FROM {this._fx.Table} ORDER BY dist LIMIT @k",
            new Dictionary<string, object?> { ["vec"] = "[1,0,0]", ["k"] = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, ds.Rows.Count);
        Assert.Equal("apple", ds["name", 0]);
        Assert.Equal("apricot", ds["name", 1]);
    }

    // ── Index ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a B-tree index can be created on the name column and used for queries.
    /// SQLite does not support approximate-nearest-neighbour indexes natively; this test
    /// verifies general index creation and correctness in the presence of an index.
    /// </summary>
    [Fact]
    public async Task Index_OnNameColumn_CanBeCreatedAndQueried()
    {
        var indexName = this._fx.UniqueName("name_idx");
        var indexTable = this._fx.UniqueName("idx_docs");

        await this._fx.Runner.ExecuteAsync($"""
            CREATE TABLE {indexTable} (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                name      TEXT NOT NULL,
                embedding TEXT NOT NULL
            )
            """, cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            await this._fx.Runner.ExecuteAsync($"""
                INSERT INTO {indexTable} (name, embedding)
                VALUES ('a', '[1,0,0]'), ('b', '[0,1,0]'), ('c', '[0,0,1]')
                """, cancellationToken: TestContext.Current.CancellationToken);

            await this._fx.Runner.ExecuteAsync($"""
                CREATE INDEX {indexName} ON {indexTable} (name)
                """, cancellationToken: TestContext.Current.CancellationToken);

            // Verify index was created
            var idxDs = await this._fx.Runner.QueryAsync($"""
                SELECT name FROM sqlite_master
                WHERE type = 'index' AND name = '{indexName}'
                """, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Single(idxDs.Rows);

            // Nearest-neighbour query should still return correct result
            var ds = await this._fx.Runner.QueryAsync($"""
                SELECT name
                FROM {indexTable}
                ORDER BY vec_distance_cosine(embedding, '[1,0,0]')
                LIMIT 1
                """, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Single(ds.Rows);
            Assert.Equal("a", ds["name", 0]);
        }
        finally
        {
            await this._fx.Runner.ExecuteAsync($"DROP TABLE IF EXISTS {indexTable}", cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Parses a vector text literal like "[1,0,0]" into a float array.</summary>
    private static float[] ParseVector(string raw)
        => raw.Trim('[', ']')
              .Split(',')
              .Select(float.Parse)
              .ToArray();

    // ── UDF registration ──────────────────────────────────────────────────────

    /// <summary>
    /// Registers vector distance scalar functions on a <see cref="SqliteConnection"/>.
    /// Vectors are expected as TEXT in JSON-array format, e.g. "[1,0,0]".
    /// </summary>
    internal static void RegisterVectorFunctions(SqliteConnection connection)
    {
        connection.CreateFunction("vec_distance_L2", (string v1, string v2) =>
        {
            float[] a = ParseVector(v1);
            float[] b = ParseVector(v2);
            return Math.Sqrt(a.Zip(b).Sum(p => Math.Pow(p.First - p.Second, 2)));
        });

        connection.CreateFunction("vec_distance_cosine", (string v1, string v2) =>
        {
            float[] a = ParseVector(v1);
            float[] b = ParseVector(v2);
            double dot = a.Zip(b).Sum(p => (double)p.First * p.Second);
            double magA = Math.Sqrt(a.Sum(x => (double)x * x));
            double magB = Math.Sqrt(b.Sum(x => (double)x * x));
            return 1.0 - (dot / (magA * magB));
        });

        connection.CreateFunction("vec_dot_product", (string v1, string v2) =>
        {
            float[] a = ParseVector(v1);
            float[] b = ParseVector(v2);
            return a.Zip(b).Sum(p => (double)p.First * p.Second);
        });
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared database fixture for SQLite vector integration tests.
    /// </summary>
    public sealed class VectorFixture : IAsyncLifetime
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

        /// <summary>
        /// Opens the keep-alive connection to the named in-memory database, registers the vector UDFs,
        /// and creates the runner.
        /// </summary>
        public VectorFixture()
        {
            var dbName = $"vec_tests_{Guid.NewGuid():N}";
            var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";

            this._keepAlive = new SqliteConnection(connectionString);
            this._keepAlive.Open();
            RegisterVectorFunctions(this._keepAlive);

            this.Runner = new SqliteRunner(connectionString, onConnectionOpen: RegisterVectorFunctions);
        }

        /// <summary>
        /// Gets the shared SQLite runner with vector UDFs registered.
        /// </summary>
        public SqliteRunner Runner { get; }

        /// <summary>
        /// Gets the dedicated test table name.
        /// </summary>
        public string Table { get; private set; } = default!;

        /// <summary>
        /// Returns a unique name scoped to the current test run.
        /// </summary>
        public string UniqueName(string prefix) => $"{prefix}_{this._runId}";

        /// <summary>
        /// Creates the test table and inserts seed vectors.
        /// </summary>
        public async ValueTask InitializeAsync()
        {
            this.Table = this.UniqueName("vec_tests");

            await this.Runner.ExecuteAsync($"""
                CREATE TABLE {this.Table} (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    name      TEXT NOT NULL,
                    embedding TEXT NOT NULL
                )
                """, cancellationToken: TestContext.Current.CancellationToken);

            // Four predictable seed vectors (same as PgVectorTests)
            await this.Runner.ExecuteAsync($"""
                INSERT INTO {this.Table} (name, embedding) VALUES
                    ('apple',   '[1,0,0]'),
                    ('banana',  '[0,1,0]'),
                    ('cherry',  '[0,0,1]'),
                    ('apricot', '[0.707,0.707,0]')
                """, cancellationToken: TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Drops the dedicated test table and closes the keep-alive connection.
        /// </summary>
        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            await this.Runner.ExecuteAsync($"DROP TABLE IF EXISTS {this.Table}", cancellationToken: TestContext.Current.CancellationToken);
            await this._keepAlive.CloseAsync();
        }
    }
}
