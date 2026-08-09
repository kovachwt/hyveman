/**
 * Write-only secret input (FRONTEND.md §8.5/§8.6): masked with a reveal
 * toggle, autocomplete off, and an edit-mode hint that blank means
 * "leave the stored value unchanged". Values are cleared after submission by
 * the owning form and never logged or stored by the browser.
 */
import { useState } from 'react';
import { IconButton, InputAdornment, TextField, type TextFieldProps } from '@mui/material';
import Visibility from '@mui/icons-material/Visibility';
import VisibilityOff from '@mui/icons-material/VisibilityOff';

export interface SecretFieldProps extends Omit<TextFieldProps, 'type' | 'slotProps'> {
  /** Edit mode: blank means "leave unchanged" (shown as helper text). */
  editMode?: boolean;
}

export function SecretField({ editMode = false, helperText, ...props }: SecretFieldProps) {
  const [visible, setVisible] = useState(false);
  const hint = editMode ? 'Leave blank to keep the stored value unchanged.' : undefined;
  return (
    <TextField
      {...props}
      type={visible ? 'text' : 'password'}
      autoComplete="new-password"
      helperText={helperText ?? hint}
      slotProps={{
        input: {
          endAdornment: (
            <InputAdornment position="end">
              <IconButton
                aria-label={visible ? 'Hide secret' : 'Show secret'}
                onClick={() => setVisible((v) => !v)}
                onMouseDown={(e) => e.preventDefault()}
                edge="end"
                size="small"
              >
                {visible ? <VisibilityOff fontSize="small" /> : <Visibility fontSize="small" />}
              </IconButton>
            </InputAdornment>
          ),
        },
      }}
    />
  );
}
