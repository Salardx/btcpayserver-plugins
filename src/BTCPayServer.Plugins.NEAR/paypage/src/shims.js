// Injected by esbuild: references to the Node globals `Buffer` and `process` anywhere in the bundled wallet code
// resolve to these. The wallet modules call Buffer.from(...) for base64, and a nested @near-js/accounts reads
// process.env at module load, which would otherwise throw "process is not defined" before the page script runs.
export { Buffer } from "buffer";

export const process = {
  browser: true,
  env: {},
  versions: {},
  nextTick(callback, ...args) {
    setTimeout(() => callback(...args), 0);
  },
};
