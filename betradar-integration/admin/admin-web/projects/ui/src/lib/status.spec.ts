import { humanize, severityOf } from './status';
import { formatXml } from './xml';

describe('status helpers', () => {
  it('maps canonical statuses to tag severities', () => {
    expect(severityOf('active')).toBe('success');
    expect(severityOf('suspended')).toBe('warn');
    expect(severityOf('down')).toBe('danger');
    expect(severityOf('something_new')).toBe('secondary');
    expect(severityOf(null)).toBe('secondary');
  });

  it('humanizes snake_case', () => {
    expect(humanize('not_started')).toBe('not started');
    expect(humanize(undefined)).toBe('');
  });
});

describe('formatXml', () => {
  it('indents nested elements and keeps the declaration', () => {
    const xml = '<?xml version="1.0" encoding="utf-8"?><odds_change product="1"><odds><market id="1"/></odds></odds_change>';
    expect(formatXml(xml)).toBe(
      ['<?xml version="1.0" encoding="utf-8"?>', '<odds_change product="1">', '  <odds>', '    <market id="1"/>', '  </odds>', '</odds_change>'].join('\n'),
    );
  });

  it('returns invalid input unchanged', () => {
    expect(formatXml('<broken')).toBe('<broken');
  });
});
