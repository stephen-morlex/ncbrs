import '@testing-library/jest-dom/vitest'

/**
 * jsdom implements neither the Pointer Capture API nor scrollIntoView, and
 * radix builds its menus and selects on both. Without these, opening a Select
 * throws — so a form that works in a browser fails in a test for reasons that
 * have nothing to do with the form.
 *
 * Stubs rather than implementations: nothing here asserts on capture or
 * scrolling, it only has to not be absent.
 */
if (!Element.prototype.hasPointerCapture) {
  Element.prototype.hasPointerCapture = () => false
}

if (!Element.prototype.setPointerCapture) {
  Element.prototype.setPointerCapture = () => {}
}

if (!Element.prototype.releasePointerCapture) {
  Element.prototype.releasePointerCapture = () => {}
}

if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = () => {}
}
