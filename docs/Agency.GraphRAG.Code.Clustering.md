# Agency.GraphRAG.Code — Clustering & Communities

#graphrag #leiden #clustering #community-detection #graph-analysis

A background worker partitions the symbol graph into communities and produces an LLM summary per community. This answers global ("tour the codebase") and subsystem ("how does auth work") queries without retrieving every symbol individually.

## The boundary problem

Pure Leiden community detection optimizes for modularity (dense intra-cluster edges, sparse inter-cluster edges) with no concept of project or namespace. Unconstrained runs typically produce:

- **Over-merging:** a giant cluster around the DI container or shared utilities because everything calls them
- **Fragmentation:** every leaf service becomes its own cluster, losing subsystem-level grouping

**Cluster summary quality is bottlenecked by cluster boundary quality** — a cluster spanning "auth + half of user management + the JWT wrapper" produces vague summaries; a cluster cleanly scoped to "JWT validation and refresh token handling" produces sharp ones.

---

## V1 approach: boundary-aware Leiden

Use existing repository structure as a prior, not a hard rule. Two mechanisms:

### Hierarchical seeding by project

Run Leiden separately within each `Project` node, then run a coarser pass at the project level. Project boundaries are effectively inviolable.

### Soft namespace bias via edge weights

Before running Leiden, multiply edge weights:

- Intra-namespace `REFERENCES` edges: ×1.5 (favor keeping namespaces together)
- Inter-project `REFERENCES` edges: ×0.5 (resist crossing project boundaries unless coupling really demands it)
- All other edges: base weight = `confidence`

Configuration parameters:

- `projectBoundaryMode`: `hard` (default) | `soft` | `off`
- `namespaceWeightMultiplier`: default 1.5
- `interProjectWeightMultiplier`: default 0.5

---

## Utility node handling (the God Object problem)

Boundary-aware weights aren't enough. A common library — `Logger`, `Result<T>`, `Constants`, `BaseService` — used by 90% of symbols will still drag unrelated features into the same community. The fix has to be structural.

### Detection: statistical + topological + convention

A node is flagged as a utility when:

1. **Statistical** — node degree (in + out) above a percentile threshold (default 99th percentile with floor of 50)
2. **Topological** — after a trial Leiden run, the node's neighbors span many disparate clusters (entropy threshold = 0.6)
3. **Convention** — optional hint: anything in `*.Common`, `*.Shared`, `*.Infrastructure`, `*.Utilities`, or `*.Abstractions` project is a utility candidate

### Procedure: two-pass clustering

1. Trial Leiden run with all edges and boundary-aware weights
2. Compute per-node degree percentiles and caller-cluster spread
3. Flag utility nodes per combined detection rule
4. Build cleaned edge set: edges incident to utility nodes excluded from modularity computation
5. Run final Leiden on the cleaned edge set
6. Post-hoc, assign utility nodes to a dedicated "Infrastructure" / "Shared" cluster per project

### Weak membership semantics

The `MEMBER_OF` edge gains a `kind` property:

- `kind: "primary"` — the symbol is a defining part of this cluster
- `kind: "utility"` — the symbol is structural and lives in a Shared/Infrastructure cluster

This affects queries: "show me everything in the auth cluster" returns primary members only by default. Utility references are surfaced as adjacent, not as members.

Configuration parameters:

- `utilityDegreePercentile`: default `99`
- `utilityDegreeFloor`: default `50`
- `utilityClusterSpreadThreshold`: default `0.6`
- `utilityNamingHints`: default `["*.Common", "*.Shared", "*.Infrastructure", "*.Utilities", "*.Abstractions"]`
- `utilityAssignmentStrategy`: `dedicated` (default) | `byDefinition`

---

## Cluster summarization

For each community, an LLM produces a **subsystem summary** plus an embedding for vector retrieval. Stored as `Cluster` nodes with `MEMBER_OF` edges.

### Cluster type classification

Every cluster is classified into one of:

- `business` — domain-specific code that exists because of what the product does
- `infrastructure` — code that exists because of how software is built (logging, DI, error handling, config)
- `mixed` — clusters that genuinely combine both; flagged for re-cluster review

The classification drives query-time pruning.

### Two prompt templates

1. **Primary clusters** get a *domain summary* prompt — "Identify the business concept this cluster owns."
2. **Utility clusters** get an *infrastructure summary* prompt — "Describe the role this cross-cutting code plays."

### Coherence scoring

Each cluster gets a self-rated coherence score on a 1–5 scale. For `business`: "Does this represent a single coherent business concept?" For `infrastructure`: "Does this represent a single coherent technical role?" Low-coherence clusters trigger a re-cluster suggestion.

---

## Re-clustering policy

- Scheduled nightly for active repos, on-demand for ad-hoc indexing
- **Not incremental in V1** — Leiden run from scratch each time
- **Old cluster summaries are replaced atomically** at the end, not piecewise

---

## Next: [[Agency.GraphRAG.Code.Querying]] — Query planning and hybrid retrieval
