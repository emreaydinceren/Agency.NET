# Project Summary

This document provides a summary of the projects within the Agency solution and their respective responsibilities. The Agency solution is a .NET 10 RAG (Retrieval-Augmented Generation) pipeline that manages embeddings, vector storage, and LLM interactions.

## Core Data Models

| Project | Description |
| :--- | :--- |
| `Agency.Common` | Foundational types including `Dataset` (represents tabular result sets with column metadata) and `IColumnMetadata`. Provides base data structures used across the solution. |

## LLM Abstractions & Implementations

| Project | Description |
| :--- | :--- |
| `Agency.Llm.Common` | Defines the core `ILlmClient` interface with `SendAsync()` and `StreamAsync()` methods for non-streaming and streaming LLM completions. Includes shared types like `LlmResponse`, `LlmTokenUsage`, and `StopReason`. |
| `Agency.Llm.Claude` | Implementation of `ILlmClient` for the Anthropic Claude API. Provides full OpenTelemetry instrumentation (ActivitySource + Meter) with request counts, error tracking, duration histograms, and token usage metrics. |
| `Agency.Llm.OpenAI` | Implementation of `ILlmClient` for the OpenAI Chat API. Includes comprehensive observability for request tracing, performance monitoring, and token usage analysis. |

## Embedding Abstractions & Implementations

| Project | Description |
| :--- | :--- |
| `Agency.Embeddings.Common` | Defines the core `IEmbeddingGenerator` interface for generating vector embeddings from text. Provides the contract used by all embedding providers. |
| `Agency.Embeddings.OpenAI` | Implementation of `IEmbeddingGenerator` using OpenAI's embedding models. Supports configurable model selection and includes options for batch processing. |

## Vector Store Abstractions & Implementations

| Project | Description |
| :--- | :--- |
| `Agency.VectorStore.Common` | Defines core abstractions including `IKVStore` interface for vector-backed key-value stores, `Query` for specifying search parameters (key, value, metadata filters, limits), and `SearchHit<TValue>` for representing search results with similarity scoring and recency metrics. |
| `Agency.VectorStore.Sql.Postgre` | PostgreSQL implementation of `IKVStore` using the `pgvector` extension for efficient vector similarity search and JSONB for flexible metadata filtering. Enables production-scale vector searches. |
| `Agency.VectorStore.Sql.Sqlite` | SQLite implementation of `IKVStore` with custom User-Defined Functions (UDFs) for vector distance calculations. Optimized for local development and lightweight deployments. |

## Database & SQL Infrastructure

| Project | Description |
| :--- | :--- |
| `Agency.Sql.Postgre` | Provides PostgreSQL infrastructure including `PostgreSqlRunner` for executing raw SQL queries and `SQLQueryEmbedder` for processing vectorized SQL commands. The `SQLQueryEmbedder` uses regex to find `vectorize('<text>')` placeholders in SQL and replaces them with pgvector literal format (`[f1,f2,...]`) via the injected `IEmbeddingGenerator`. |

## RAG & Data Processing

| Project | Description |
| :--- | :--- |
| `Agency.RagFormatter` | Provides RAG (Retrieval-Augmented Generation) formatting utilities, including `DatasetExtensions.ToMarkdownTable()` to convert tabular `Dataset` results into markdown format suitable for inclusion in LLM prompts as context. |

## Console Application

| Project | Description |
| :--- | :--- |
| `Agency.Console` | Entry point for the Agency application. Demonstrates end-to-end usage of the RAG pipeline: embeddings → vector storage → retrieval → LLM querying. |

## Test Projects

| Project | Description |
| :--- | :--- |
| `Agency.Embeddings.OpenAI.Test` | Unit and functional tests for OpenAI embedding generation, including HTTP mocking and real API integration. |
| `Agency.Llm.Test` | Functional tests for Claude and OpenAI LLM clients, with separate configurations for CI and local development (targets LM Studio at `http://llm-host.example:1234` for functional tests). |
| `Agency.Sql.Postgre.Test` | Functional tests for PostgreSQL SQL runner and `SQLQueryEmbedder` logic using Docker-based PostgreSQL with pgvector extension. |
| `Agency.Sql.Sqlite.Test` | Functional tests for SQLite SQL runner and query embedding with custom vector distance UDFs. |
| `Agency.VectorStore.Common.Test` | Unit tests for vector store abstractions like `SearchHit` similarity scoring and `Query` filtering logic. |
| `Agency.VectorStore.Sql.Postgre.Test` | Functional tests for the PostgreSQL vector store implementation, verifying schema initialization, upserts, complex similarity searches, and metadata filtering. |
| `Agency.VectorStore.Sql.Sqlite.Test` | Functional tests for the SQLite vector store implementation and custom vector distance functionality. |