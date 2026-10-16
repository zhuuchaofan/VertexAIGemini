// Minimal DOM stand-in so DOM-building modules can run under node:test without dependencies.
class FakeNode {
  constructor(tag, text = "") {
    this.tag = tag;
    this.text = text;
    this.children = [];
    this.dataset = {};
    this.attrs = {};
  }
  append(...nodes) {
    this.children.push(...nodes);
  }
  set textContent(value) {
    this.children = [];
    this.text = value;
  }
  get textContent() {
    return this.text + this.children.map(child => child.textContent).join("");
  }
  // Compact HTML-ish serialisation for assertions.
  toString() {
    if (this.tag === "#text") return this.text;
    if (this.tag === "br") return "<br>";
    const attrs = Object.entries({ ...this.attrs, ...(this.href ? { href: this.href } : {}) })
      .map(([k, v]) => ` ${k}="${v}"`).join("");
    const lang = this.dataset.language ? ` data-language="${this.dataset.language}"` : "";
    return `<${this.tag}${attrs}${lang}>${this.text}${this.children.join("")}</${this.tag}>`;
  }
}

export function installFakeDom() {
  globalThis.document = {
    createElement: tag => new FakeNode(tag),
    createTextNode: text => new FakeNode("#text", text)
  };
  globalThis.window = { location: { href: "https://app.example/" } };
}
