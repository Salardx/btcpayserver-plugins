// Stands in for `zod` in the bundle (see the zod-slim plugin in build.mjs). Only @meteorwallet/sdk imports it, as
// `import { z } from "zod"`, and it uses five builders plus the methods on their schemas. Importing the `z`
// namespace object keeps every one of zod's ~60 locales alive (about 250 KB minified); naming the builders lets
// esbuild drop everything else. If the SDK ever starts using another `z.*` member, the smoke test fails at load.
import { array, literal, object, string, undefined as undefinedSchema } from "zod/v4";

export const z = { array, literal, object, string, undefined: undefinedSchema };
export default z;
