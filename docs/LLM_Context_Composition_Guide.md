# LLM Context Composition: Building Effective Prompts with Code Examples

A comprehensive guide to composing, managing, and optimizing LLM context for production systems. Includes context engineering patterns, memory management, and practical code examples.

---

## Context Engineering Fundamentals

### 1. [Context Engineering Guide - Prompt Engineering Guide](https://www.promptingguide.ai/guides/context-engineering-guide)
Foundational overview of context engineering: the discipline of designing and optimizing all information fed to an LLM. Explains the distinction between prompt engineering (how you write instructions) and context engineering (managing the entire information environment).

### 2. [Context Engineering for LLM Apps: Prompts, System Messages, Tools, and Memory - Aishwarya Srinivasan](https://aishwaryasrinivasan.substack.com/p/context-engineering-for-llm-apps)
Comprehensive guide covering how context comes from multiple sources: system messages, user queries, retrieved documents (RAG), tool outputs, conversation history, and persistent memory. Includes practical patterns for composition.

### 3. [Context Engineering: Bringing Engineering Discipline to Prompts - Add Your Own](https://addyo.substack.com/p/context-engineering-bringing-engineering)
Explores context engineering as an engineering discipline. Covers the mental model of LLM context as "RAM" and the importance of loading the context window with exactly what's needed for the task.

### 4. [Context Engineering: Foundations, Categories, and Techniques of Prompt Engineering - Daily Dose of DS](https://www.dailydoseofds.com/llmops-crash-course-part-5/)
Deep dive into prompt engineering foundations and techniques. Categories of prompting approaches and their application to different tasks.

---

## System Prompts & RAG Context

### 5. [Retrieval Augmented Generation (RAG) for LLMs - Prompt Engineering Guide](https://www.promptingguide.ai/research/rag)
Essential guide to RAG architecture for LLMs. Explains the RAG pipeline: query → retrieve relevant chunks → synthesize context → prompt LLM. Includes code examples for chunking, embedding, and retrieval.

### 6. [Mastering the Art of Prompting LLMs for RAG - Progress](https://www.progress.com/blogs/mastering-the-art-of-prompting-llms-for-rag)
Practical guide to crafting effective prompts specifically for RAG pipelines. Covers how to instruct the LLM to use retrieved context, prevent hallucination, and handle insufficient information.

### 7. [Top 5 LLM Prompts for Retrieval-Augmented Generation (RAG) - Scout](https://www.scoutos.com/blog/top-5-llm-prompts-for-retrieval-augmented-generation-rag)
Collection of production-tested prompt templates for RAG systems. Provides ready-to-use examples with explanations of why each pattern works.

### 8. [The Art and Science of RAG: Mastering Prompt Templates and Contextual Understanding - Ajay Verma](https://medium.com/@ajayverma23/the-art-and-science-of-rag-mastering-prompt-templates-and-contextual-understanding-a47961a57e27)
Medium article on RAG prompt template design. Covers contextual understanding, template patterns, and iterative refinement strategies.

---

## Context Window Management with Code

### 9. [Context Window Management for LLM Apps: Dev Guide - Redis](https://redis.io/blog/context-window-management-llm-apps-developer-guide/)
Developer-focused guide to managing context windows in production. Includes code patterns for token counting, context truncation strategies, and Redis-based memory management.

### 10. [Top Techniques to Manage Context Lengths in LLMs - Agenta](https://agenta.ai/blog/top-6-techniques-to-manage-context-length-in-llms)
Practical techniques for handling long context: smart chunking, filtering, compression, and diversification. Each technique includes implementation considerations and code patterns.

### 11. [Calculating LLM Token Counts: A Practical Guide - Winder.ai](https://winder.ai/calculating-token-counts-llm-context-windows-practical-guide/)
Comprehensive guide to token counting in practice. Covers tokenization differences across models, tools for accurate counting, and cost/latency implications of token usage.

### 12. [How to Optimize Token Efficiency When Prompting - Portkey](https://portkey.ai/blog/optimize-token-efficiency-in-prompts/)
Practical techniques for reducing token usage without sacrificing quality. Includes examples of prompt compression, efficient formatting, and optimization strategies.

---

## Memory & Context Composition Patterns

### 13. [The Ultimate Guide to LLM Memory: From Context Windows to Advanced Agent Memory Systems - Tanishk Soni](https://medium.com/@sonitanishk2003/the-ultimate-guide-to-llm-memory-from-context-windows-to-advanced-agent-memory-systems-3ec106d2a345)
Comprehensive guide to LLM memory architecture. Covers short-term memory (context window), long-term memory (persistent storage), memory formation patterns, and caching strategies with code examples.

### 14. [LLM Chat History Summarization: Best Practices and Techniques - Mem0](https://mem0.ai/blog/llm-chat-history-summarization-guide-2025)
Production patterns for managing conversation history. Covers summarization techniques, the ConversationSummaryBufferMemory pattern, and strategies for maintaining coherence across long conversations.

### 15. [How Does LLM Memory Work? Building Context-Aware AI Applications - DataCamp](https://www.datacamp.com/blog/how-does-llm-memory-work)
Educational guide to LLM memory systems. Explains memory hierarchy, formation patterns, and practical approaches to building context-aware applications.

### 16. [Conversational Memory for LLMs with Langchain - Pinecone](https://www.pinecone.io/learn/series/langchain/langchain-conversational-memory/)
Hands-on guide to implementing conversational memory using LangChain. Includes code examples for different memory types: buffer, summary, entity memory, and hybrid approaches.

### 17. [LLM Context Management Overview - Emergent Mind](https://www.emergentmind.com/topics/llm-context-management)
Curated overview of context management approaches and patterns. Links to key resources and emerging best practices.

---

## Advanced Context Strategies

### 18. [LLM Context Management: How to Improve Performance and Lower Costs - 16x Engineer](https://eval.16x.engineer/blog/llm-context-management-guide)
Strategies for optimizing both performance and cost through context management. Covers input token caching, context prioritization, and smart retrieval patterns.

### 19. [The LLM Context Problem in 2026: Strategies for Memory, Relevance, and Scale - LogRocket](https://blog.logrocket.com/llm-context-problem/)
Analysis of the evolving context problem as models get larger. Covers scaling strategies, relevance mechanisms, and future approaches to context management.

### 20. [Context Engineering: Memory and Temporal Context - Daily Dose of DS](https://www.dailydoseofds.com/llmops-crash-course-part-8/)
Advanced patterns for temporal context: how to include time-sensitive information, manage context decay, and implement memory expiration strategies.

---

## Practical Implementation Resources

### 21. [LLM RAG Tutorial: Examples and Best Practices - LaunchDarkly](https://launchdarkly.com/blog/llm-rag-tutorial/)
Step-by-step tutorial on building RAG systems with code examples. Covers the full pipeline from document ingestion to context composition to LLM querying.

### 22. [Managing Retrieved Context for Generation - APXML](https://apxml.com/courses/getting-started-with-llm-toolkit/chapter-6-building-rag-systems/managing-retrieved-context)
Course material on post-retrieval context management. Covers ranking, filtering, compression, and formatting retrieved documents for LLM consumption.

### 23. [Prompt Compression in Large Language Models (LLMs): Making Every Token Count - Sahin Ahmed](https://medium.com/@sahin.samia/prompt-compression-in-large-language-models-llms-making-every-token-count-078a2d1c7e03)
In-depth exploration of prompt compression techniques. Includes code patterns for implementing compression at different stages of the pipeline.

### 24. [LLM Prompt Best Practices for Large Context Windows - Winder.ai](https://winder.ai/llm-prompt-best-practices-large-context-windows/)
Best practices specifically for modern models with large context windows (100k+ tokens). Covers how to take advantage of extended context without sacrificing coherence.

---

## Code Example Resources & Tools

### 25. [LangChain Context Engineering Documentation](https://docs.langchain.com/oss/python/langchain/context-engineering)
Official LangChain documentation on context engineering. Includes patterns for composing context, managing memory, and building middleware for context control.

### 26. [Prompt in Context Learning - GitHub](https://github.com/EgoAlpha/prompt-in-context-learning)
Open-source repository with comprehensive resources on in-context learning and prompt engineering. Includes code examples, tutorials, and a curated list of techniques.

### 27. [Long-term Memory in LLM Applications - LangChain Memory](https://langchain-ai.github.io/langmem/concepts/conceptual_guide/)
LangChain's memory framework documentation. Covers building persistent memory systems and composing context across sessions.

### 28. [Completion Token Usage & Cost - liteLLM](https://docs.litellm.ai/docs/completion/token_usage)
Practical guide to token counting and cost tracking using liteLLM. Includes code examples for integrating token counting into your applications.

### 29. [LLM Context & Token Management - APXML Course](https://apxml.com/courses/getting-started-with-llm-toolkit/chapter-3-context-and-token-management)
Educational course module on context and token management. Covers practical implementations with working code examples.

### 30. [GitHub - Prompt in Context Learning PromptEngineering.md](https://github.com/EgoAlpha/prompt-in-context-learning/blob/main/PromptEngineering.md)
Detailed guide to prompt engineering with code examples. Covers techniques from basic to advanced with working implementations.

---

## Key Patterns Summary

### Context Composition Hierarchy
```
System Message (persona, constraints, instructions)
├─ User Query
├─ Retrieved Context (RAG)
│  └─ Ranked & Filtered Chunks
├─ Tool Outputs (APIs, databases)
├─ Conversation History
│  └─ Summarized or Compressed
└─ Persistent Memory (vector store, database)
```

### Token Budget Allocation Strategy
1. **Reserve**: Keep 20-30% for the response
2. **System**: Allocate 5-10% for system prompt
3. **Context**: Use 40-60% for retrieved/composed context
4. **History**: Keep remaining for conversation history

### Memory Formation Patterns
- **Hot Path**: Save memories during conversations (immediate impact)
- **Background**: Form memories between interactions (deeper analysis)
- **Hybrid**: Combine both for balance of responsiveness and comprehensiveness

### Context Management Techniques
1. **Chunking**: Split documents with strategic overlap at boundaries
2. **Filtering**: Remove low-relevance or duplicate information
3. **Ranking**: Order context by relevance to query
4. **Compression**: Summarize or abstract less critical information
5. **Caching**: Store frequently-accessed context in memory cache

### Memory Hierarchy
- **Short-Term**: Last 5-9 interactions in context window
- **Working**: Current conversation and retrieved context
- **Long-Term**: Vector store or database with semantic indexing
- **Cache**: Fast-access memory for frequently-used context

---

## Performance Considerations

- **Latency**: Context size significantly impacts latency (7x increase observed at 15,000 words)
- **Cost**: Input tokens cost less than output tokens; leverage input token caching when available
- **Quality**: Relevant context > larger context; focus on precision, not volume
- **Coherence**: Balance rich context with clarity; excessive context can confuse the model

---

## Implementation Frameworks

- **LangChain**: Middleware hooks for context composition and memory management
- **LiteLLM**: Cross-model token counting and cost tracking
- **Pinecone**: Vector storage and semantic retrieval for RAG
- **Redis**: Fast memory caching and context management
- **Mem0**: Advanced memory management for LLM applications

---

*Last updated: 2026-04-05*
