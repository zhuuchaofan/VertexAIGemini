import { test as base, expect } from "@playwright/test";

/**
 * Every test is its own person: a fresh development user (so no shared conversations, settings or limits) and its own
 * address for the rate limiter, as if the tests were run by different people.
 */
export const test = base.extend<{ user: string }>({
  // eslint-disable-next-line no-empty-pattern
  user: async ({}, use, info) => {
    await use(`e2e-${info.workerIndex}-${Date.now().toString(36)}-${Math.floor(Math.random() * 1e6).toString(36)}`);
  },
  context: async ({ context, user }, use) => {
    await context.addInitScript(name => {
      try {
        localStorage.setItem("e2e:user", name);
      } catch {
        // storage unavailable
      }
    }, user);
    await context.setExtraHTTPHeaders({ "x-forwarded-for": `10.${Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250) + 1}` });
    await use(context);
  }
});

export { expect };
export const userHeader = (user: string) => ({ "x-dev-user": user });
