import type { ButtonHTMLAttributes, ReactNode } from 'react';

type ButtonProps = {
  /** Primary filled style (default) vs. subtle secondary style. */
  variant?: 'primary' | 'secondary';
  children: ReactNode;
} & ButtonHTMLAttributes<HTMLButtonElement>;

export function Button({
  variant = 'primary',
  children,
  className = '',
  ...rest
}: ButtonProps) {
  return (
    <button
      className={`btn btn--${variant} ${className}`}
      {...rest}
    >
      {children}
    </button>
  );
}