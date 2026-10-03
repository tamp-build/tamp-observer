import { mount } from "svelte";
import App from "./App.svelte";
import { installClientObservability } from "./lib/observability";
import { installClientSink } from "./lib/sink";

// Install the intrusive client observability adapter once, before anything runs. From here on every fetch/XHR
// failure, unhandled error and rejection, and resource load failure is captured automatically.
installClientObservability();
// Forward captured errors to the backend (when a client project key is configured). Fire and forget.
void installClientSink();

const app = mount(App, { target: document.getElementById("app")! });

export default app;
