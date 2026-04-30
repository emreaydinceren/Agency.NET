# tree-sitter sidecar

Node.js sidecar for GraphRAG.Code tree-sitter parsing.

## Install

```bash
cd tools/treesitter-sidecar
npm install
```

## Run

```bash
node index.js
```

The process reads one JSON request per line from stdin and writes one JSON response per line to stdout.

Request shape:

```json
{"file":"sample.py","language":"python","source":"def add(a, b):\n    return a + b\n"}
```

Success response shape:

```json
{"ok":true,"file":"sample.py","language":"python","ast":{"type":"module","named":true,"startIndex":0,"endIndex":32,"startPosition":{"row":0,"column":0},"endPosition":{"row":2,"column":0},"children":[{"type":"function_definition","named":true,"startIndex":0,"endIndex":31,"startPosition":{"row":0,"column":0},"endPosition":{"row":1,"column":16},"children":[]}]}}
```

Error response shape:

```json
{"ok":false,"file":"sample.py","language":"python","error":{"code":"unsupported_language","message":"Unsupported language 'ruby'."}}
```

Supported languages:

- `csharp`
- `typescript`
- `javascript`
- `python`

## Notes

- Protocol is JSON Lines so a future .NET client can keep the process alive and stream requests.
- Responses preserve request ordering.
- AST nodes include type, named flag, byte offsets, row/column positions, optional field name, and recursive children.
