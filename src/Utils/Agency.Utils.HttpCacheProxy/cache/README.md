# Functional-test response cache

The `*.json` blobs in this directory are the **persistent HTTP response cache** for
`Agency.Utils.HttpCacheProxy`, checked in on purpose so the CI functional-test step serves every
LLM / embedding request **offline** (no LM Studio upstream). Each blob is one cached response,
keyed by `SHA256("{Method}|{PathAndQuery}|{SHA256(body)}")` with a base64 body; entries loaded
from disk never expire.

**Do not hand-edit the blobs.** To regenerate them, understand **why cache misses happen**, or
add a new LLM-calling functional test, see the hand-off document:

→ [`docs/HTTP-Cache-Proxy-Handoff.md`](../../../../docs/HTTP-Cache-Proxy-Handoff.md)

Quick reminder of the one operational gotcha: **always restart the proxy after clearing this
directory** — a running proxy serves deleted entries from its in-memory tier (a hit) and never
re-persists them, silently producing an incomplete cache.
