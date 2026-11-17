// The Solana pay page's browser script, run outside a browser: node:vm with a stub page, a stub Wallet Standard
// wallet and a stub server. It proves the script's own logic without a browser, a network or a dependency.
//
//   node smoke-solana-paypage.mjs
//
// What a browser does with the markup is not tested here; demo/record-solana.mjs records the real thing.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const script = readFileSync(join(here, "../src/BTCPayServer.Plugins.Solana/Resources/solanapay/app.js"), "utf8");

const SHOP = "8giHjS28zBrkBtmBfG183XkWAJyPyJuv84wju7pysNQm";
const PAYER = "5SChe7cEb69tr6L8puLEVqp6RnrJSUnjHfX4fRPEXaJq";
const REFERENCE = "7GQaGbPQ42EzftXrsGAfMoLgHBmevBaptTpD9t5rRfVN";

/** Just enough of an element for what the page's script touches. */
function element(tag) {
    const node = {
        tagName: tag,
        className: "",
        children: [],
        attributes: {},
        dataset: {},
        disabled: false,
        listeners: {},
        appendChild(child) {
            node.children.push(child);
            return child;
        },
        addEventListener(name, handler) {
            (node.listeners[name] ||= []).push(handler);
        },
        setAttribute(name, value) {
            node.attributes[name] = String(value);
        },
        removeAttribute(name) {
            delete node.attributes[name];
        },
        getAttribute(name) {
            return name in node.attributes ? node.attributes[name] : null;
        },
        click() {
            (node.listeners.click || []).forEach((handler) => handler());
        },
        get text() {
            return [node.ownText, ...node.children.map((child) => child.text || "")].filter(Boolean).join("");
        },
    };
    node.ownText = "";
    Object.defineProperty(node, "textContent", {
        get: () => node.ownText,
        set: (value) => {
            node.ownText = String(value);
            node.children = [];
        },
    });
    return node;
}

/** The page the script is given, and the levers a test needs on it. */
function page(settings) {
    const root = element("div");
    Object.assign(root.dataset, settings);
    const status = element("div");
    const wallets = element("div");
    const timers = [];
    const requests = [];
    let answerStatus = (url) => ({ ok: true, body: { status: "new" } });
    let answerTransaction = () => ({ ok: true, body: { transaction: Buffer.from([1, 2, 3]).toString("base64"), amount: "0.05", currency: "SOL", lastValidBlockHeight: 1000 } });
    let answerSent = () => ({ ok: true, body: { state: "onItsWay" } });

    const window_ = {
        listeners: {},
        addEventListener(name, handler) {
            (window_.listeners[name] ||= []).push(handler);
        },
        dispatchEvent(event) {
            (window_.listeners[event.type] || []).forEach((handler) => handler(event));
            return true;
        },
    };

    const sandbox = {
        window: window_,
        navigator: {},
        console,
        CustomEvent: class {
            constructor(type, init) {
                this.type = type;
                this.detail = init && init.detail;
            }
        },
        Event: class {
            constructor(type) {
                this.type = type;
            }
        },
        atob: (text) => Buffer.from(text, "base64").toString("binary"),
        Uint8Array,
        Promise,
        setTimeout(handler, delay) {
            timers.push({ handler, delay });
            return timers.length;
        },
        document: {
            getElementById: (id) => ({ "solana-pay": root, "solana-pay-status": status, "solana-pay-wallets": wallets }[id] || null),
            createElement: element,
            createTextNode: (value) => ({ text: String(value) }),
            querySelectorAll: () => [],
        },
        fetch(url, options) {
            requests.push({ url, options });
            const answer = url.startsWith(settings.transactionUrl) ? answerTransaction(url, options)
                : url.startsWith(settings.sentUrl) ? answerSent(url)
                    : answerStatus(url);
            return Promise.resolve({
                ok: answer.ok !== false,
                status: answer.status || (answer.ok === false ? 409 : 200),
                json: () => Promise.resolve(answer.body),
            });
        },
    };
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);

    return {
        root, status, wallets, requests, window: window_, sandbox,
        run: () => vm.runInContext(script, sandbox),
        /**
         * Lets the script get on with it: the promises it is waiting on are drained, then whatever it asked to run
         * later is run, round after round. The page keeps asking after the invoice, so there is always something
         * scheduled; the rounds are what ends this, not an empty queue.
         */
        async settle(rounds = 25) {
            for (let i = 0; i < rounds; i++) {
                for (let drain = 0; drain < 8; drain++) {
                    await new Promise((resolve) => setImmediate(resolve));
                }
                timers.splice(0, timers.length).forEach((timer) => timer.handler());
            }
        },
        set status_(answer) { answerStatus = answer; },
        set transaction_(answer) { answerTransaction = answer; },
        set sent_(answer) { answerSent = answer; },
    };
}

const SETTINGS = {
    invoice: "INV1",
    method: "SOL-SOLANA",
    amount: "0.05",
    currency: "SOL",
    network: "devnet",
    chain: "solana:devnet",
    explorer: "https://explorer.solana.com/tx/{0}?cluster=devnet",
    statusUrl: "/i/INV1/status",
    transactionUrl: "/plugins/solana/pay/INV1/SOL-SOLANA/transaction",
    sentUrl: "/plugins/solana/pay/INV1/SOL-SOLANA/sent",
    checkoutUrl: "/i/INV1",
};

/** A Wallet Standard wallet that registers the way an extension does, and hands back a signature. */
function wallet(context, { chain = "solana:devnet", name = "Stub wallet", signature = new Uint8Array(64).fill(7), fails = null, onSign = null } = {}) {
    const account = { address: PAYER, publicKey: new Uint8Array(32), chains: [chain], features: ["solana:signAndSendTransaction"] };
    const sent = [];
    const theWallet = {
        version: "1.0.0",
        name,
        icon: "data:image/svg+xml;base64,AAA=",
        chains: [chain],
        accounts: [],
        features: {
            "standard:connect": { version: "1.0.0", connect: async () => ({ accounts: [account] }) },
            "solana:signAndSendTransaction": {
                version: "1.0.0",
                supportedTransactionVersions: ["legacy", 0],
                signAndSendTransaction: async (input) => {
                    sent.push(input);
                    if (onSign) onSign();
                    if (fails) throw new Error(fails);
                    return [{ signature }];
                },
            },
        },
    };
    const register = (api) => api.register(theWallet);
    context.window.dispatchEvent({ type: "wallet-standard:register-wallet", detail: register });
    return { wallet: theWallet, sent, account };
}

const checks = [];
async function check(what, body) {
    try {
        await body();
        checks.push(true);
        console.log("  ok  ", what);
    } catch (error) {
        checks.push(false);
        console.log("  FAIL", what, "\n       ", String(error).split("\n").slice(0, 4).join("\n        "));
    }
}

/** Every button the page is offering right now. */
const buttons = (context) => context.wallets.children.filter((child) => child.tagName === "button");

await check("a wallet on this chain is offered, and one on another chain is named with the reason", async () => {
    const context = page(SETTINGS);
    context.run();
    wallet(context, { name: "Good wallet" });
    wallet(context, { chain: "solana:mainnet", name: "Mainnet wallet" });
    await context.settle();
    assert.equal(buttons(context).length, 1);
    assert.equal(buttons(context)[0].text, "Pay with Good wallet");
    assert.match(context.wallets.text, /Mainnet wallet was found, but it does not offer Solana devnet\./);
    assert.equal(context.root.getAttribute("data-wallets"), "1");
});

await check("a wallet of another ecosystem is not listed at all", async () => {
    const context = page(SETTINGS);
    context.run();
    const other = { version: "1.0.0", name: "Ethereum wallet", icon: "data:image/svg+xml;base64,AAA=", chains: ["eip155:1"], accounts: [], features: {} };
    context.window.dispatchEvent({ type: "wallet-standard:register-wallet", detail: (api) => api.register(other) });
    await context.settle();
    assert.equal(context.wallets.text, "No Solana wallet was found in this browser. Open this page in a browser that has a Solana wallet extension, or in the browser of a wallet app, or pay with the wallet link below.");
});

await check("paying posts the connected account, signs what the server built, and shows the signature", async () => {
    const context = page(SETTINGS);
    context.run();
    // Two bytes that are not the same, so a wrong Base58 encoder cannot pass by accident.
    const signature = Uint8Array.from({ length: 64 }, (_, i) => (i * 7 + 3) % 256);
    const stub = wallet(context, { signature });
    await context.settle();
    buttons(context)[0].click();
    await context.settle();

    const posted = context.requests.filter((r) => r.url === SETTINGS.transactionUrl);
    assert.equal(posted.length, 1);
    assert.deepEqual(JSON.parse(posted[0].options.body), { account: PAYER });
    assert.equal(stub.sent.length, 1);
    assert.equal(stub.sent[0].chain, "solana:devnet");
    assert.deepEqual([...stub.sent[0].transaction], [1, 2, 3]);
    assert.equal(context.root.getAttribute("data-sent"), base58(Buffer.from(signature)));
    assert.equal(buttons(context)[0].disabled, true);
});

await check("the signature it shows is the wallet's bytes in Base58", async () => {
    const context = page(SETTINGS);
    context.run();
    // Base58 of 64 bytes 0x01..0x40, worked out independently (the same alphabet Solana uses).
    const bytes = Uint8Array.from({ length: 64 }, (_, i) => i + 1);
    wallet(context, { signature: bytes });
    await context.settle();
    buttons(context)[0].click();
    await context.settle();
    const expected = base58(Buffer.from(bytes));
    assert.equal(context.root.getAttribute("data-sent"), expected);
    assert.match(context.status.text, new RegExp(expected));
});

await check("the amount it asks the customer to approve is the server's, not the one the page was loaded with", async () => {
    const context = page(SETTINGS);
    context.run();
    // A payment on another method changed what is due between the page being loaded and the button being clicked.
    context.transaction_ = () => ({ ok: true, body: { transaction: "AQID", amount: "0.07", currency: "SOL", lastValidBlockHeight: 10 } });
    let askedToApprove = null;
    wallet(context, { fails: "the customer closed the wallet", onSign: () => (askedToApprove = context.status.text) });
    await context.settle();
    buttons(context)[0].click();
    await context.settle();

    assert.equal(askedToApprove, "Approve the payment of 0.07 SOL in Stub wallet.");
    assert.match(context.status.text, /No payment was confirmed\. the customer closed the wallet\. You can try again\./);
    assert.equal(buttons(context)[0].disabled, false, "the button works again");
});

await check("a refusal from the server is shown in its own words and nothing is marked as sent", async () => {
    const context = page(SETTINGS);
    context.run();
    context.transaction_ = () => ({ ok: false, status: 422, body: { message: "This wallet holds no SOL on Solana devnet." } });
    wallet(context);
    await context.settle();
    buttons(context)[0].click();
    await context.settle();
    assert.match(context.status.text, /No payment was confirmed\. This wallet holds no SOL on Solana devnet\. You can try again\./);
    assert.equal(context.root.getAttribute("data-sent"), null);
    assert.equal(buttons(context)[0].disabled, false);
});

await check("a payment the server says is already on its way is shown, and the page keeps watching it", async () => {
    const context = page(SETTINGS);
    context.run();
    const already = base58(Buffer.alloc(64, 9));
    context.transaction_ = () => ({ ok: false, status: 409, body: { message: "A payment for this invoice was sent already.", signature: already } });
    wallet(context);
    await context.settle();
    buttons(context)[0].click();
    await context.settle();
    assert.equal(context.root.getAttribute("data-sent"), already);
    assert.equal(buttons(context)[0].disabled, true);
    assert.ok(context.requests.some((r) => r.url.startsWith(SETTINGS.sentUrl) && r.url.includes(encodeURIComponent(already))), "it asked what became of it");
});

await check("a payment that never reached the network lets the customer pay again", async () => {
    const context = page(SETTINGS);
    context.run();
    context.sent_ = () => ({ ok: true, body: { state: "gone" } });
    wallet(context);
    await context.settle();
    buttons(context)[0].click();
    await context.settle();
    assert.equal(context.root.getAttribute("data-sent"), null, "it is not treated as sent any more");
    assert.match(context.status.text, /The payment never reached the network, so nothing was paid\. You can pay again\./);
    assert.equal(buttons(context)[0].disabled, false);
});

await check("a payment that failed on the network says so", async () => {
    const context = page(SETTINGS);
    context.run();
    context.sent_ = () => ({ ok: true, body: { state: "failed" } });
    wallet(context);
    await context.settle();
    buttons(context)[0].click();
    await context.settle();
    assert.match(context.status.text, /The payment failed on the network, so nothing was paid\./);
    assert.equal(buttons(context)[0].disabled, false);
});

await check("the page turns to paid by itself, and then offers nothing more to pay", async () => {
    const context = page(SETTINGS);
    context.run();
    wallet(context);
    await context.settle();
    context.status_ = () => ({ ok: true, body: { status: "Settled" } });
    await context.settle();
    assert.match(context.status.text, /Payment received\. Thank you\./);
    assert.equal(context.status.className, "alert alert-success");
    assert.equal(buttons(context)[0].disabled, true);
});

await check("an expired invoice is said to be expired and nothing can be paid", async () => {
    const context = page(SETTINGS);
    context.run();
    wallet(context);
    await context.settle();
    context.status_ = () => ({ ok: true, body: { status: "Expired" } });
    await context.settle();
    assert.match(context.status.text, /This invoice has expired\. Go back to the checkout for a new one\./);
    assert.equal(context.status.className, "alert alert-danger");
    assert.equal(buttons(context)[0].disabled, true);
});

/** Base58 with Bitcoin's alphabet, written here so the script's own encoder is checked against something else. */
function base58(buffer) {
    const alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    let value = BigInt("0x" + (buffer.toString("hex") || "0"));
    let text = "";
    while (value > 0n) {
        text = alphabet[Number(value % 58n)] + text;
        value /= 58n;
    }
    for (const byte of buffer) {
        if (byte !== 0) break;
        text = "1" + text;
    }
    return text || "";
}

const failed = checks.filter((ok) => !ok).length;
console.log(failed ? `${failed} of ${checks.length} checks FAILED` : `all ${checks.length} checks passed`);
process.exit(failed ? 1 : 0);
