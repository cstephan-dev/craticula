import { describe, expect, it } from 'vitest';
import { errorHeading } from './errorHeading';

describe('errorHeading', () => {
  it('says plainly that an unsupported shape is not supported on this provider', () => {
    expect(errorHeading('unsupported-shape')).toBe('Not supported on this provider');
  });

  it('keeps the machine kind for every other error', () => {
    expect(errorHeading('backend-unavailable')).toBe('backend-unavailable');
    expect(errorHeading('unknown-field')).toBe('unknown-field');
  });
});
