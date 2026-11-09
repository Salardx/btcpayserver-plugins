// Runs the built bundle in a bare V8 context (node:vm) that has only what a browser page has: no Node globals, a
// stub document with the two elements the page provides, a localStorage, no network. No wallet can open here, but
// the load path and the first render run for real: the shims for Buffer and process, the slim zod alias, the
// wallet selector's setup with the four modules, the wallet list, the expiry check and the redirect parameters.
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const bundle = readFileSync(resolve(here, "..", "Resources", "nearpay", "app.js"), "utf8");
const noop = () => {};

class FakeElement {
  constructor(tag) {
    this.tagName = tag.toUpperCase();
    this.children = [];
    this.className = "";
    this.textContent = "";
    this.dataset = {};
    this.style = {};
  }
  append(...nodes) {
    this.children.push(...nodes);
  }
  appendChild(node) {
    this.children.push(node);
    return node;
  }
  replaceChildren(...nodes) {
    this.children = nodes;
  }
  addEventListener() {}
  removeEventListener() {}
  setAttribute(name, value) {
    this[name] = value;
  }
}

// All text under a node, the way textContent would read it.
function textOf(node) {
  if (typeof node === "string") return node;
  return node.textContent || node.children.map(textOf).join("");
}

function buttonsIn(mount) {
  const found = [];
  const walk = (node) => {
    if (typeof node === "string") return;
    if (node.tagName === "BUTTON") found.push(node);
    node.children.forEach(walk);
  };
  walk(mount);
  return found;
}

function invoice(overrides = {}) {
  return {
    invoice: "INV123",
    method: "NEAR-CHAIN",
    destination: "store.testnet",
    units: "1500000000000000000000000",
    amount: "1.5",
    currency: "NEAR",
    token: "",
    decimals: "24",
    network: "testnet",
    rpc: "https://rpc.invalid/",
    statusUrl: "/i/INV123/status",
    checkoutUrl: "/i/INV123",
    expires: String(Math.floor(Date.now() / 1000) + 900),
    ...overrides,
  };
}

async function scenario({ dataset, search = "" }) {
  const root = dataset ? Object.assign(new FakeElement("div"), { dataset }) : null;
  const mount = dataset ? new FakeElement("div") : null;
  const storage = new Map();
  const result = { messages: [], warnings: [], replacedUrl: null, mount, error: null, ready: false };
  const sandbox = {
    console: {
      log: noop,
      debug: noop,
      info: noop,
      warn: (...args) => result.warnings.push(args.map(String).join(" ")),
      error: (...args) => result.warnings.push(args.map(String).join(" ")),
    },
    setTimeout,
    clearTimeout,
    setInterval,
    clearInterval,
    queueMicrotask,
    structuredClone,
    TextEncoder,
    TextDecoder,
    URL,
    URLSearchParams,
    atob,
    btoa,
    crypto: globalThis.crypto,
    fetch: () => Promise.reject(new Error("the smoke test has no network")),
    addEventListener: noop,
    removeEventListener: noop,
    postMessage: noop,
    open: () => null,
    innerWidth: 1280,
    innerHeight: 800,
    screen: { width: 1280, height: 800 },
    navigator: { userAgent: "node smoke test" },
    location: new URL(`http://localhost/plugins/near/pay/INV123/NEAR-CHAIN${search}`),
    history: {
      replaceState: (_state, _title, url) => {
        result.replacedUrl = url;
      },
    },
    localStorage: {
      getItem: (key) => (storage.has(key) ? storage.get(key) : null),
      setItem: (key, value) => storage.set(key, String(value)),
      removeItem: (key) => storage.delete(key),
    },
    document: {
      all: undefined,
      body: new FakeElement("body"),
      addEventListener: noop,
      removeEventListener: noop,
      createElement: (tag) => new FakeElement(tag),
      getElementById: (id) => (id === "near-pay" ? root : id === "near-wallet-mount" ? mount : null),
      querySelectorAll: () => [],
    },
    nearPayShow: (kind, text) => result.messages.push({ kind, text }),
  };
  sandbox.window = sandbox;
  sandbox.self = sandbox;
  const context = vm.createContext(sandbox);
  try {
    vm.runInContext(bundle, context, { filename: "app.js" });
  } catch (error) {
    result.error = error;
    return result;
  }
  const deadline = Date.now() + 5000;
  while (!sandbox.nearPayWalletsReady && Date.now() < deadline) {
    await new Promise((done) => setTimeout(done, 20));
  }
  result.ready = sandbox.nearPayWalletsReady === true;
  return result;
}

const expectedWallets = ["MyNearWallet", "Meteor Wallet", "HOT Wallet", "Nightly"];
const failures = [];
function check(name, condition, detail) {
  if (!condition) failures.push(`${name}: ${detail}`);
}

{
  const r = await scenario({ dataset: null });
  check("load without the page", !r.error, `the bundle threw while loading: ${r.error && r.error.stack}`);
  check("load without the page", !r.ready, "the app ran its page logic without a #near-pay element");
}
{
  const r = await scenario({ dataset: invoice() });
  check("fresh NEAR invoice", !r.error, `threw: ${r.error && r.error.stack}`);
  check("fresh NEAR invoice", r.ready, "nearPayWalletsReady was not set");
  const names = buttonsIn(r.mount).map(textOf);
  check("fresh NEAR invoice", JSON.stringify(names) === JSON.stringify(expectedWallets), `wallets rendered: ${JSON.stringify(names)}`);
  check("fresh NEAR invoice", r.messages.length === 0, `unexpected status messages: ${JSON.stringify(r.messages)}`);
}
{
  const r = await scenario({ dataset: invoice({ token: "usdc.fakes.testnet", currency: "USDC", decimals: "6", units: "1500000", amount: "1.5" }) });
  check("token invoice", !r.error, `threw: ${r.error && r.error.stack}`);
  check("token invoice", r.ready, "nearPayWalletsReady was not set");
  check("token invoice", buttonsIn(r.mount).length === 4, `wallets rendered: ${buttonsIn(r.mount).length}`);
  await new Promise((done) => setTimeout(done, 50)); // the registration lookup fails without network
  check("token invoice", r.warnings.some((w) => w.includes("storage_balance_of")), `the failed registration lookup was not reported: ${JSON.stringify(r.warnings)}`);
}
{
  const r = await scenario({ dataset: invoice({ expires: String(Math.floor(Date.now() / 1000) - 60) }) });
  check("expired invoice", !r.error, `threw: ${r.error && r.error.stack}`);
  check("expired invoice", r.ready, "nearPayWalletsReady was not set");
  check("expired invoice", r.mount.children.length === 0, "the mount still has content");
  check("expired invoice", r.messages.length === 1 && r.messages[0].kind === "warning" && r.messages[0].text === "This invoice has expired", `messages: ${JSON.stringify(r.messages)}`);
}
{
  const r = await scenario({ dataset: invoice(), search: "?transactionHashes=8N4f&other=1" });
  check("redirect with hashes", !r.error, `threw: ${r.error && r.error.stack}`);
  check("redirect with hashes", r.messages.some((m) => m.kind === "success" && m.text.startsWith("Transaction sent")), `messages: ${JSON.stringify(r.messages)}`);
  check("redirect with hashes", r.replacedUrl === "/plugins/near/pay/INV123/NEAR-CHAIN?other=1", `url after cleanup: ${r.replacedUrl}`);
  check("redirect with hashes", buttonsIn(r.mount).length === 4, "the wallets were not rendered");
}
{
  const r = await scenario({ dataset: invoice(), search: "?errorCode=userRejected&errorMessage=User%2520rejected%2520transaction" });
  check("redirect with error", !r.error, `threw: ${r.error && r.error.stack}`);
  check("redirect with error", r.messages.some((m) => m.kind === "warning" && m.text.includes("User rejected transaction")), `messages: ${JSON.stringify(r.messages)}`);
}

if (failures.length) {
  console.error("Smoke test failed:");
  for (const failure of failures) console.error(" - " + failure);
  process.exit(1);
}
console.log(`Smoke test passed: the bundle (${(bundle.length / 1024).toFixed(0)} KB) loads and renders the four wallets without a browser.`);
