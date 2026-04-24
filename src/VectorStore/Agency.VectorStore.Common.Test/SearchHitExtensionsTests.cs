namespace Agency.VectorStore.Common.Test;

public sealed class SearchHitExtensionsTests
{
    // -------------------------------------------------------------------------
    // ToDataset - Basic Functionality
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that ToDataset converts a single search hit to a dataset with one row.
    /// </summary>
    [Fact]
    public void ToDataset_SingleSearchHit_CreatesDatasetWithOneRow()
    {
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key1", "value1", null, 0.5, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        Assert.Single(dataset.Rows);
    }

    /// <summary>
    /// Verifies that ToDataset converts multiple search hits to a dataset with multiple rows.
    /// </summary>
    [Fact]
    public void ToDataset_MultipleSearchHits_CreatesDatasetWithMultipleRows()
    {
        var now = DateTimeOffset.UtcNow;
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key1", "value1", null, 0.1, now),
            new SearchHit<string>("test-user", "test-session", "key2", "value2", null, 0.2, now),
            new SearchHit<string>("test-user", "test-session", "key3", "value3", null, 0.3, now),
        };

        var dataset = hits.ToDataset();

        Assert.Equal(3, dataset.Rows.Count);
    }

    /// <summary>
    /// Verifies that ToDataset creates a dataset with the expected columns.
    /// </summary>
    [Fact]
    public void ToDataset_AnyInput_CreatesDatasetWithExpectedColumns()
    {
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key", "value", null, 0.5, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        var columnNames = dataset.Columns.Select(c => c.ColumnName).ToList();
        Assert.Equal(5, columnNames.Count);
        Assert.Contains("Key", columnNames);
        Assert.Contains("Value", columnNames);
        Assert.Contains("Distance", columnNames);
        Assert.Contains("SimilarityPercentage", columnNames);
        Assert.Contains("UpdatedOn", columnNames);
    }

    // -------------------------------------------------------------------------
    // ToDataset - Column Values
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that the Key column contains the correct value from the search hit.
    /// </summary>
    [Fact]
    public void ToDataset_SingleHit_KeyColumnHasCorrectValue()
    {
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "test-key", "value", null, 0.5, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        var keyValue = dataset[0, 0];
        Assert.Equal("test-key", keyValue);
    }

    /// <summary>
    /// Verifies that the Value column contains the correct value from the search hit.
    /// </summary>
    [Fact]
    public void ToDataset_SingleHit_ValueColumnHasCorrectValue()
    {
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key", "test-value", null, 0.5, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        var valueData = dataset[1, 0];
        Assert.Equal("test-value", valueData);
    }

    /// <summary>
    /// Verifies that the Distance column contains the correct value from the search hit.
    /// </summary>
    [Fact]
    public void ToDataset_SingleHit_DistanceColumnHasCorrectValue()
    {
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key", "value", null, 0.25, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        var distanceValue = dataset[2, 0];
        Assert.Equal(0.25, distanceValue);
    }

    /// <summary>
    /// Verifies that the SimilarityPercentage column is calculated correctly.
    /// </summary>
    [Fact]
    public void ToDataset_SingleHit_SimilarityPercentageColumnIsCalculatedCorrectly()
    {
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key", "value", null, 0.0, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        var similarityValue = dataset[3, 0];
        Assert.Equal(100.0, similarityValue);
    }

    /// <summary>
    /// Verifies that SimilarityPercentage handles maximum distance correctly (2.0 → 0%).
    /// </summary>
    [Fact]
    public void ToDataset_MaxDistance_SimilarityPercentageIsZero()
    {
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key", "value", null, 2.0, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        var similarityValue = dataset[3, 0];
        Assert.Equal(0.0, similarityValue);
    }

    /// <summary>
    /// Verifies that SimilarityPercentage handles mid-range distance correctly.
    /// </summary>
    [Fact]
    public void ToDataset_MidRangeDistance_SimilarityPercentageIsCorrect()
    {
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key", "value", null, 1.0, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        var similarityValue = dataset[3, 0];
        Assert.Equal(0.0, similarityValue);
    }

    /// <summary>
    /// Verifies that the UpdatedOn column contains the correct timestamp.
    /// </summary>
    [Fact]
    public void ToDataset_SingleHit_UpdatedOnColumnHasCorrectValue()
    {
        var timestamp = new DateTimeOffset(2026, 3, 15, 10, 30, 45, TimeSpan.Zero);
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key", "value", null, 0.5, timestamp)
        };

        var dataset = hits.ToDataset();

        var updatedOnValue = dataset[4, 0];
        Assert.Equal(timestamp, updatedOnValue);
    }

    // -------------------------------------------------------------------------
    // ToDataset - Multiple Rows
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that all rows in the dataset contain the correct data.
    /// </summary>
    [Fact]
    public void ToDataset_MultipleHits_AllRowsHaveCorrectData()
    {
        var now = DateTimeOffset.UtcNow;
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key1", "value1", null, 0.1, now),
            new SearchHit<string>("test-user", "test-session", "key2", "value2", null, 0.2, now),
        };

        var dataset = hits.ToDataset();

        Assert.Equal("key1", dataset[0, 0]);
        Assert.Equal("value1", dataset[1, 0]);
        Assert.Equal(0.1, dataset[2, 0]);

        Assert.Equal("key2", dataset[0, 1]);
        Assert.Equal("value2", dataset[1, 1]);
        Assert.Equal(0.2, dataset[2, 1]);
    }

    // -------------------------------------------------------------------------
    // ToDataset - Different Value Types
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that ToDataset works with different value types (e.g., integers).
    /// </summary>
    [Fact]
    public void ToDataset_IntegerValues_CreatesDatasetCorrectly()
    {
        var hits = new List<SearchHit<int>>
        {
            new SearchHit<int>("test-user", "test-session", "key1", 42, null, 0.5, DateTimeOffset.UtcNow),
            new SearchHit<int>("test-user", "test-session", "key2", 100, null, 0.3, DateTimeOffset.UtcNow),
        };

        var dataset = hits.ToDataset();

        Assert.Equal(2, dataset.Rows.Count);
        Assert.Equal(42, dataset[1, 0]);
        Assert.Equal(100, dataset[1, 1]);
    }

    /// <summary>
    /// Verifies that ToDataset works with object values.
    /// </summary>
    [Fact]
    public void ToDataset_ObjectValues_CreatesDatasetCorrectly()
    {
        var obj1 = new { Name = "Object1", Score = 95 };
        var obj2 = new { Name = "Object2", Score = 87 };

        var hits = new List<SearchHit<object>>
        {
            new SearchHit<object>("test-user", "test-session", "key1", obj1, null, 0.5, DateTimeOffset.UtcNow),
            new SearchHit<object>("test-user", "test-session", "key2", obj2, null, 0.3, DateTimeOffset.UtcNow),
        };

        var dataset = hits.ToDataset();

        Assert.Equal(2, dataset.Rows.Count);
        Assert.Same(obj1, dataset[1, 0]);
        Assert.Same(obj2, dataset[1, 1]);
    }

    // -------------------------------------------------------------------------
    // ToDataset - Empty Collection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that ToDataset handles an empty collection by creating a dataset with no rows.
    /// </summary>
    [Fact]
    public void ToDataset_EmptyCollection_CreatesDatasetWithNoRows()
    {
        var hits = new List<SearchHit<string>>();

        var dataset = hits.ToDataset();

        Assert.Empty(dataset.Rows);
        Assert.Equal(5, dataset.Columns.Count);
    }

    // -------------------------------------------------------------------------
    // ToDataset - Column Ordering
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that columns are in the expected order.
    /// </summary>
    [Fact]
    public void ToDataset_AnyInput_ColumnsAreInExpectedOrder()
    {
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key", "value", null, 0.5, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        var columns = dataset.Columns.ToList();
        Assert.Equal("Key", columns[0].ColumnName);
        Assert.Equal("Value", columns[1].ColumnName);
        Assert.Equal("Distance", columns[2].ColumnName);
        Assert.Equal("SimilarityPercentage", columns[3].ColumnName);
        Assert.Equal("UpdatedOn", columns[4].ColumnName);
    }

    /// <summary>
    /// Verifies that column ordinals are set correctly.
    /// </summary>
    [Fact]
    public void ToDataset_AnyInput_ColumnOrdinalsAreCorrect()
    {
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key", "value", null, 0.5, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        var columns = dataset.Columns.ToList();
        Assert.Equal(0, columns[0].ColumnOrdinal);
        Assert.Equal(1, columns[1].ColumnOrdinal);
        Assert.Equal(2, columns[2].ColumnOrdinal);
        Assert.Equal(3, columns[3].ColumnOrdinal);
        Assert.Equal(4, columns[4].ColumnOrdinal);
    }

    // -------------------------------------------------------------------------
    // ToDataset - Metadata Handling
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that ToDataset works correctly with search hits containing metadata.
    /// </summary>
    [Fact]
    public void ToDataset_WithMetadata_CreatesDatasetCorrectly()
    {
        var metadata = new Dictionary<string, object> { { "type", "document" }, { "source", "test" } };
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key1", "value1", metadata, 0.5, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        Assert.Single(dataset.Rows);
        Assert.Equal("key1", dataset[0, 0]);
    }

    /// <summary>
    /// Verifies that ToDataset works correctly with search hits containing null metadata.
    /// </summary>
    [Fact]
    public void ToDataset_WithNullMetadata_CreatesDatasetCorrectly()
    {
        var hits = new List<SearchHit<string>>
        {
            new SearchHit<string>("test-user", "test-session", "key1", "value1", null, 0.5, DateTimeOffset.UtcNow)
        };

        var dataset = hits.ToDataset();

        Assert.Single(dataset.Rows);
        Assert.Equal("key1", dataset[0, 0]);
    }
}
