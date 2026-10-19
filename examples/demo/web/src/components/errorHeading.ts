/** The kind the API answers (HTTP 400) when a provider cannot answer a request shape another provider can. */
export const UNSUPPORTED_SHAPE = 'unsupported-shape';

/**
 * The bold lead of an error shown to the user. An unsupported shape is not a failure of the provider: the page says so in plain words and
 * the API's own message (which names the shape and what to use instead) follows. Every other kind keeps its machine name.
 */
export function errorHeading(kind: string): string {
  return kind === UNSUPPORTED_SHAPE ? 'Not supported on this provider' : kind;
}
