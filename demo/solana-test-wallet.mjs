// A Solana wallet for the recorder: a Wallet Standard wallet put into the pages of a Playwright browser context, the
// way a wallet extension puts itself into every page. It registers with the standard's two window events, shares one
// account, and takes "solana:signAndSendTransaction". The transaction it is handed goes to the recorder (Node), which
// signs and sends it; the key never enters the page.
//
//   await installTestWallet(context, {
//     address,                       // the account the wallet shares, in Base58
//     chain: "solana:devnet",        // the one chain it offers
//     send: async (base64) => "...", // signs and sends the serialized transaction; returns the signature in Base58
//   });
import { getBase58Encoder } from "@solana/kit";

const ICON = "data:image/svg+xml;base64," + Buffer.from(
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" rx="7" fill="#1b1f2a"/><path d="M8 12h16M8 16h16M8 20h10" stroke="#19fb9b" stroke-width="2.4" stroke-linecap="round"/></svg>',
).toString("base64");

export async function installTestWallet(context, { address, chain, send, name = "Demo wallet (devnet key)" }) {
  const toBase64 = (bytes) => Buffer.from(bytes).toString("base64");
  await context.exposeFunction("__solanaTestWalletSend", async (transaction) => {
    const signature = await send(transaction);
    return toBase64(getBase58Encoder().encode(signature));
  });
  await context.addInitScript(walletInPage, { address, publicKey: toBase64(getBase58Encoder().encode(address)), chain, name, icon: ICON });
}

/** Runs in the page before its own scripts. Registration follows @wallet-standard/wallet's registerWallet. */
function walletInPage(settings) {
  const fromBase64 = (text) => Uint8Array.from(atob(text), (c) => c.charCodeAt(0));
  const account = Object.freeze({
    address: settings.address,
    publicKey: fromBase64(settings.publicKey),
    chains: Object.freeze([settings.chain]),
    features: Object.freeze(["solana:signAndSendTransaction"]),
  });
  let accounts = Object.freeze([]);
  const wallet = Object.freeze({
    version: "1.0.0",
    name: settings.name,
    icon: settings.icon,
    chains: Object.freeze([settings.chain]),
    get accounts() {
      return accounts;
    },
    features: Object.freeze({
      "standard:connect": Object.freeze({
        version: "1.0.0",
        connect: async () => {
          accounts = Object.freeze([account]);
          return { accounts };
        },
      }),
      "standard:events": Object.freeze({ version: "1.0.0", on: () => () => {} }),
      "solana:signAndSendTransaction": Object.freeze({
        version: "1.0.0",
        supportedTransactionVersions: Object.freeze(["legacy", 0]),
        signAndSendTransaction: async (...inputs) => {
          const outputs = [];
          for (const input of inputs) {
            if (input.account !== account) throw new Error("This wallet does not hold that account.");
            if (input.chain !== settings.chain) throw new Error(`This wallet is on ${settings.chain}, not ${input.chain}.`);
            let binary = "";
            for (const byte of input.transaction) binary += String.fromCharCode(byte);
            outputs.push({ signature: fromBase64(await window.__solanaTestWalletSend(btoa(binary))) });
          }
          return outputs;
        },
      }),
    }),
  });

  class RegisterWalletEvent extends Event {
    #detail;
    constructor(callback) {
      super("wallet-standard:register-wallet", { bubbles: false, cancelable: false, composed: false });
      this.#detail = callback;
    }
    get detail() {
      return this.#detail;
    }
  }
  const callback = ({ register }) => register(wallet);
  window.dispatchEvent(new RegisterWalletEvent(callback));
  window.addEventListener("wallet-standard:app-ready", ({ detail: api }) => callback(api));
}
