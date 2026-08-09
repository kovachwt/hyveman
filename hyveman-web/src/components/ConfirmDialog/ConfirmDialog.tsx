/** Confirmation dialog with optional required reason (FRONTEND.md §8.4). */
import { useState } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  TextField,
} from '@mui/material';
import type { ReactNode } from 'react';

export interface ConfirmDialogProps {
  open: boolean;
  title: string;
  body?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  danger?: boolean;
  /** When set, the dialog requires a non-empty reason before confirming. */
  requireReason?: boolean;
  reasonLabel?: string;
  busy?: boolean;
  onConfirm: (reason: string | undefined) => void;
  onCancel: () => void;
  /** Extra fields rendered after the body/reason (e.g. a silence-until picker). */
  children?: ReactNode;
}

export function ConfirmDialog({
  open,
  title,
  body,
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  danger = false,
  requireReason = false,
  reasonLabel = 'Reason',
  busy = false,
  onConfirm,
  onCancel,
  children,
}: ConfirmDialogProps) {
  const [reason, setReason] = useState('');
  const canConfirm = !requireReason || reason.trim().length > 0;

  return (
    <Dialog open={open} onClose={busy ? undefined : onCancel} maxWidth="sm" fullWidth>
      <DialogTitle>{title}</DialogTitle>
      <DialogContent>
        {body ? <DialogContentText>{body}</DialogContentText> : null}
        {requireReason ? (
          <TextField
            autoFocus
            margin="dense"
            label={reasonLabel}
            fullWidth
            multiline
            minRows={2}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            helperText={requireReason && reason.trim().length === 0 ? 'Required' : undefined}
            required
            disabled={busy}
          />
        ) : null}
        {children}
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel} disabled={busy} color="inherit">
          {cancelLabel}
        </Button>
        <Button
          onClick={() => onConfirm(requireReason ? reason.trim() : undefined)}
          disabled={busy || !canConfirm}
          color={danger ? 'error' : 'primary'}
          variant="contained"
        >
          {confirmLabel}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
