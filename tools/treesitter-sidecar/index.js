"use strict";

const readline = require("readline");
const Parser = require("tree-sitter");
const csharp = require("tree-sitter-c-sharp");
const javascript = require("tree-sitter-javascript");
const python = require("tree-sitter-python");
const typescript = require("tree-sitter-typescript");

const languages = new Map([
  ["csharp", csharp],
  ["javascript", javascript],
  ["jsx", javascript],
  ["python", python],
  ["typescript", typescript.typescript],
  ["tsx", typescript.tsx]
]);

const parser = new Parser();

function toPoint(point) {
  return {
    row: point.row,
    column: point.column
  };
}

function serializeNode(node, fieldName) {
  const result = {
    type: node.type,
    named: node.isNamed,
    startIndex: node.startIndex,
    endIndex: node.endIndex,
    startPosition: toPoint(node.startPosition),
    endPosition: toPoint(node.endPosition),
    children: []
  };

  if (fieldName) {
    result.fieldName = fieldName;
  }

  for (let i = 0; i < node.childCount; i += 1) {
    const child = node.child(i);
    result.children.push(serializeNode(child, node.fieldNameForChild(i) || undefined));
  }

  return result;
}

function successResponse(request, tree) {
  return {
    ok: true,
    file: request.file,
    language: request.language,
    ast: serializeNode(tree.rootNode)
  };
}

function errorResponse(request, code, message) {
  return {
    ok: false,
    file: request && typeof request.file === "string" ? request.file : null,
    language: request && typeof request.language === "string" ? request.language : null,
    error: {
      code,
      message
    }
  };
}

function validateRequest(request) {
  if (!request || typeof request !== "object" || Array.isArray(request)) {
    throw new Error("Request must be a JSON object.");
  }

  if (typeof request.file !== "string" || request.file.length === 0) {
    throw new Error("Request field 'file' must be a non-empty string.");
  }

  if (typeof request.language !== "string" || request.language.length === 0) {
    throw new Error("Request field 'language' must be a non-empty string.");
  }

  if (typeof request.source !== "string") {
    throw new Error("Request field 'source' must be a string.");
  }
}

function parseLine(line) {
  let request;

  try {
    request = JSON.parse(line);
  } catch (error) {
    return errorResponse(null, "invalid_json", error.message);
  }

  try {
    validateRequest(request);
  } catch (error) {
    return errorResponse(request, "invalid_request", error.message);
  }

  const language = languages.get(request.language);
  if (!language) {
    return errorResponse(
      request,
      "unsupported_language",
      `Unsupported language '${request.language}'.`
    );
  }

  try {
    parser.setLanguage(language);
    const tree = parser.parse(request.source);
    return successResponse(request, tree);
  } catch (error) {
    return errorResponse(request, "parse_failed", error.message);
  }
}

const rl = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity,
  terminal: false
});

rl.on("line", (line) => {
  if (line.trim().length === 0) {
    return;
  }

  process.stdout.write(`${JSON.stringify(parseLine(line))}\n`);
});

rl.on("close", () => {
  process.exit(0);
});
