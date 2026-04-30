import { format } from "node:util";

export class Worker {
  async run(task: string): Promise<string> {
    const parts = [task, format("done")];
    const message = parts.join(":");
    return message.trim();
  }
}

export function buildMessage(name: string): string {
  const greeting = `hello ${name}`;
  return greeting.toUpperCase();
}

export const formatUser = (name: string): string => {
  const normalized = name.trim();
  return normalized.toLowerCase();
};
