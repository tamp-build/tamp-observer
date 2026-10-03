import { mount } from "svelte";
import App from "./App.svelte";
import { installGlobalErrorCapture } from "./lib/stores/errors.svelte";

// Capture anything that escapes a component boundary (async rejections, event handlers) before the app mounts.
installGlobalErrorCapture();

const app = mount(App, { target: document.getElementById("app")! });

export default app;
