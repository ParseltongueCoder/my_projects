/** Formatting and parsing shared by the ODDS pages. Odds are decimal with a dot: 2.50 (decision 2026-10-04). */

export function fmtOdds(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : value.toFixed(2);
}

/** Overround / margin as a percentage: 0.0481 → "4.81%". */
export function fmtPct(value: number | null | undefined, digits = 2): string {
  return value === null || value === undefined ? '—' : `${(value * 100).toFixed(digits)}%`;
}

/** Relative difference of the offer to the feed: -0.019 → "-1.9%". */
export function drift(feed: number | null, offered: number | null): number | null {
  return feed && offered ? (offered - feed) / feed : null;
}

/** "in 12 min", "in 3 h", "expired" — for TTLs and expiries. */
export function expiresIn(iso: string | null, now: number = Date.now()): string {
  if (!iso) {
    return 'until lifted';
  }
  const minutes = Math.round((new Date(iso).getTime() - now) / 60000);
  if (minutes <= 0) {
    return 'expired';
  }
  return minutes < 120 ? `in ${minutes} min` : `in ${Math.round(minutes / 60)} h`;
}

/**
 * Feed prices typed in the simulator: "2.10 3.40 3.60" (codes 1, 2, 3) or "1=2.10, X=3.40, 2=3.60".
 * Returns null when a value is not a price above 1.
 */
export function parseOdds(text: string): { code: string; odds: number }[] | null {
  const parts = text.split(/[\s,;]+/).filter((p) => p.length > 0);
  if (parts.length === 0) {
    return null;
  }
  const out: { code: string; odds: number }[] = [];
  for (const [i, part] of parts.entries()) {
    const [code, raw] = part.includes('=') ? part.split('=', 2) : [String(i + 1), part];
    const odds = Number(raw);
    if (!code || !Number.isFinite(odds) || odds <= 1) {
      return null;
    }
    out.push({ code, odds });
  }
  return out;
}

const REASONS: Record<string, string> = {
  'cfg:offer.visible': 'hidden in the catalogue',
  'cfg:market.enabled': 'market type switched off',
  'cfg:offer.live_enabled': 'live betting off',
  'cfg:offer.prematch_enabled': 'prematch betting off',
  'trading:close': 'closed by a trader',
  'trading:suspend': 'suspended by a trader',
  producer_down: 'feed producer down',
  'manual:prematch_only': 'manual market, event is live',
  'sanity:margin_floor': 'prices below the margin floor',
};

/** Why a market is restricted, in words (Offer.Core reason codes). */
export function reasonLabel(reason: string): string {
  if (REASONS[reason]) {
    return REASONS[reason];
  }
  return reason.startsWith('feed:') ? `feed: ${reason.slice(5)}` : reason;
}
