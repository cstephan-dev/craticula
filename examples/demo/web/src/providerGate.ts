import type { Provider } from './api';

/** Which provider is on screen. Datasources of any other provider ask it before reporting a late answer. */
export function createProviderGate(initial: Provider) {
  let current = initial;
  return {
    select(next: Provider) {
      current = next;
    },
    current(): Provider {
      return current;
    },
    isCurrent(candidate: Provider) {
      return current === candidate;
    },
  };
}
