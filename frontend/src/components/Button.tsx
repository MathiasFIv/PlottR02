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
  const variantClass = variant === 'primary' ? 'btn-primary' : '';

  return (
    <button
      className={`btn ${variantClass} ${className}`}
      {...rest}
    >
      {children}
    </button>
  );
}