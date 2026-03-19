using Agency.SQL;

namespace Agency.SQL.Test;

/// <summary>
/// Functional tests for pgvector operations against the PostgreSQL instance in docker-compose.yml.
/// Requires: docker compose up -d  (image: pgvector/pgvector:pg16)
///
/// Test vectors are 3-dimensional unit vectors so expected distances are
/// analytically exact and assertions can be made with tight tolerances.
///
///   "apple"   → [1, 0, 0]
///   "banana"  → [0, 1, 0]
///   "cherry"  → [0, 0, 1]
///   "apricot" → [0.707, 0.707, 0]   ≈ normalised midpoint of apple + banana
/// </summary>
public sealed class PgVectorTests : IClassFixture<PgVectorTests.VectorFixture>
{
    private const double Tolerance = 1e-4;

    private readonly VectorFixture _fx;

    public PgVectorTests(VectorFixture fx)
    {
        _fx = fx;
    }

    // ── Extension presence ──────────────────────────────────────────────────

    [Fact]
    public async Task PgVector_ExtensionIsInstalled()
    {
        var ds = await _fx.Runner.QueryAsync("""
            SELECT extname
            FROM pg_extension
            WHERE extname = 'vector'
            """);

        Assert.Single(ds.Rows);
        Assert.Equal("vector", ds["extname", 0]);
    }

    // ── DDL ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VectorColumn_IsCreatedWithCorrectDimension()
    {
        // information_schema does not expose custom types, so query pg_attribute directly
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT atttypmod
            FROM pg_attribute
            WHERE attrelid = '{_fx.Table}'::regclass
              AND attname    = 'embedding'
            """);

        Assert.Single(ds.Rows);
        // atttypmod for vector(n) encodes the dimension as the raw value
        int typmod = Convert.ToInt32(ds["atttypmod", 0]);
        Assert.Equal(3, typmod);
    }

    // ── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public async Task VectorColumn_RoundTrip_PreservesComponents()
    {
        // Cast to text so Npgsql returns it as a plain string without needing
        // the Pgvector type plugin.
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT embedding::text AS vec
            FROM {_fx.Table}
            WHERE name = 'apple'
            """);

        Assert.Single(ds.Rows);

        string raw = (string)ds["vec", 0]!;        // e.g. "[1,0,0]"
        float[] components = ParseVector(raw);

        Assert.Equal(3, components.Length);
        Assert.Equal(1f, components[0], precision: 4);
        Assert.Equal(0f, components[1], precision: 4);
        Assert.Equal(0f, components[2], precision: 4);
    }

    // ── L2 distance (<->) ───────────────────────────────────────────────────

    [Fact]
    public async Task L2Distance_SameVector_IsZero()
    {
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT embedding <-> '[1,0,0]' AS dist
            FROM {_fx.Table}
            WHERE name = 'apple'
            """);

        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(0.0, dist, Tolerance);
    }

    [Fact]
    public async Task L2Distance_OrthogonalUnitVectors_IsSqrtTwo()
    {
        // ||[1,0,0] - [0,1,0]||₂  =  √(1+1+0)  =  √2
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT embedding <-> '[1,0,0]' AS dist
            FROM {_fx.Table}
            WHERE name = 'banana'
            """);

        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(Math.Sqrt(2), dist, Tolerance);
    }

    // ── Cosine distance (<=>) ────────────────────────────────────────────────

    [Fact]
    public async Task CosineDistance_SameVector_IsZero()
    {
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT embedding <=> '[1,0,0]' AS dist
            FROM {_fx.Table}
            WHERE name = 'apple'
            """);

        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(0.0, dist, Tolerance);
    }

    [Fact]
    public async Task CosineDistance_OrthogonalVectors_IsOne()
    {
        // cos([1,0,0], [0,1,0]) = 0  →  cosine distance = 1 - 0 = 1
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT embedding <=> '[1,0,0]' AS dist
            FROM {_fx.Table}
            WHERE name = 'banana'
            """);

        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(1.0, dist, Tolerance);
    }

    [Fact]
    public async Task CosineDistance_DiagonalVector_IsExpectedValue()
    {
        // apricot ≈ [0.707, 0.707, 0]
        // cos([1,0,0], [0.707,0.707,0]) = 0.707  →  distance = 1 - 0.707 ≈ 0.293
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT embedding <=> '[1,0,0]' AS dist
            FROM {_fx.Table}
            WHERE name = 'apricot'
            """);

        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(1.0 - (1.0 / Math.Sqrt(2)), dist, Tolerance);
    }

    // ── Inner product (<#>) ──────────────────────────────────────────────────

    [Fact]
    public async Task InnerProduct_SameUnitVector_IsNegativeOne()
    {
        // pgvector returns the *negative* inner product for <#>
        // [1,0,0] · [1,0,0] = 1  →  <#> returns -1
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT embedding <#> '[1,0,0]' AS neg_dot
            FROM {_fx.Table}
            WHERE name = 'apple'
            """);

        double negDot = Convert.ToDouble(ds["neg_dot", 0]);
        Assert.Equal(-1.0, negDot, Tolerance);
    }

    [Fact]
    public async Task InnerProduct_OrthogonalVectors_IsZero()
    {
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT embedding <#> '[1,0,0]' AS neg_dot
            FROM {_fx.Table}
            WHERE name = 'banana'
            """);

        double negDot = Convert.ToDouble(ds["neg_dot", 0]);
        Assert.Equal(0.0, negDot, Tolerance);
    }

    // ── Nearest-neighbour ORDER BY ────────────────────────────────────────────

    [Fact]
    public async Task NearestNeighbour_CosineTopOne_ReturnsExactMatch()
    {
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT name
            FROM {_fx.Table}
            ORDER BY embedding <=> '[1,0,0]'
            LIMIT 1
            """);

        Assert.Single(ds.Rows);
        Assert.Equal("apple", ds["name", 0]);
    }

    [Fact]
    public async Task NearestNeighbour_L2TopTwo_ReturnsAppleAndApricot()
    {
        // apple  distance = 0
        // apricot distance ≈ 0.765  (< √2 ≈ 1.414 for banana/cherry)
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT name, (embedding <-> '[1,0,0]') AS dist
            FROM {_fx.Table}
            ORDER BY dist
            LIMIT 2
            """);

        Assert.Equal(2, ds.Rows.Count);
        Assert.Equal("apple", ds["name", 0]);
        Assert.Equal("apricot", ds["name", 1]);
    }

    [Fact]
    public async Task NearestNeighbour_WithDistanceThreshold_ExcludesDistantRows()
    {
        // Only apple (dist 0) and apricot (dist ~0.765) are within 0.9 of [1,0,0]
        var ds = await _fx.Runner.QueryAsync($"""
            SELECT name
            FROM {_fx.Table}
            WHERE (embedding <-> '[1,0,0]') < 0.9
            ORDER BY embedding <-> '[1,0,0]'
            """);

        Assert.Equal(2, ds.Rows.Count);
        Assert.Equal("apple", ds["name", 0]);
        Assert.Equal("apricot", ds["name", 1]);
    }

    // ── Parameterised ExecuteAsync ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WithParameters_InsertsNewRow()
    {
        try
        {
            int affected = await _fx.Runner.ExecuteAsync(
                $"INSERT INTO {_fx.Table} (name, embedding) VALUES (@name, @vec::vector)",
                new Dictionary<string, object?> { ["name"] = "mango", ["vec"] = "[0,0,1]" });

            Assert.Equal(1, affected);

            var ds = await _fx.Runner.QueryAsync(
                $"SELECT name FROM {_fx.Table} WHERE name = @name",
                new Dictionary<string, object?> { ["name"] = "mango" });

            Assert.Single(ds.Rows);
            Assert.Equal("mango", ds["name", 0]);
        }
        finally
        {
            await _fx.Runner.ExecuteAsync(
                $"DELETE FROM {_fx.Table} WHERE name = @name",
                new Dictionary<string, object?> { ["name"] = "mango" });
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithParameters_UpdatesEmbedding()
    {
        // Insert a temporary row, then update its vector via parameters.
        await _fx.Runner.ExecuteAsync(
            $"INSERT INTO {_fx.Table} (name, embedding) VALUES (@name, @vec::vector)",
            new Dictionary<string, object?> { ["name"] = "temp_update", ["vec"] = "[0,1,0]" });

        try
        {
            int affected = await _fx.Runner.ExecuteAsync(
                $"UPDATE {_fx.Table} SET embedding = @vec::vector WHERE name = @name",
                new Dictionary<string, object?> { ["name"] = "temp_update", ["vec"] = "[1,0,0]" });

            Assert.Equal(1, affected);

            var ds = await _fx.Runner.QueryAsync(
                $"SELECT embedding::text AS vec FROM {_fx.Table} WHERE name = @name",
                new Dictionary<string, object?> { ["name"] = "temp_update" });

            string raw = (string)ds["vec", 0]!;
            float[] components = ParseVector(raw);
            Assert.Equal(1f, components[0], precision: 4);
            Assert.Equal(0f, components[1], precision: 4);
            Assert.Equal(0f, components[2], precision: 4);
        }
        finally
        {
            await _fx.Runner.ExecuteAsync(
                $"DELETE FROM {_fx.Table} WHERE name = @name",
                new Dictionary<string, object?> { ["name"] = "temp_update" });
        }
    }

    // ── Parameterised QueryAsync ──────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_WithNameParameter_ReturnsMatchingVector()
    {
        var ds = await _fx.Runner.QueryAsync(
            $"SELECT embedding::text AS vec FROM {_fx.Table} WHERE name = @name",
            new Dictionary<string, object?> { ["name"] = "cherry" });

        Assert.Single(ds.Rows);
        float[] components = ParseVector((string)ds["vec", 0]!);
        Assert.Equal(0f, components[0], precision: 4);
        Assert.Equal(0f, components[1], precision: 4);
        Assert.Equal(1f, components[2], precision: 4);
    }

    [Fact]
    public async Task QueryAsync_WithVectorParameter_ComputesL2Distance()
    {
        // Query vector matches banana exactly → distance should be 0.
        var ds = await _fx.Runner.QueryAsync(
            $"SELECT (embedding <-> @vec::vector) AS dist FROM {_fx.Table} WHERE name = @name",
            new Dictionary<string, object?> { ["name"] = "banana", ["vec"] = "[0,1,0]" });

        Assert.Single(ds.Rows);
        double dist = Convert.ToDouble(ds["dist", 0]);
        Assert.Equal(0.0, dist, Tolerance);
    }

    [Fact]
    public async Task QueryAsync_WithDistanceThresholdParameter_FiltersRows()
    {
        // Only apple (dist 0) and apricot (dist ~0.765) are within threshold 0.9 of [1,0,0].
        var ds = await _fx.Runner.QueryAsync(
            $"SELECT name FROM {_fx.Table} WHERE (embedding <-> '[1,0,0]') < @threshold ORDER BY embedding <-> '[1,0,0]'",
            new Dictionary<string, object?> { ["threshold"] = 0.9 });

        Assert.Equal(2, ds.Rows.Count);
        Assert.Equal("apple", ds["name", 0]);
        Assert.Equal("apricot", ds["name", 1]);
    }

    [Fact]
    public async Task QueryAsync_WithVectorAndLimitParameters_ReturnsTopKNeighbours()
    {
        // Pass the query vector as a parameter; verify the two nearest to [1,0,0] are returned.
        var ds = await _fx.Runner.QueryAsync(
            $"SELECT name, (embedding <-> @vec::vector) AS dist FROM {_fx.Table} ORDER BY dist LIMIT @k",
            new Dictionary<string, object?> { ["vec"] = "[1,0,0]", ["k"] = 2 });

        Assert.Equal(2, ds.Rows.Count);
        Assert.Equal("apple", ds["name", 0]);
        Assert.Equal("apricot", ds["name", 1]);
    }

    // ── Indexes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task HnswIndex_CosineOps_CanBeCreatedAndQueried()
    {
        var indexName = _fx.UniqueName("hnsw_idx");
        var indexTable = _fx.UniqueName("hnsw_docs");

        await _fx.Runner.ExecuteAsync($"""
            CREATE TABLE {indexTable} (
                id        SERIAL PRIMARY KEY,
                name      TEXT NOT NULL,
                embedding vector(3) NOT NULL
            )
            """);

        try
        {
            await _fx.Runner.ExecuteAsync($"""
                INSERT INTO {indexTable} (name, embedding)
                VALUES ('a', '[1,0,0]'), ('b', '[0,1,0]'), ('c', '[0,0,1]')
                """);

            await _fx.Runner.ExecuteAsync($"""
                CREATE INDEX {indexName}
                ON {indexTable}
                USING hnsw (embedding vector_cosine_ops)
                """);

            // Verify index was created
            var idxDs = await _fx.Runner.QueryAsync($"""
                SELECT indexname
                FROM pg_indexes
                WHERE tablename = '{indexTable}'
                  AND indexname  = '{indexName}'
                """);

            Assert.Single(idxDs.Rows);

            // Nearest-neighbour query should still return correct result through the index
            var ds = await _fx.Runner.QueryAsync($"""
                SELECT name
                FROM {indexTable}
                ORDER BY embedding <=> '[1,0,0]'
                LIMIT 1
                """);

            Assert.Single(ds.Rows);
            Assert.Equal("a", ds["name", 0]);
        }
        finally
        {
            await _fx.Runner.ExecuteAsync($"DROP TABLE IF EXISTS {indexTable}");
        }
    }

    [Fact]
    public async Task IvfflatIndex_L2Ops_CanBeCreatedAndQueried()
    {
        var indexTable = _fx.UniqueName("ivf_docs");

        await _fx.Runner.ExecuteAsync($"""
            CREATE TABLE {indexTable} (
                id        SERIAL PRIMARY KEY,
                embedding vector(3) NOT NULL
            )
            """);

        try
        {
            // IVFFlat requires at least as many rows as lists; use lists=1 for small test data
            await _fx.Runner.ExecuteAsync($"""
                INSERT INTO {indexTable} (embedding)
                SELECT ('[' || (random())::text || ',' || (random())::text || ',' || (random())::text || ']')::vector
                FROM generate_series(1, 10)
                """);

            await _fx.Runner.ExecuteAsync($"""
                CREATE INDEX ON {indexTable}
                USING ivfflat (embedding vector_l2_ops)
                WITH (lists = 1)
                """);

            // A nearest-neighbour query should execute without error
            var ds = await _fx.Runner.QueryAsync($"""
                SELECT id
                FROM {indexTable}
                ORDER BY embedding <-> '[0.5,0.5,0.5]'
                LIMIT 1
                """);

            Assert.Single(ds.Rows);
        }
        finally
        {
            await _fx.Runner.ExecuteAsync($"DROP TABLE IF EXISTS {indexTable}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Parses a pgvector text literal like "[1,0,0]" into a float array.</summary>
    private static float[] ParseVector(string raw)
        => raw.Trim('[', ']')
              .Split(',')
              .Select(float.Parse)
              .ToArray();

    // ── Fixture ───────────────────────────────────────────────────────────────

    public sealed class VectorFixture : IAsyncLifetime
    {
        private const string ConnectionString =
            "Host=localhost;Port=5432;Username=dev_user;Password=dev_password;Database=dev_db";

        private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

        public PostgreSqlRunner Runner { get; } = new(ConnectionString);

        public string Table { get; private set; } = default!;

        public string UniqueName(string prefix) => $"{prefix}_{_runId}";

        public async Task InitializeAsync()
        {
            await Runner.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");

            Table = UniqueName("vec_tests");

            await Runner.ExecuteAsync($"""
                CREATE TABLE {Table} (
                    id        SERIAL PRIMARY KEY,
                    name      TEXT NOT NULL,
                    embedding vector(3) NOT NULL
                )
                """);

            // Four predictable seed vectors
            await Runner.ExecuteAsync($"""
                INSERT INTO {Table} (name, embedding) VALUES
                    ('apple',   '[1,0,0]'),
                    ('banana',  '[0,1,0]'),
                    ('cherry',  '[0,0,1]'),
                    ('apricot', '[0.707,0.707,0]')
                """);
        }

        public async Task DisposeAsync()
        {
            await Runner.ExecuteAsync($"DROP TABLE IF EXISTS {Table}");
        }
    }
}
