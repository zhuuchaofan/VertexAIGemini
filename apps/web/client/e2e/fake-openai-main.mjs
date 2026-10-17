import { startFakeOpenAI } from "./fake-openai.mjs";

await startFakeOpenAI(Number(process.env.FAKE_OPENAI_PORT ?? 5099));
console.log("fake openai listening");
setInterval(() => {}, 1 << 30); // stay alive
