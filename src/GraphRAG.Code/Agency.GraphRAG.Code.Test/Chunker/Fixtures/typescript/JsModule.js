const path = require("node:path");

export default function build(name) {
  return path.join("root", name);
}

export const format = (value) => value.trim();
