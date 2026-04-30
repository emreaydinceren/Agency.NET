export interface Worker extends Disposable, Runner<TaskContext> {
  run(task: string): Promise<void>;
}

export abstract class WorkerBase implements Worker {
  abstract run(task: string): Promise<void>;
}

export class Service extends WorkerBase implements Worker, Disposable {
  run(task: string): Promise<void> {
    return Promise.resolve();
  }
}
