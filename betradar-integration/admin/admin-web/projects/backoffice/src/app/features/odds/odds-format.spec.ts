import { drift, expiresIn, fmtOdds, fmtPct, parseOdds, reasonLabel } from './odds-format';

describe('odds formatting', () => {
  it('shows decimal odds with a dot and two decimals', () => {
    expect(fmtOdds(2.5)).toBe('2.50');
    expect(fmtOdds(null)).toBe('—');
    expect(fmtPct(0.0481)).toBe('4.81%');
    expect(drift(2.1, 2.06)).toBeCloseTo(-0.019, 3);
  });

  it('parses simulator input with or without outcome codes', () => {
    expect(parseOdds('2.10 3.40 3.60')).toEqual([{ code: '1', odds: 2.1 }, { code: '2', odds: 3.4 }, { code: '3', odds: 3.6 }]);
    expect(parseOdds('1=2.10; X=3.4')).toEqual([{ code: '1', odds: 2.1 }, { code: 'X', odds: 3.4 }]);
    expect(parseOdds('2.10 0.9')).toBeNull();
    expect(parseOdds('  ')).toBeNull();
  });

  it('explains expiries and restriction reasons', () => {
    const now = Date.parse('2026-10-05T12:00:00Z');
    expect(expiresIn('2026-10-05T12:30:00Z', now)).toBe('in 30 min');
    expect(expiresIn('2026-10-05T18:00:00Z', now)).toBe('in 6 h');
    expect(expiresIn('2026-10-05T11:00:00Z', now)).toBe('expired');
    expect(expiresIn(null, now)).toBe('until lifted');
    expect(reasonLabel('trading:suspend')).toBe('suspended by a trader');
    expect(reasonLabel('feed:deactivated')).toBe('feed: deactivated');
  });
});
