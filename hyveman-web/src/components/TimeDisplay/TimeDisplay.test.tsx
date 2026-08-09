import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { TimeDisplay } from './TimeDisplay';

const NOW = Date.parse('2025-08-09T14:32:05Z');

describe('TimeDisplay', () => {
  it('renders relative time paired with an absolute timestamp', () => {
    render(<TimeDisplay time="2025-08-09T14:30:00Z" now={NOW} />);
    const el = screen.getByText(/2 minutes ago/);
    // The absolute local time is always present next to the relative label.
    expect(el.textContent).toMatch(/2 minutes ago · \d{1,2}:\d{2}:\d{2}/);
  });

  it('renders a dash for missing times', () => {
    render(<TimeDisplay time={undefined} now={NOW} />);
    expect(screen.getByText('—')).toBeInTheDocument();
  });

  it('shows the full date in full variant', () => {
    render(<TimeDisplay time="2025-08-09T14:30:00Z" now={NOW} variant="full" />);
    expect(screen.getByText(/2025/)).toBeInTheDocument();
  });
});
