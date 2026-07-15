import "@testing-library/jest-dom/vitest";

// jsdom does not implement the <dialog> interaction methods the app uses.
if (typeof HTMLDialogElement !== "undefined") {
  HTMLDialogElement.prototype.show ??= function (this: HTMLDialogElement) { this.open = true; };
  HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true; };
  HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement, returnValue?: string) {
    if (returnValue !== undefined) this.returnValue = returnValue;
    this.open = false;
    this.dispatchEvent(new Event("close"));
  };
}