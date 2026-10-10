import type { InputHTMLAttributes, ReactNode } from 'react';
import { Input } from './Input';

type FormFieldProps = {
  label: string;
  htmlFor: string;
  /** Maps onto <Input> (text field) by default. */
  inputProps?: InputHTMLAttributes<HTMLInputElement> & { type?: string };
  children?: ReactNode;
};

/**
 * Small form wrapper: a labelled field. Either pass the `<Input>` through the
 * `children` slot for full control, or `inputProps` for a plain text input.
 */
export function FormField({
  label,
  htmlFor,
  inputProps,
  children,
}: FormFieldProps) {
  return (
    <div className="mb-4">
      <label htmlFor={htmlFor} className="label">
        <span className="label-text text-sm font-medium text-base-content/80">
          {label}
        </span>
      </label>
      {children ?? <Input id={htmlFor} name={htmlFor} {...inputProps} />}
    </div>
  );
}