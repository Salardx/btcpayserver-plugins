// Stands in for Node's `util` module: `generate-function` (reached through @near-js/accounts' ABI validator) requires
// it for `util.format`, and that is the only thing anything in this bundle asks of it.
export function format(template, ...args) {
  let i = 0;
  const text = String(template).replace(/%[sdifjoO%]/g, (token) => {
    if (token === "%%") return "%";
    if (i >= args.length) return token;
    const value = args[i++];
    switch (token) {
      case "%d":
      case "%i":
        return String(parseInt(value, 10));
      case "%f":
        return String(parseFloat(value));
      case "%j":
        return JSON.stringify(value);
      default:
        return typeof value === "string" ? value : JSON.stringify(value) ?? String(value);
    }
  });
  const rest = args.slice(i).map((value) => (typeof value === "string" ? value : JSON.stringify(value) ?? String(value)));
  return [text, ...rest].join(" ");
}

export default { format };
