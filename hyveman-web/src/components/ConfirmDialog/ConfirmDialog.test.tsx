import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConfirmDialog } from './ConfirmDialog';

describe('ConfirmDialog', () => {
  it('confirms without a reason by default', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    const onCancel = vi.fn();
    render(
      <ConfirmDialog open title="Delete host?" body="Really?" confirmLabel="Delete" danger onConfirm={onConfirm} onCancel={onCancel} />,
    );
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    expect(onConfirm).toHaveBeenCalledWith(undefined);
  });

  it('requires a reason when configured', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    render(
      <ConfirmDialog
        open
        title="Acknowledge?"
        requireReason
        reasonLabel="Reason (required for audit)"
        confirmLabel="Acknowledge"
        onConfirm={onConfirm}
        onCancel={() => undefined}
      />,
    );
    const confirm = screen.getByRole('button', { name: 'Acknowledge' });
    expect(confirm).toBeDisabled();
    await user.type(screen.getByLabelText(/Reason/), 'replacing disk');
    expect(confirm).toBeEnabled();
    await user.click(confirm);
    expect(onConfirm).toHaveBeenCalledWith('replacing disk');
  });

  it('blocks confirmation and close while busy', () => {
    const onCancel = vi.fn();
    const onConfirm = vi.fn();
    render(<ConfirmDialog open title="Test" busy onConfirm={onConfirm} onCancel={onCancel} />);
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Confirm' })).toBeDisabled();
    // Clicking a disabled button does nothing.
    onCancel('ignored' as never);
    expect(onConfirm).not.toHaveBeenCalled();
  });
});
