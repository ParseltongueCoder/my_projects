/** Tag colours (see StatusTag). */
export type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary';

const SEVERITIES: Record<string, Severity> = {
  // market / event
  active: 'success',
  live: 'success',
  suspended: 'warn',
  deactivated: 'secondary',
  settled: 'info',
  cancelled: 'danger',
  not_started: 'secondary',
  ended: 'info',
  closed: 'secondary',
  abandoned: 'danger',
  postponed: 'warn',
  delayed: 'warn',
  interrupted: 'warn',
  // producers
  up: 'success',
  down: 'danger',
  recovering: 'warn',
  // feed messages
  processed: 'success',
  received: 'info',
  skipped_duplicate: 'secondary',
  skipped_stale: 'secondary',
  failed: 'danger',
  // settlements
  effective: 'success',
  superseded: 'secondary',
  rolled_back: 'warn',
  won: 'success',
  lost: 'secondary',
};

/** Colour for a status value from the canonical model; unknown values are neutral. */
export function severityOf(status: string | null | undefined): Severity {
  return (status && SEVERITIES[status]) || 'secondary';
}

/** "not_started" → "not started". */
export function humanize(status: string | null | undefined): string {
  return (status ?? '').replaceAll('_', ' ');
}
