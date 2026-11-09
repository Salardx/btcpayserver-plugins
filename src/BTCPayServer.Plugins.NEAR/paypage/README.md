# Pay page script

The browser side of the NEAR plugin's pay page (`Views/UINEARPay/Pay.cshtml`): it lists NEAR wallets through the
[NEAR Wallet Selector](https://github.com/near/wallet-selector) (MyNearWallet, Meteor, HOT, Nightly), connects one,
and sends the invoice's transfer: a plain NEAR transfer, or `ft_transfer` on the NEP-141 contract with a
`storage_deposit` first when the store's account is not registered with the token yet. The invoice id goes in the memo.

The built bundle is committed at `../Resources/nearpay/app.js` and embedded in the plugin assembly, so the .NET build
does not need Node. Rebuild it after changing `src/app.js` or the pinned package versions:

```sh
npm ci
npm run build
```

`node build.mjs --analyze` prints what the bundle is made of. Node 24 and npm 11 were used; esbuild is pinned in
`package.json`, as are the wallet-selector packages (all at the same version).
